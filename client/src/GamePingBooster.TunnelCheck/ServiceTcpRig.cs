using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;

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

        File.WriteAllText(control, "quiet");
        RigShell("heal");
        await run.Send("disconnect");
        Check("Disconnected", await run.WaitFor(@"Disconnect took", 30_000) is not null, Tail(run));
        await Task.Delay(1_000);
        var left = PinnedVia(wsl).Where(p => new[] { _a, _b }.Any(x => x.Address.ToString() == p)).ToList();
        Check("Every pin removed", left.Count == 0, string.Join(", ", left));

        run.Read();
        var errors = Regex.Matches(run.Seen, @"(?im)^.*(exception|hit an error|collapsed|could not move|could not pin).*$");
        Check("No errors in the service log", errors.Count == 0, string.Join(" | ", errors.Select(m => m.Value.Trim()).Take(5)));
    }

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
