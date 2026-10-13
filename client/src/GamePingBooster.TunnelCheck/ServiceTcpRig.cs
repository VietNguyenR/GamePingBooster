using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using GamePingBooster.Service.Native;

namespace GamePingBooster.TunnelCheck;

/// <summary>
/// The service rig for a game that plays over one long TCP connection - Aniimo, roadmap step 2 (persistentSession).
/// `dotnet run --project client/src/GamePingBooster.TunnelCheck -- service tcp`, after the builds ServiceRigScenario.cs
/// lists; the rig must have been brought up with a rig.sh that has the TCP echo on port 20000.
///
/// Two relays, A and B, both carrying the game, no entries, region routing off - so what moves between matches is home
/// itself (RescanBetweenMatchesAsync). B starts 30 ms slow so connect picks A; then A is made 40 ms slow while the game
/// sits on its open connection saying nothing, and B becomes the clearly faster relay.
///
///   1. tcpGameplay WITHOUT persistentSession, the control: the quiet reads as a gap between matches - TCP counting as
///      the match, end to end - the tunnel moves to B, and the move closes the game's connection. This is the break a
///      game like Aniimo must never see.
///   2. The same with persistentSession: the tunnel stays on A, the log says why, and the same connection carries on
///      after the quiet with nothing lost.
///   3. Step 3, routes going out: relay A goes silent under the connection. The reconnect keeps the routes in while A is
///      tried again; A answers and the same connection carries on. Then A stays silent: the routes go, home fails over
///      to B, and the game's next connection plays through B.
///   4. Step 3, routes going in: a second RigGame plays over its own TCP connection to the rig's line server L - over
///      the PC's own line, before Connect - and L is the game's range. Without tcpGameplay (the control) the route
///      going in cuts that connection; with it the connection is kept on the line, its address let go once it closes,
///      and the next connection to L takes the tunnel.
/// </summary>
internal static partial class Program
{
    private static void WriteTcpRigFiles(string rigData, string profile, bool persistentSession)
    {
        File.WriteAllText(profile, $$"""
            {
              "schemaVersion": 1,
              "generatedUtc": "2026-10-13T00:00:00Z",
              "games": [
                {
                  "id": "riggame", "name": "Rig game", "processNames": ["RigGame.exe"],
                  "tcpGameplay": true, "persistentSession": {{(persistentSession ? "true" : "false")}},
                  "regions": [
                    { "id": "sg", "name": "Rig SG", "cidrs": ["198.51.100.10/32"], "landmarks": ["198.51.100.10"] }
                  ]
                }
              ],
              "relays": [
                { "id": "a", "name": "Rig A", "location": "WSL", "endpoint": "{{_a}}", "entrySwitching": "off", "games": ["riggame"] },
                { "id": "b", "name": "Rig B", "location": "WSL", "endpoint": "{{_b}}", "entrySwitching": "off", "games": ["riggame"] }
              ]
            }
            """);
        File.WriteAllText(Path.Combine(rigData, "config.json"), $$"""
            {"profileUrl":null,"profilePath":{{System.Text.Json.JsonSerializer.Serialize(profile)}},
             "psk":"gpb-multi-tunnel-rig-psk-0123456789abcdef","licenceUrl":"","relayEndpoints":[],
             "defaultGameId":"riggame","lastGameId":"riggame","shareQuality":false,
             "regionRouting":"off" }
            """);
    }

    private static async Task RunTcpServiceScenario(ServiceRun run, string rigGameExe, string control, string stats,
        string profile, IPAddress wsl, Action<Process> started)
    {
        if (!await WaitForRigService(run)) return;

        var game = Process.Start(new ProcessStartInfo(rigGameExe, $"\"{control}\" \"{stats}\"") { UseShellExecute = false, CreateNoWindow = true })!;
        started(game);

        // ------------------------------------------------ 1. without persistentSession: the move happens and cuts it
        Console.WriteLine();
        Console.WriteLine("A TCP game without persistentSession (the control):");
        var cut = await TcpQuietSpell(run, control, stats, wsl);
        if (cut is { } c)
        {
            var moved = await run.WaitFor(@"Between matches: moved from Rig A \[a\] to Rig B \[b\][^\n]*", 45_000);
            Check($"The quiet read as a gap between matches - TCP counted as the match - and home moved to B: {moved?.Value.Trim()}",
                moved is not null, Tail(run));
            var closed = Regex.Match(run.Seen, @"Closed (\d+) of (\d+) TCP connection\(s\) that rode the old relay");
            Check($"  the move closed the game's connection ({closed.Value})",
                closed.Success && int.Parse(closed.Groups[1].Value) >= 1, Tail(run));
            File.WriteAllText(control, "tcp");
            await Task.Delay(5_000);
            var after = GameStats(stats, "tcp");
            Check($"  the game saw its connection break ({after.Breaks - c.Breaks} break(s)), and a new one plays through B " +
                  $"({after.Back - c.Back} echoed since)",
                after.Breaks > c.Breaks && after.Back > c.Back, $"{after}");
        }
        File.WriteAllText(control, "quiet");
        await run.Send("disconnect");
        Check("Disconnected", await run.WaitFor(@"Disconnect took", 30_000) is not null, Tail(run));

        // ------------------------------------------------ 2. with persistentSession: the tunnel stays, the connection lives
        Console.WriteLine();
        Console.WriteLine("The same game with persistentSession:");
        WriteTcpRigFiles(Path.GetDirectoryName(profile)!, profile, persistentSession: true);
        var fromHere = run.Seen.Length;
        var held = await TcpQuietSpell(run, control, stats, wsl);
        if (held is { } h)
        {
            var said = await run.WaitFor(@"Rig game holds one session for as long as it is open[^\n]*", 30_000);
            Check($"The quiet is not a gap: the log says the session is held - {said?.Value.Trim()}", said is not null, Tail(run));
            // Past where the control had moved by: the gap came 10 s into the quiet, the move a few seconds after.
            await Task.Delay(15_000);
            run.Read();
            var since = run.Seen[fromHere..];
            Check("  home stayed on A: no move, no relay re-measured", !Regex.IsMatch(since, @"moved from Rig A|re-measuring the relays"),
                Tail(run));
            Check("  and A is still the only relay pinned", PinnedVia(wsl) is var pins && pins.Contains(_a.Address.ToString()) &&
                !pins.Contains(_b.Address.ToString()), string.Join(", ", PinnedVia(wsl)));
            File.WriteAllText(control, "tcp");
            await Task.Delay(5_000);
            var after = GameStats(stats, "tcp");
            Check($"  the same connection carried on after the quiet: no break, nothing lost ({after.Back - h.Back} echoed since)",
                after.Breaks == h.Breaks && after.Lost == h.Lost && after.Back > h.Back, $"{after} against {h}");
        }

        // ------------------------------------------------ 3. a reconnect keeps a TCP game's routes while A may come back
        if (held is not null) await TcpFailoverHold(run, control, stats);

        File.WriteAllText(control, "quiet");
        RigShell("heal");
        await run.Send("disconnect");
        Check("Disconnected", await run.WaitFor(@"Disconnect took", 30_000) is not null, Tail(run));

        // ------------------------------------------------ 4. no route goes in under a connection open on the line
        var line = IPAddress.Parse(Regex.Match(RigShell("status"), @"L=(\S+)").Groups[1].Value);
        foreach (var tcpGameplay in new[] { false, true })
        {
            await LineHoldRun(run, rigGameExe, profile, line, tcpGameplay);
        }

        await Task.Delay(1_000);
        var left = PinnedVia(wsl).Where(p => new[] { _a, _b }.Any(x => x.Address.ToString() == p)).ToList();
        Check("Every pin removed", left.Count == 0, string.Join(", ", left));
        var lineLeft = IpHelper.ReadRoutes()
            .Where(r => r.DestinationPrefixLength == 32 && new IPAddress(r.DestinationAddress).Equals(line))
            .Select(r => $"{new IPAddress(r.DestinationAddress)} on interface {r.InterfaceIndex}").ToList();
        Check("  and no route to the line server is left", lineLeft.Count == 0, string.Join(", ", lineLeft));

        run.Read();
        var errors = Regex.Matches(run.Seen, @"(?im)^.*(exception|hit an error|collapsed|could not move|could not pin).*$");
        Check("No errors in the service log", errors.Count == 0, string.Join(" | ", errors.Select(m => m.Value.Trim()).Take(5)));
    }

    /// <summary>
    /// Step 3, routes going out. On a tunnel to A with the game playing over its TCP connection: A's replies are dropped
    /// until the reconnect has started with the routes held, then let through - the same connection must carry on. Then
    /// dropped for good: the routes must go after the held try, home fail over to B, and a new connection play there.
    /// </summary>
    private static async Task TcpFailoverHold(ServiceRun run, string control, string stats)
    {
        Console.WriteLine();
        Console.WriteLine("A reconnect under the game's TCP connection (step 3):");
        RigShell("heal");
        File.WriteAllText(control, "tcp");
        await Task.Delay(5_000);
        var before = GameStats(stats, "tcp");
        var fromHere = run.Seen.Length;

        RigShell("degrade a 0 100");
        var holding = await run.WaitFor(@"Tunnel is down while Rig game has \d+ TCP connection\(s\) through it[^\n]*", 25_000);
        RigShell("heal");
        Check($"A went silent: the reconnect came within the TCP timeout and kept the routes - {holding?.Value.Trim()}",
            holding is not null, Tail(run));
        var back = await run.WaitFor(@"Back on Rig A with the routes never out[^\n]*", 20_000);
        Check($"  A answered again: {back?.Value.Trim()}", back is not null, Tail(run));
        await Task.Delay(5_000);
        run.Read();
        var since = run.Seen[fromHere..];
        Check("  no game or lobby route was pulled", !Regex.IsMatch(since, @"removing (game|lobby) routes"), Tail(run));
        var after = GameStats(stats, "tcp");
        Check($"  the same connection carried on: no break ({after.Back - before.Back} echoed since)",
            after.Breaks == before.Breaks && after.Back > before.Back + 100, $"{after} against {before}");

        Console.WriteLine("  ...and when A does not come back:");
        fromHere = run.Seen.Length;
        RigShell("degrade a 0 100");
        Check("  the reconnect holds first", await run.WaitFor(@"Tunnel is down while Rig game has", 25_000) is not null, Tail(run));
        var gaveUp = await run.WaitFor(@"Rig A did not answer - the TCP connections through it cannot be kept", 30_000);
        Check("  then gives the connections up when A stays silent", gaveUp is not null, Tail(run));
        var failedOver = await run.WaitFor(@"failing over to Rig B", 45_000);
        Check("  and fails over to B as for any other game", failedOver is not null, Tail(run));
        run.Read();
        Check("  with the routes pulled before it did", Regex.IsMatch(run.Seen[fromHere..], @"removing game routes"), Tail(run));
        // Not at once: B gave another inner IP, and Windows may refuse to close the old connection (error 317 on the
        // 2026-10-13 run) - it then breaks when its own retransmissions run out, about 19 s after A went silent.
        var onB = GameStats(stats, "tcp");
        for (var waited = 0; waited < 30 && !(onB.Breaks > after.Breaks && onB.Back > after.Back + 100); waited++)
        {
            await Task.Delay(1_000);
            onB = GameStats(stats, "tcp");
        }
        Check($"  the game's connection broke ({onB.Breaks - after.Breaks}) and a new one plays through B " +
              $"({onB.Back - after.Back} echoed)",
            onB.Breaks > after.Breaks && onB.Back > after.Back + 100, $"{onB} against {after}");
        RigShell("heal");
    }

    /// <summary>
    /// Step 3, routes going in. A second RigGame plays to the line server L over the PC's own line before Connect; L is
    /// the game's whole range. <paramref name="tcpGameplay"/> false is the control: the route must cut the connection.
    /// </summary>
    private static async Task LineHoldRun(ServiceRun run, string rigGameExe, string profile, IPAddress line, bool tcpGameplay)
    {
        Console.WriteLine();
        Console.WriteLine(tcpGameplay
            ? "A TCP game's connection open on the line before its routes go in (step 3):"
            : "The same connection for a game without tcpGameplay (the control):");
        WriteLineRigProfile(profile, line, tcpGameplay);
        var dir = Path.GetDirectoryName(profile)!;
        var control = Path.Combine(dir, "riggame-line-control.txt");
        var stats = Path.Combine(dir, $"riggame-line-stats-{tcpGameplay}.csv");
        File.WriteAllText(control, "tcp");
        using var game = Process.Start(new ProcessStartInfo(rigGameExe, $"\"{control}\" \"{stats}\" {line}")
            { UseShellExecute = false, CreateNoWindow = true })!;

        // The address the game's connection to L was opened from: the PC's own on the line, a relay's
        // pool (10.77.x on A, 10.78.x on B) through the tunnel.
        string? OpenedFrom() => TcpConnections.OfProcesses(new HashSet<uint> { (uint)game.Id })
            .Where(r => r.Remote.Address.Equals(line) && r.StateName == "Established")
            .Select(r => r.Local.ToString()).FirstOrDefault();
        try
        {
            await Task.Delay(4_000);
            var before = GameStats(stats, "tcp");
            var from = OpenedFrom();
            Check($"  RigGame plays to L over the line before Connect ({before.Back} echoed, from {from})",
                before.Back > 50 && before.Breaks == 0 && from is not null && !ThroughRigTunnel(from),
                $"{before}");

            var fromHere = run.Seen.Length;
            await run.Send("connect", "riggame");
            if (await run.WaitFor(@"Connect took", 60_000) is null)
            {
                Check("Connected", false, Tail(run));
                return;
            }
            await Task.Delay(8_000);
            var after = GameStats(stats, "tcp");
            run.Read();
            var since = run.Seen[fromHere..];
            if (!tcpGameplay)
            {
                // The 2026-10-13 run: it was NOT cut. Windows sends by the strong host model - a connection keeps the
                // interface that holds its source address, whatever route goes in later - so this records that, as the
                // ground the line hold stands on. Should it ever break here, the line hold is what keeps Aniimo alive.
                var still = OpenedFrom();
                Check($"  the route went in, but Windows kept the open connection on its own interface - strong host send " +
                      $"({after.Breaks - before.Breaks} break(s), {after.Back - before.Back} echoed, from {still})",
                    after.Breaks == before.Breaks && after.Back > before.Back + 200 && still == from, $"{after} against {before}");
            }
            else
            {
                var kept = Regex.Match(since, @"Rig game had \d+ TCP connection address\(es\) open on your own line[^\n]*");
                Check($"  the connection was kept on the line: {kept.Value.Trim()}", kept.Success, Tail(run));
                var still = OpenedFrom();
                Check($"  and carried on there: no break ({after.Back - before.Back} echoed since, from {still})",
                    after.Breaks == before.Breaks && after.Back > before.Back + 200 && still == from, $"{after} against {before}");

                File.WriteAllText(control, "quiet");
                // The log seals from the address to the end of the line, so nothing after it can be matched.
                var released = await run.WaitFor(@"Nothing is connected to (\[enc:|\S+ on your own line any more)", 20_000);
                Check("  closed, its address was let go", released is not null, Tail(run));
                File.WriteAllText(control, "tcp");
                await Task.Delay(4_000);
                var next = GameStats(stats, "tcp");
                var through = OpenedFrom();
                Check($"  and the next connection to L takes the tunnel (from {through}, {next.Back - after.Back} echoed)",
                    through is not null && ThroughRigTunnel(through) && next.Back > after.Back + 50,
                    $"{next}");
            }
        }
        finally
        {
            File.WriteAllText(control, "exit");
            if (!game.WaitForExit(3_000)) game.Kill();
        }
        await run.Send("disconnect");
        Check("Disconnected", await run.WaitFor(@"Disconnect took", 30_000) is not null, Tail(run));
    }

    /// <summary>Whether a connection was opened from a rig relay's address pool - A's 10.77.0.0/24 or B's 10.78.0.0/24.</summary>
    private static bool ThroughRigTunnel(string local) =>
        local.StartsWith("10.77.", StringComparison.Ordinal) || local.StartsWith("10.78.", StringComparison.Ordinal);

    /// <summary>The TCP rig's profile with the line server L as the game's only range and landmark.</summary>
    private static void WriteLineRigProfile(string profile, IPAddress line, bool tcpGameplay) =>
        File.WriteAllText(profile, $$"""
            {
              "schemaVersion": 1,
              "generatedUtc": "2026-10-13T00:00:00Z",
              "games": [
                {
                  "id": "riggame", "name": "Rig game", "processNames": ["RigGame.exe"],
                  "tcpGameplay": {{(tcpGameplay ? "true" : "false")}}, "persistentSession": true,
                  "regions": [
                    { "id": "sg", "name": "Rig SG", "cidrs": ["{{line}}/32"], "landmarks": ["{{line}}"] }
                  ]
                }
              ],
              "relays": [
                { "id": "a", "name": "Rig A", "location": "WSL", "endpoint": "{{_a}}", "entrySwitching": "off", "games": ["riggame"] },
                { "id": "b", "name": "Rig B", "location": "WSL", "endpoint": "{{_b}}", "entrySwitching": "off", "games": ["riggame"] }
              ]
            }
            """);

    /// <summary>
    /// Connects with B 30 ms slow so home is A, plays 15 s over the TCP connection, then makes A the slow one and leaves
    /// the connection open and silent. The game's TCP stats at the start of the quiet, or null when a step failed.
    /// </summary>
    private static async Task<(int Sent, int Back, int Lost, int GapMs, int Breaks)?> TcpQuietSpell(ServiceRun run,
        string control, string stats, IPAddress wsl)
    {
        RigShell("heal");
        RigShell("degrade b 30");
        File.WriteAllText(control, "quiet");
        await run.Send("connect", "riggame");
        if (await run.WaitFor(@"Connect took", 60_000) is null)
        {
            Check("Connected", false, Tail(run));
            return null;
        }
        var pins = PinnedVia(wsl);
        Check($"Connected, home on Rig A - B was 30 ms slow ({string.Join(", ", pins)})",
            pins.Contains(_a.Address.ToString()) && !pins.Contains(_b.Address.ToString()));

        var before = GameStats(stats, "tcp");
        File.WriteAllText(control, "tcp");
        await Task.Delay(15_000);
        var playing = GameStats(stats, "tcp");
        var ok = playing.Back - before.Back > 300 && playing.Breaks == before.Breaks;
        Check($"  15 s of play over one TCP connection through the tunnel ({playing.Back - before.Back} echoed, " +
              $"{playing.Breaks - before.Breaks} break(s))", ok, $"{playing}");
        if (!ok) return null;

        RigShell("heal");
        RigShell("degrade a 40");
        File.WriteAllText(control, "tcp-idle");
        await Task.Delay(1_000);
        return GameStats(stats, "tcp");
    }
}
