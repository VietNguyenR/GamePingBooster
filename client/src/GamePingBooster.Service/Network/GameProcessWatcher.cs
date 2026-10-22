using System.Diagnostics;
using GamePingBooster.Core.Profiles;

namespace GamePingBooster.Service.Network;

/// <summary>
/// Watches which game is running, so routes go in and come out at the right time - and for the
/// right game.
///
/// How: it enumerates running processes - exactly what Task Manager does, through a public
/// Windows API. It does NOT open a handle into the game, read its memory, hook it, or inject
/// anything. That is a hard constraint of this project and the reason BattlEye has nothing to
/// object to.
///
/// Why watch at all: PUBG's IP ranges live on AWS/Azure alongside thousands of other services.
/// Leaving the routes in place permanently would drag unrelated traffic through the relay.
///
/// It watches every game in the profile at once. There is no game selector in the client: the
/// game that is open decides which game's routes are installed, and with several open, the one
/// opened last (2026-10-22). It used to be the first in profile order, so a LoL client left open
/// meant PUBG started after it got no routes and no relay move at all.
/// </summary>
internal sealed class GameProcessWatcher : IDisposable
{
    private readonly GameEntry[] _games;
    private readonly string[][] _namesOf;
    private readonly Func<bool> _holdCurrent;
    private readonly Action<string> _log;
    private readonly TimeSpan _interval;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    /// <summary>
    /// The games running at the last look, oldest first: the order in which this watcher SAW them open. The last one is
    /// the newest and the one routed for.
    ///
    /// Seen, not asked: a process's start time needs a handle into it (Process.StartTime opens one), and opening
    /// anything on the game is the line this class never crosses - see above. Every game opened while connected is
    /// seen within one poll of starting, which is the case that matters: LoL's client left open, PUBG started after it
    /// (2026-10-22). Games already open at the first look have no order to see; they keep the profile's, so the first
    /// listed is routed for, as before this list existed.
    ///
    /// Per game, not per process: PUBG's BattlEye shim handing over to TslGame.exe, or the LoL client restarting a
    /// helper, is the same game still open and does not make it newer.
    /// </summary>
    private readonly List<int> _order = [];
    private int? _current;
    private bool _warningSpent;
    private int? _heldAgainst;

    /// <summary>
    /// Raised on change: (true, process) when a game started or a different game's process took
    /// over, (false, null) when the last one exited.
    /// </summary>
    public event Action<bool, string?>? GameStateChanged;

    public bool IsGameRunning { get; private set; }
    public string? RunningProcessName { get; private set; }

    /// <summary>Every game open at the last look, oldest first. Empty when none is.</summary>
    public IReadOnlyList<GameEntry> RunningGames { get; private set; } = [];

    /// <summary>
    /// The games to name in the "several games open" line, or null when there is nothing to say. Once per connect: it is
    /// said while two or more are open, and once that ends it does not come back until the next connect - somebody who
    /// keeps the LoL client open beside PUBG on purpose has been told.
    /// </summary>
    public IReadOnlyList<GameEntry>? SeveralGamesOpen { get; private set; }

    /// <param name="games">Every game in the profile, in profile order.</param>
    /// <param name="holdCurrent">
    /// True while the game routed for must keep its routes even though a newer one opened: its match is live. Moving the
    /// routes then sends the match's packets out of another address mid-round, which ends it; the newer game takes over
    /// at the first look after the match.
    /// </param>
    public GameProcessWatcher(IEnumerable<GameEntry> games, Func<bool>? holdCurrent = null, Action<string>? log = null,
        TimeSpan? interval = null)
    {
        _games = [.. games];
        _namesOf = [.. _games.Select(g => Normalise(g.ProcessNames))];
        _holdCurrent = holdCurrent ?? (() => false);
        _log = log ?? (_ => { });
        _interval = interval ?? TimeSpan.FromSeconds(2);
    }

    public void Start()
    {
        _loop ??= Task.Run(() => LoopAsync(_cts.Token));
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_interval);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                Look(RunningNames());
                await timer.WaitForNextTickAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                // Enumerating processes can fail transiently under heavy load; retry next tick.
                try { await Task.Delay(_interval, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    /// <summary>
    /// One look at the process list: which games are open, which of them is routed for, and the event when that changed.
    /// Called by the loop; TunnelCheck calls it with a list of its own.
    /// </summary>
    internal void Look(IReadOnlySet<string> running)
    {
        var open = new List<int>();
        for (var i = 0; i < _games.Length; i++)
        {
            if (_namesOf[i].Any(running.Contains)) open.Add(i);
        }

        _order.RemoveAll(i => !open.Contains(i));
        // Opened since the last look, in reverse profile order, so of several seen at once the first listed counts as
        // newest - at the first look that is the profile's order, exactly as before.
        foreach (var i in open.Where(i => !_order.Contains(i)).Reverse()) _order.Add(i);

        int? chosen = _order.Count > 0 ? _order[^1] : null;
        if (chosen != _current && _current is { } held && _order.Contains(held) && _holdCurrent())
        {
            if (_heldAgainst != chosen)
            {
                _log($"{_games[chosen!.Value].Name} opened during a {_games[held].Name} match - {_games[held].Name} keeps " +
                     "its routes until the match ends.");
            }
            _heldAgainst = chosen;
            chosen = held;
        }
        else
        {
            _heldAgainst = null;
        }
        _current = chosen;

        RunningGames = [.. _order.Select(i => _games[i])];
        if (_order.Count >= 2 && !_warningSpent)
        {
            if (SeveralGamesOpen is null)
            {
                _log($"Several games are open ({string.Join(", ", RunningGames.Select(g => g.Name))}) - routes follow " +
                     $"the one opened last, {_games[_order[^1]].Name}.");
            }
            SeveralGamesOpen = RunningGames;
        }
        else if (SeveralGamesOpen is not null)
        {
            SeveralGamesOpen = null;
            _warningSpent = true;
        }

        // Compared by NAME, not only running-or-not. Closing one game and opening another
        // within a single poll would otherwise look like no change at all, and the first
        // game's routes would stay in while the second game played. Within a game the first
        // listed process that is running names it, as before.
        var found = chosen is { } c ? _namesOf[c].First(running.Contains) : null;
        if (!string.Equals(found, RunningProcessName, StringComparison.OrdinalIgnoreCase))
        {
            IsGameRunning = found is not null;
            RunningProcessName = found;
            GameStateChanged?.Invoke(IsGameRunning, found);
        }
    }

    /// <summary>
    /// The first of <paramref name="processNames"/> that is running, or null. Also used once at
    /// connect, before a watcher exists, to measure relays for a game that is already open.
    ///
    /// One process list per call, not one per name: GetProcessesByName takes the whole list every time and filters
    /// it, so asking for each of 21 games' names cost ~190 ms of CPU every 2 s - about 9 % of a core for as long as
    /// the tunnel was up, game or no game (measured 2026-10-17, 530 processes). One list is ~8 ms.
    /// Pass <paramref name="running"/> (<see cref="RunningNames"/>) when asking about several games at once.
    /// </summary>
    public static string? FindRunning(IEnumerable<string> processNames, IReadOnlySet<string>? running = null)
    {
        running ??= RunningNames();
        // Profile order, as before: the first listed name that is running wins.
        foreach (var name in Normalise(processNames))
        {
            if (running.Contains(name)) return name;
        }
        return null;
    }

    /// <summary>The names of every running process (no ".exe"), from one process list.</summary>
    public static HashSet<string> RunningNames()
    {
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            using (p) running.Add(p.ProcessName);
        }
        return running;
    }

    /// <summary>Process.GetProcessesByName expects names WITHOUT the .exe suffix.</summary>
    private static string[] Normalise(IEnumerable<string> processNames) =>
        processNames
            .Select(ProfileMerge.StripExe)
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(3)); } catch (AggregateException) { /* shutting down */ }
        _cts.Dispose();
    }
}
