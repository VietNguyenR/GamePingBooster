using GamePingBooster.Core.Profiles;
using GamePingBooster.Service.Network;
using GamePingBooster.Service.Tunnel;

namespace GamePingBooster.TunnelCheck;

/// <summary>
/// The two fixes agreed on 2026-10-22 (game-relay-move-handoff.md), offline:
///
///   1. A licence that runs out while connected ends the connection (TunnelEngine.LicenceEnd) - the rule, case by case.
///   3. With several games open the one opened last is routed for (GameProcessWatcher.Look), driven with process lists
///      of its own: the LoL client left open used to win over PUBG by profile order, so PUBG got no routes and no move.
/// </summary>
internal static partial class Program
{
    private static Task TheGameOpenedLastIsRoutedFor()
    {
        // Profile order is by code, a -> z: Delta Force and LoL sort before PUBG, which is how they used to win.
        GameEntry Game(string id, string name, params string[] processes) => new() { Id = id, Name = name, ProcessNames = [.. processes] };
        var deltaForce = Game("deltaforce", "Delta Force", "DeltaForceClient-Win64-Shipping.exe");
        var lol = Game("lol", "League of Legends", "LeagueClient.exe", "LeagueClientUx.exe", "League of Legends.exe");
        var pubg = Game("pubg", "PUBG", "TslGame_BE.exe", "TslGame.exe");
        GameEntry[] games = [deltaForce, lol, pubg];

        var inMatch = false;
        var log = new List<string>();
        using var watcher = new GameProcessWatcher(games, () => inMatch, log.Add);
        var events = new List<string>();
        watcher.GameStateChanged += (running, process) => events.Add(running ? process! : "-");
        HashSet<string> Running(params string[] names) => new(names, StringComparer.OrdinalIgnoreCase);
        string Names(IReadOnlyList<GameEntry>? list) => list is null ? "null" : string.Join(",", list.Select(g => g.Id));

        watcher.Look(Running("LeagueClientUx"));
        Check("The LoL client alone is routed for", watcher.RunningProcessName == "LeagueClientUx" && events.SequenceEqual(["LeagueClientUx"]),
            $"{watcher.RunningProcessName}, events {string.Join(" ", events)}");
        Check("  and one game open says nothing", watcher.SeveralGamesOpen is null, Names(watcher.SeveralGamesOpen));

        watcher.Look(Running("LeagueClientUx", "TslGame_BE"));
        Check("PUBG started after the LoL client takes the routes", watcher.RunningProcessName == "TslGame_BE" && events.Count == 2,
            $"{watcher.RunningProcessName}, events {string.Join(" ", events)}");
        Check("  and both are named for the warning, oldest first", Names(watcher.SeveralGamesOpen) == "lol,pubg", Names(watcher.SeveralGamesOpen));
        Check("  and the log says so once", log.Count(l => l.StartsWith("Several games are open")) == 1, string.Join(" | ", log));

        watcher.Look(Running("LeagueClientUx", "TslGame_BE", "TslGame"));
        watcher.Look(Running("LeagueClient", "TslGame_BE", "TslGame"));
        Check("BattlEye handing over and a LoL helper restarting change nothing", watcher.RunningProcessName == "TslGame_BE" && events.Count == 2,
            $"{watcher.RunningProcessName}, events {string.Join(" ", events)}");

        inMatch = true;
        watcher.Look(Running("LeagueClient", "TslGame_BE", "TslGame", "DeltaForceClient-Win64-Shipping"));
        watcher.Look(Running("LeagueClient", "TslGame_BE", "TslGame", "DeltaForceClient-Win64-Shipping"));
        Check("A game opened during a PUBG match leaves PUBG its routes", watcher.RunningProcessName == "TslGame_BE" && events.Count == 2,
            $"{watcher.RunningProcessName}, events {string.Join(" ", events)}");
        Check("  and the log says why, once", log.Count(l => l.Contains("keeps its routes until the match ends")) == 1, string.Join(" | ", log));
        inMatch = false;
        watcher.Look(Running("LeagueClient", "TslGame_BE", "TslGame", "DeltaForceClient-Win64-Shipping"));
        Check("  and takes them once the match is over", watcher.RunningProcessName == "DeltaForceClient-Win64-Shipping" && events.Count == 3,
            $"{watcher.RunningProcessName}, events {string.Join(" ", events)}");

        watcher.Look(Running("LeagueClient", "TslGame"));
        Check("The newest closing falls back to the newest still open", watcher.RunningProcessName == "TslGame" && events.Count == 4,
            $"{watcher.RunningProcessName}, events {string.Join(" ", events)}");
        watcher.Look(Running("LeagueClient"));
        Check("  and then to the next", watcher.RunningProcessName == "LeagueClient" && events.Count == 5,
            $"{watcher.RunningProcessName}, events {string.Join(" ", events)}");
        Check("One game left ends the warning", watcher.SeveralGamesOpen is null, Names(watcher.SeveralGamesOpen));

        watcher.Look(Running("LeagueClient", "TslGame"));
        Check("  and it does not come back in the same connection", watcher.SeveralGamesOpen is null && watcher.RunningProcessName == "TslGame",
            $"{Names(watcher.SeveralGamesOpen)}, {watcher.RunningProcessName}");

        watcher.Look(Running());
        Check("Nothing open: the routes go", !watcher.IsGameRunning && events[^1] == "-", string.Join(" ", events));

        // Already open at the first look: no order to see, so the profile's stands - what ChooseGameForConnect measured for.
        using var atConnect = new GameProcessWatcher(games);
        var both = Running("LeagueClientUx", "TslGame");
        atConnect.Look(both);
        Check("Games already open at connect keep the profile's order", atConnect.RunningProcessName == GameProcessWatcher.FindRunning(games.SelectMany(g => g.ProcessNames), both) &&
            atConnect.RunningProcessName == "LeagueClientUx", $"{atConnect.RunningProcessName}");
        // Oldest first, and the one routed for counts as the newest: LoL last.
        Check("  and are named for the warning", Names(atConnect.SeveralGamesOpen) == "pubg,lol", Names(atConnect.SeveralGamesOpen));
        return Task.CompletedTask;
    }

    private static Task ALicenceThatRunsOutEndsTheConnection()
    {
        var expiry = new DateTimeOffset(2026, 10, 20, 9, 48, 0, TimeSpan.Zero);
        var grace = TunnelEngine.LicenceRenewalGrace;
        var past = expiry + grace + TimeSpan.FromSeconds(1);

        Check("A connection made without a licence (self-hosted) is never ended",
            !TunnelEngine.LicenceRanOut(null, null, past) && !TunnelEngine.LicenceRanOut(null, expiry, past));
        Check("Before the expiry nothing happens", !TunnelEngine.LicenceRanOut(expiry, expiry, expiry.AddMinutes(-1)));
        Check("Past the expiry, the same token still held: the renewal is given its grace",
            !TunnelEngine.LicenceRanOut(expiry, expiry, expiry + grace - TimeSpan.FromSeconds(1)));
        Check("  and the connection ends once it has passed with nothing newer", TunnelEngine.LicenceRanOut(expiry, expiry, past));
        Check("The token cleared past the expiry (402: the trial is over) ends it at once",
            TunnelEngine.LicenceRanOut(expiry, null, expiry.AddSeconds(1)));
        Check("The token cleared before the expiry (signing out) does not - the app disconnects itself",
            !TunnelEngine.LicenceRanOut(expiry, null, expiry.AddMinutes(-5)));
        var renewed = expiry.AddHours(24);
        Check("A renewed token keeps the connection past the old expiry", !TunnelEngine.LicenceRanOut(expiry, renewed, past));
        Check("  until it runs out in turn", TunnelEngine.LicenceRanOut(expiry, renewed, renewed + grace + TimeSpan.FromSeconds(1)));
        Check("An older token pushed later never shortens it", !TunnelEngine.LicenceRanOut(renewed, expiry, past));
        Check("The status line is the one the app turns into the notice and the plans",
            TunnelEngine.LicenceExpiredText.Key == "svc.licenceExpired", TunnelEngine.LicenceExpiredText.Key);
        return Task.CompletedTask;
    }
}
