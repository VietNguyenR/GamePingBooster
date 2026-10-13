using System.Diagnostics;
using System.Net;
using GamePingBooster.Core.Ipc;
using GamePingBooster.Core.Profiles;
using GamePingBooster.Service.Native;

namespace GamePingBooster.Service.Tunnel;

/// <summary>
/// What the game does on its way back to the lobby, written to the log every few seconds after each match - so a
/// player stuck on the lobby's loading screen ("màn khói") only has to send gpb-service.log.
///
/// FPT, 2026-10-11: a customer was stuck twice after a match and closed the game each time; the owner was stuck the
/// same evening. Nothing in the log said what the game was waiting for, and tools/Check-Unblock.ps1, which can, asks for
/// the game to be opened again - which ends the very thing it should look at. So the service looks itself: the game's
/// TCP connections (to which server, in what state, for how long) and the unblock proxy's view of the ones it carries
/// (which way each goes, what came down, whether the game is waiting for an answer).
///
/// Starts when the supervisor sees a match end (the same gap that starts the rescan), stops when the next match starts,
/// the game closes, the tunnel goes, or after <see cref="StuckWatchFor"/> (every 5 s for the first
/// <see cref="AfterMatchFor"/>, every 30 s after). Addresses and names are sealed by the log.
///
/// And it reports it. Both stuck customers got out the same way: closed the game (or pressed Disconnect) before the next
/// match and opened it again. When that is seen, <see cref="AfterMatchTrouble"/> sends an unblock report, which carries
/// these lines - the stuck player no longer has to be asked for the log, and the reports add up by provider and relay.
/// </summary>
internal sealed partial class TunnelEngine
{
    private static readonly TimeSpan AfterMatchEvery = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan AfterMatchFor = TimeSpan.FromMinutes(3);

    /// <summary>After <see cref="AfterMatchFor"/> a line is written this often, until <see cref="StuckWatchFor"/>.</summary>
    private static readonly TimeSpan AfterMatchSlowEvery = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long after a match the game closing (or Disconnect) still counts as a way out of a stuck lobby. Longer than
    /// the 5 s log: a player waits a minute or several before giving up, the owner waited longer than the customer.
    /// </summary>
    private static readonly TimeSpan StuckWatchFor = TimeSpan.FromMinutes(10);

    /// <summary>The game opened again this soon after it closed (or after Disconnect) is a player getting unstuck.</summary>
    private static readonly TimeSpan RelaunchWithin = TimeSpan.FromMinutes(5);

    /// <summary>Most connections named on one line; the rest are counted.</summary>
    private const int AfterMatchListed = 12;

    /// <summary>The unblock proxy's open connections, one clause per name and path; set by Program (UnblockDns).</summary>
    public Func<string?>? LobbyConnections { get; set; }

    /// <summary>
    /// Told (trigger, what happened, null) when a player looks stuck on the way back to the lobby; set by Program to
    /// UnblockReporter.Trouble, which sends it with the log's tail - the "After the match" lines - to
    /// /admin/unblock/reports. Must not block.
    /// </summary>
    public Action<string, string, string?>? AfterMatchTrouble { get; set; }

    public const string AfterMatchStuckTrigger = "after-match-stuck";

    private int _afterMatchWatching;

    private void StartAfterMatchWatch(TunnelClient tunnel, CancellationToken ct)
    {
        if (_game is not { } game || game.ProcessNames.Count == 0) return;
        if (Interlocked.Exchange(ref _afterMatchWatching, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await WatchAfterMatchAsync(game, tunnel, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log($"After the match: watching the game's connections failed ({ex.Message}).");
            }
            finally
            {
                Volatile.Write(ref _afterMatchWatching, 0);
            }
        }, CancellationToken.None);
    }

    private async Task WatchAfterMatchAsync(GameEntry game, TunnelClient tunnel, CancellationToken ct)
    {
        var began = Stopwatch.GetTimestamp();
        var home = tunnel;
        var rate = new GameRate(began, AllGamePackets(home));
        var firstSeen = new Dictionary<(int Port, IPEndPoint Remote), long>();
        var lastPids = GameProcessIds(game);
        var lastLine = TimeSpan.Zero;
        string? endedBy = null;
        string Since() => $"+{Stopwatch.GetElapsedTime(began).TotalSeconds:0} s";

        try
        {
            while (Stopwatch.GetElapsedTime(began) < StuckWatchFor)
            {
                await Task.Delay(AfterMatchEvery, ct).ConfigureAwait(false);
                if (_state is TunnelState.Disconnected or TunnelState.Faulted)
                {
                    endedBy = "the player disconnected";
                    break;
                }
                // Connecting or Reconnecting: a move between matches, or a failover - wait for it.
                if (_state != TunnelState.Connected || _tunnel is not { } now) continue;
                if (!ReferenceEquals(now, home))
                {
                    // Moved between matches: the new tunnel's count starts again.
                    home = now;
                    rate = new GameRate(Stopwatch.GetTimestamp(), AllGamePackets(home));
                }
                else if (rate.Feed(Stopwatch.GetTimestamp(), AllGamePackets(home)) >= GameOpenMatchPerSecond)
                {
                    _log($"After the match, {Since()}: the next match is loading - stopped watching the game's connections.");
                    return;
                }

                var pids = GameProcessIds(game);
                if (pids.Count == 0)
                {
                    _log($"After the match, {Since()}: the game closed.");
                    endedBy = "the game closed";
                    break;
                }
                lastPids = pids;

                var elapsed = Stopwatch.GetElapsedTime(began);
                if (elapsed > AfterMatchFor && elapsed - lastLine < AfterMatchSlowEvery) continue;
                lastLine = elapsed;
                _log($"After the match, {Since()}: {DescribeGameTcp(TcpConnections.OfProcesses(pids), firstSeen)}" +
                     (LobbyConnections?.Invoke() is { } carried ? $" | carried by the unblock proxy: {carried}" : "") + ".");
            }
        }
        catch (OperationCanceledException)
        {
            // The supervisor's token: Disconnect, or the service stopping.
            endedBy = "the player disconnected";
        }

        if (endedBy is null)
        {
            _log($"After the match: no match for {StuckWatchFor.TotalMinutes:0} min - stopped watching the game's connections.");
            return;
        }
        var endedAt = Stopwatch.GetElapsedTime(began);
        if (lastPids.Count == 0) return;   // the game was not running when the watch began

        // The way out of a stuck lobby, seen on 2026-10-11 twice: close the game (or Disconnect) and open it again.
        // Not tied to the supervisor's token - Disconnect is one of the ways, and this only reads the process list.
        var closed = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(closed) < RelaunchWithin)
        {
            await Task.Delay(AfterMatchEvery, CancellationToken.None).ConfigureAwait(false);
            if (!Relaunched(lastPids, GameProcessIds(game))) continue;

            var detail = StuckDetail(game.Name, endedBy, endedAt, Stopwatch.GetElapsedTime(closed));
            _log($"After the match: {detail} - reporting it.");
            AfterMatchTrouble?.Invoke(AfterMatchStuckTrigger, detail, null);
            return;
        }
    }

    /// <summary>
    /// The game is running again as new processes: none of the ones running when the watch ended is left. Internal for
    /// TunnelCheck.
    /// </summary>
    internal static bool Relaunched(IReadOnlySet<uint> before, IReadOnlySet<uint> now) =>
        before.Count > 0 && now.Count > 0 && !now.Overlaps(before);

    /// <summary>"PUBG: the game closed 50 s after the match ended and was opened again 40 s later". Internal for TunnelCheck.</summary>
    internal static string StuckDetail(string game, string endedBy, TimeSpan endedAt, TimeSpan reopenedAfter) =>
        $"{game}: {endedBy} {endedAt.TotalSeconds:0} s after the match ended and the game was opened again " +
        $"{reopenedAfter.TotalSeconds:0} s later - stuck on the way back to the lobby?";

    /// <summary>The ids of the game's running processes (TslGame and its BattlEye shim, say).</summary>
    private static HashSet<uint> GameProcessIds(GameEntry game)
    {
        var ids = new HashSet<uint>();
        foreach (var name in game.ProcessNames)
        {
            var bare = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
            foreach (var process in Process.GetProcessesByName(bare))
            {
                using (process) ids.Add((uint)process.Id);
            }
        }
        return ids;
    }

    /// <summary>
    /// "game TCP 7: 127.77.0.1:443 Established x5 (up to 80 s), 52.1.2.3:443 SynSent (12 s)" - one clause per remote and
    /// state, the longest-open first. <paramref name="firstSeen"/> carries when each connection was first seen across
    /// calls, which is as close to its age as the table gets. Internal for TunnelCheck.
    /// </summary>
    internal static string DescribeGameTcp(IReadOnlyList<TcpConnections.Owned> rows,
        Dictionary<(int Port, IPEndPoint Remote), long> firstSeen)
    {
        var now = Stopwatch.GetTimestamp();
        var keys = rows.Select(r => (r.LocalPort, r.Remote)).ToHashSet();
        foreach (var gone in firstSeen.Keys.Where(k => !keys.Contains(k)).ToArray()) firstSeen.Remove(gone);
        foreach (var key in keys) firstSeen.TryAdd(key, now);
        if (rows.Count == 0) return "game TCP 0 - no connection open";

        var clauses = rows.GroupBy(r => (r.Remote, r.StateName))
            .Select(g => (g.Key, Count: g.Count(),
                Oldest: g.Max(r => Stopwatch.GetElapsedTime(firstSeen[(r.LocalPort, r.Remote)], now).TotalSeconds)))
            .OrderByDescending(c => c.Oldest)
            .ToArray();
        var listed = clauses.Take(AfterMatchListed).Select(c =>
            $"{c.Key.Remote} {c.Key.StateName}{(c.Count > 1 ? $" x{c.Count}" : "")} " +
            $"({(c.Count > 1 ? "up to " : "")}{c.Oldest:0} s)");
        var more = clauses.Length > AfterMatchListed ? $", and {clauses.Length - AfterMatchListed} more" : "";
        return $"game TCP {rows.Count}: {string.Join(", ", listed)}{more}";
    }
}
