using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using GamePingBooster.Service.Native;

namespace GamePingBooster.TunnelCheck;

/// <summary>
/// The whole service against the WSL rig: a real Wintun adapter, real routes and pins, the real supervisor, region
/// plan and recorder - driven as the app drives it, over the pipe, with RigGame.exe standing in for the game and netem
/// making one way into a relay bad at a time. It checks what only the service does: which tunnel a decision moves,
/// what gets pinned and unpinned, the move back when a new way goes silent, and a region's tunnel changing its way in
/// between matches.
///
///     wsl -d Ubuntu -u root -- bash tools/multi-tunnel-rig/rig.sh up
///     dotnet build client/src/GamePingBooster.Service -o client/src/GamePingBooster.Service/bin/rig
///     dotnet build tools/multi-tunnel-rig/RigGame -o tools/multi-tunnel-rig/RigGame/bin/rig
///     dotnet run --project client/src/GamePingBooster.TunnelCheck -- service
///
/// Asks for Administrator once (Run-ServiceRig.ps1, for LocalSystem and one route) and puts the PC back afterwards:
/// %ProgramData%\GamePingBooster moved aside and restored, the route and every pin removed. Close the app first - a UI
/// attached to the pipe would push its own token and profiles into the rig's service.
/// </summary>
internal static partial class Program
{
    private sealed class ServiceRun : IDisposable
    {
        private readonly string _logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "GamePingBooster", "logs", "gpb-service.log");
        private long _offset;
        private readonly StringBuilder _seen = new();
        private NamedPipeClientStream? _pipe;
        private StreamWriter? _writer;

        public string Seen => _seen.ToString();

        /// <summary>Everything the service logged since the last call.</summary>
        public string Read()
        {
            try
            {
                using var stream = new FileStream(_logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length < _offset) _offset = 0;
                stream.Seek(_offset, SeekOrigin.Begin);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                var text = reader.ReadToEnd();
                _offset = stream.Position;
                _seen.Append(text);
                return text;
            }
            catch (IOException)
            {
                return "";
            }
        }

        /// <summary>Waits for a line matching <paramref name="pattern"/> logged from now on. The match, or null.</summary>
        public async Task<Match?> WaitFor(string pattern, int timeoutMs)
        {
            var regex = new Regex(pattern);
            var from = _seen.Length;
            var deadline = Environment.TickCount64 + timeoutMs;
            while (Environment.TickCount64 < deadline)
            {
                Read();
                var m = regex.Match(_seen.ToString(from, _seen.Length - from));
                if (m.Success) return m;
                await Task.Delay(200);
            }
            return null;
        }

        public void SkipToEnd() => Read();

        public async Task Send(string verb, string? gameId = null)
        {
            if (_pipe is null)
            {
                var pipe = new NamedPipeClientStream(".", "GamePingBooster", PipeDirection.InOut, PipeOptions.Asynchronous);
                try
                {
                    await pipe.ConnectAsync(2_000);
                }
                catch
                {
                    pipe.Dispose();
                    throw;
                }
                _pipe = pipe;
                _writer = new StreamWriter(_pipe, new UTF8Encoding(false)) { AutoFlush = true };
                // Drain the service's pushes so its writes never block on us.
                _ = Task.Run(async () =>
                {
                    var buffer = new byte[65536];
                    try { while (await _pipe.ReadAsync(buffer) > 0) { } } catch (IOException) { } catch (ObjectDisposedException) { }
                });
            }
            await _writer!.WriteLineAsync(gameId is null
                ? $"{{\"v\":2,\"verb\":\"{verb}\"}}"
                : $"{{\"v\":2,\"verb\":\"{verb}\",\"gameId\":\"{gameId}\"}}");
        }

        public void Dispose() => _pipe?.Dispose();
    }

    /// <summary>The /32s pinned to <paramref name="nextHop"/> right now, by address.</summary>
    private static HashSet<string> PinnedVia(IPAddress nextHop)
    {
        var hop = IpHelper.ToInAddr(nextHop);
        return IpHelper.ReadRoutes()
            .Where(r => r.DestinationPrefixLength == 32 && r.NextHopAddress == hop)
            .Select(r => new IPAddress(r.DestinationAddress).ToString())
            .ToHashSet();
    }

    private static int ServiceMain(string[] args)
    {
        var repo = FindRepo();
        _rigScript = Path.Combine(repo, "tools", "multi-tunnel-rig", "rig.sh");
        var serviceExe = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(repo, "client", "src", "GamePingBooster.Service", "bin", "rig", "gpb-service.exe");
        var rigGameExe = args.Length > 1 ? Path.GetFullPath(args[1]) : Path.Combine(repo, "tools", "multi-tunnel-rig", "RigGame", "bin", "rig", "RigGame.exe");
        if (!File.Exists(serviceExe) || !File.Exists(rigGameExe))
        {
            Console.WriteLine($"Build first: {serviceExe} and {rigGameExe}.");
            return 2;
        }
        if (Process.GetProcessesByName("GamePingBooster").Length > 0)
        {
            Console.WriteLine("Close the Game Ping Booster app first: attached to the pipe, it would push its own token and profiles into the rig's service.");
            return 2;
        }

        var status = RigShell("status");
        var a = Regex.Match(status, @"A=(\S+):51820");
        if (!a.Success || !status.Contains("relayd running: 2"))
        {
            Console.WriteLine("The rig is not up. Run: wsl -d Ubuntu -u root -- bash tools/multi-tunnel-rig/rig.sh up");
            return 2;
        }
        _a = new IPEndPoint(IPAddress.Parse(a.Groups[1].Value), 51820);
        _b = new IPEndPoint(IPAddress.Parse(Regex.Match(status, @"B=(\S+):51820").Groups[1].Value), 51820);
        _e = new IPEndPoint(IPAddress.Parse(Regex.Match(status, @"E=(\S+):51820").Groups[1].Value), 51820);
        _f = new IPEndPoint(IPAddress.Parse(Regex.Match(status, @"F=(\S+):51820").Groups[1].Value), 51820);
        var wslIp = WslAddress();

        var rigData = Path.Combine(Path.GetTempPath(), "gpb-service-rig");
        if (Directory.Exists(rigData)) Directory.Delete(rigData, recursive: true);
        Directory.CreateDirectory(rigData);
        var profile = Path.Combine(rigData, "rig-profile.json");
        File.WriteAllText(profile, $$"""
            {
              "schemaVersion": 1,
              "generatedUtc": "2026-09-30T00:00:00Z",
              "games": [
                {
                  "id": "riggame", "name": "Rig game", "processNames": ["RigGame.exe"], "regionRouting": "on",
                  "regions": [
                    { "id": "sg", "name": "Rig SG", "cidrs": ["198.51.100.10/32"], "landmarks": ["198.51.100.10"] },
                    { "id": "kr", "name": "Rig KR", "cidrs": ["198.51.100.20/32"], "landmarks": ["198.51.100.20"] }
                  ]
                }
              ],
              "relays": [
                { "id": "a", "name": "Rig A", "location": "WSL", "endpoint": "{{_a}}", "entrySwitching": "on", "games": ["riggame"],
                  "entries": [ { "id": "f", "location": "WSL", "endpoint": "{{_f}}" } ] },
                { "id": "b", "name": "Rig B", "location": "WSL", "endpoint": "{{_b}}", "entrySwitching": "on", "games": ["not-riggame"], "secondaryGames": ["riggame"],
                  "entries": [ { "id": "e", "location": "WSL", "endpoint": "{{_e}}" } ] }
              ]
            }
            """);
        File.WriteAllText(Path.Combine(rigData, "config.json"), $$"""
            {"profileUrl":null,"profilePath":{{System.Text.Json.JsonSerializer.Serialize(profile)}},
             "psk":"gpb-multi-tunnel-rig-psk-0123456789abcdef","licenceUrl":"","relayEndpoints":[],
             "defaultGameId":"riggame","lastGameId":"riggame","shareQuality":false,
             "regionRouting":"on","regionRoutingForce":{"kr":"b"} }
            """);

        Console.WriteLine($"Rig: A {_a} (home), F {_f}; B {_b} (kr), E {_e}. Service {serviceExe}.");
        Console.WriteLine("Asking for Administrator once: LocalSystem for the service, and one route.");
        var runner = Path.Combine(repo, "tools", "multi-tunnel-rig", "Run-ServiceRig.ps1");
        Process.Start(new ProcessStartInfo("powershell.exe",
            $"-NoProfile -ExecutionPolicy Bypass -File \"{runner}\" -ServiceExe \"{serviceExe}\" -RigData \"{rigData}\" -WslIp {wslIp}")
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        });

        var control = Path.Combine(rigData, "riggame-control.txt");
        var stats = Path.Combine(rigData, "riggame-stats.csv");
        File.WriteAllText(control, "quiet");
        Process? game = null;
        try
        {
            using var run = new ServiceRun();
            RigShell("heal");
            RunServiceScenario(run, rigGameExe, control, stats, IPAddress.Parse(wslIp), g => game = g).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _failures++;
            Console.WriteLine($"  FAIL  the run threw: {ex}");
        }
        finally
        {
            File.WriteAllText(control, "exit");
            try { game?.WaitForExit(3000); game?.Kill(); } catch (InvalidOperationException) { }
            RigShell("heal");
            File.WriteAllText(Path.Combine(rigData, "stop"), "");
            var restored = false;
            for (var i = 0; i < 120 && !restored; i++)
            {
                Thread.Sleep(500);
                restored = File.Exists(Path.Combine(rigData, "elevated.log")) && File.ReadAllText(Path.Combine(rigData, "elevated.log")).Contains(" done");
            }
            Console.WriteLine();
            Console.WriteLine(restored ? "The PC is back as it was (see elevated.log)." : "WARNING: the elevated runner did not report 'done' - check elevated.log.");
            if (File.Exists(Path.Combine(rigData, "elevated.log"))) Console.WriteLine(File.ReadAllText(Path.Combine(rigData, "elevated.log")));
        }

        Console.WriteLine();
        Console.WriteLine(_failures == 0 ? "All service rig checks passed." : $"{_failures} service rig check(s) FAILED. Logs in {rigData}.");
        return _failures == 0 ? 0 : 1;
    }

    private static string WslAddress()
    {
        var output = RigShell("ip");
        var m = Regex.Match(output, @"eth0 (\d+\.\d+\.\d+\.\d+)");
        return m.Success ? m.Groups[1].Value : throw new InvalidOperationException("rig.sh ip gave no eth0 address: " + output);
    }

    /// <summary>The RigGame stats file's last line for one server: sent, back, lost (older than 2 s), longest silence.</summary>
    private static (int Sent, int Back, int Lost, int GapMs) GameStats(string stats, string server)
    {
        try
        {
            // RigGame holds the file open for writing: read it sharing that, or the read fails.
            using var stream = new FileStream(stats, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var line = reader.ReadToEnd().Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Split(',') is { Length: 7 } p && p[2] == server);
            if (line is null) return (0, 0, 0, 0);
            var p = line.Split(',');
            return (int.Parse(p[3]), int.Parse(p[4]), int.Parse(p[5]), int.Parse(p[6]));
        }
        catch (IOException)
        {
            return (0, 0, 0, 0);
        }
    }

    private static async Task RunServiceScenario(ServiceRun run, string rigGameExe, string control, string stats, IPAddress wsl,
        Action<Process> started)
    {
        // The service's pipe: the elevated runner has to be accepted first, then the service must be the rig's.
        var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "GamePingBooster");
        var pipeUp = false;
        for (var i = 0; i < 240 && !pipeUp; i++)
        {
            await Task.Delay(500);
            try
            {
                if (!File.ReadAllText(Path.Combine(data, "config.json")).Contains("rig-profile")) continue;
                await run.Send("status");
                pipeUp = true;
            }
            catch (IOException) { }
            catch (TimeoutException) { }
            catch (UnauthorizedAccessException) { }
        }
        Check("The rig's service is up (LocalSystem, the rig's config)", pipeUp);
        if (!pipeUp) return;
        run.SkipToEnd();

        // B must open on its direct road, so the entry starts out 20 ms slower.
        RigShell("degrade e 20");

        var game = Process.Start(new ProcessStartInfo(rigGameExe, $"\"{control}\" \"{stats}\"") { UseShellExecute = false, CreateNoWindow = true })!;
        started(game);
        await run.Send("connect", "riggame");

        Console.WriteLine();
        Console.WriteLine("Connect and plan:");
        Check("Connected, home on Rig A", await run.WaitFor(@"Connect took", 60_000) is not null, Tail(run));
        var pins = PinnedVia(wsl);
        Check($"Pinned after connect: home A and its entry F ({string.Join(", ", pins)})",
            pins.Contains(_a.Address.ToString()) && pins.Contains(_f.Address.ToString()));

        var inForce = await run.WaitFor(@"Region routing in force: .*kr -> Rig B \[b\]", 60_000);
        Check("The plan sends kr to Rig B, opened on its direct road b", inForce is not null, Tail(run));
        var announced = await run.WaitFor(@"Entry switching \(on, .*\): Rig B \(a region's tunnel\) has 2 ways in \(b, e\)", 5_000)
                        ?? (Regex.IsMatch(run.Seen, @"Rig B \(a region's tunnel\) has 2 ways in") ? Match.Empty : null);
        Check("Entry switching is on for Rig B's tunnel, both ways named", announced is not null, Tail(run));
        pins = PinnedVia(wsl);
        Check($"Pinned with B's tunnel open: A, F, B and E ({string.Join(", ", pins)})",
            new[] { _a, _f, _b, _e }.All(x => pins.Contains(x.Address.ToString())));

        // ------------------------------------------------ a match on kr, B's direct road goes bad
        Console.WriteLine();
        Console.WriteLine("A match on kr, Rig B's direct road 80 ms slow:");
        RigShell("heal");
        File.WriteAllText(control, "kr");
        Check("The match is on Rig B's tunnel", await run.WaitFor(@"The match is on Rig B \[b\], not home", 30_000) is not null, Tail(run));
        await Task.Delay(5_000);
        var krBefore = GameStats(stats, "kr");
        RigShell("degrade b 80");
        var moved = await run.WaitFor(@"Entry switching \(the switch policy\): moved the tunnel to Rig B \[b\] onto .*\[e\]", 70_000);
        Check("Rig B's tunnel moved onto e by its own switch policy, mid-match", moved is not null, Tail(run));
        await Task.Delay(6_000);
        var krAfter = GameStats(stats, "kr");
        Check($"  Korea lost nothing across the move ({krAfter.Sent - krBefore.Sent} sent meanwhile, {krAfter.Lost} lost in all)",
            krAfter.Lost == 0 && krAfter.Sent > krBefore.Sent, $"{krAfter}");
        Check("  home was not moved, and no region went home", !Regex.IsMatch(run.Seen, @"moved from Rig A") &&
            !Regex.IsMatch(run.Seen, @"Rig B.*has been silent .* go\s*back to home"));
        Check("  the pins are unchanged by the move (both ways of B were pinned already)",
            new[] { _a, _f, _b, _e }.All(x => PinnedVia(wsl).Contains(x.Address.ToString())));

        // ------------------------------------------------ between matches: back to b, then b dies right after
        Console.WriteLine();
        Console.WriteLine("Between matches: the entry slow now, the road healed - and then the road dies:");
        RigShell("heal");
        RigShell("degrade e 80");
        File.WriteAllText(control, "quiet");
        var replan = await run.WaitFor(@"Between matches: the game has been silent \d+ s - planning", 40_000);
        Check("The match ended; the regions are planned again", replan is not null, Tail(run));
        var betweenMove = await run.WaitFor(@"Entry switching \(between matches\): moved the tunnel to Rig B[^\[]*\[e\] onto .*\[b\]", 40_000);
        Check("Rig B's idle tunnel measured by Probe and moved back onto b before the next match", betweenMove is not null, Tail(run));
        if (betweenMove is not null)
        {
            RigShell("degrade b 0 100");
            var back = await run.WaitFor(@"Entry switching: the tunnel to b has heard nothing for \d+ s since its move - going back to e", 25_000);
            Check("The road died right after the move: the tunnel went back to e within seconds", back is not null, Tail(run));
            Check("  and was not given up: its region stayed on it", !Regex.IsMatch(run.Seen, @"Rig B[^\[]*\[.\] has been silent"));
        }
        RigShell("heal");
        File.WriteAllText(control, "kr");
        await Task.Delay(8_000);
        var krNext = GameStats(stats, "kr");
        Check($"The next match on kr plays clean ({krNext.Sent - krAfter.Sent} sent, {krNext.Lost - krAfter.Lost} lost)",
            krNext.Lost == krAfter.Lost && krNext.Sent > krAfter.Sent, $"{krNext}");

        // ------------------------------------------------ home still moves itself
        Console.WriteLine();
        Console.WriteLine("A match on sg (home), Rig A's direct road 80 ms slow:");
        File.WriteAllText(control, "sg");
        await Task.Delay(12_000);
        var sgBefore = GameStats(stats, "sg");
        RigShell("degrade a 80");
        var homeMoved = await run.WaitFor(@"Entry switching: moved from Rig A \[a\] to .*\[f\]", 75_000);
        Check("Home moved onto f by its own policy - not held back by the moves on Rig B's tunnel", homeMoved is not null, Tail(run));
        await Task.Delay(5_000);
        var sgAfter = GameStats(stats, "sg");
        Check($"  sg lost nothing across home's move ({sgAfter.Sent - sgBefore.Sent} sent, {sgAfter.Lost - sgBefore.Lost} lost)",
            sgAfter.Lost == sgBefore.Lost && sgAfter.Sent > sgBefore.Sent, $"{sgAfter}");

        // ------------------------------------------------ disconnect: every pin goes
        Console.WriteLine();
        Console.WriteLine("Disconnect:");
        RigShell("heal");
        File.WriteAllText(control, "quiet");
        await run.Send("disconnect");
        Check("Disconnected", await run.WaitFor(@"Disconnect took", 30_000) is not null, Tail(run));
        await Task.Delay(1_000);
        var left = PinnedVia(wsl).Where(p => new[] { _a, _b, _e, _f }.Any(x => x.Address.ToString() == p)).ToList();
        Check("Every pin of every relay and entry removed", left.Count == 0, string.Join(", ", left));

        run.Read();
        var errors = Regex.Matches(run.Seen, @"(?im)^.*(exception|hit an error|collapsed|could not move|could not pin).*$");
        Check("No errors in the service log", errors.Count == 0, string.Join(" | ", errors.Select(m => m.Value.Trim()).Take(5)));
    }

    private static string Tail(ServiceRun run)
    {
        run.Read();
        var lines = run.Seen.Split('\n');
        return "\n      " + string.Join("\n      ", lines.TakeLast(12).Select(l => l.TrimEnd()));
    }
}
