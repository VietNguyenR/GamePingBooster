using System.Diagnostics;
using System.Net;

namespace GamePingBooster.Service.Tunnel;

/// <summary>
/// Runs <see cref="LobbyTcpWatch"/> while the game is open: the game's TCP connections, per name, in the log. Logs only.
/// </summary>
internal sealed partial class TunnelEngine
{
    /// <summary>What a game connection goes to (UnblockDns.NameOfConnection); set by Program.</summary>
    public Func<IPEndPoint, int, LobbyTcpWatch.Named?>? NameOfConnection { get; set; }

    /// <summary>The Settings switch (ServiceConfig.LobbyWatch): off unless somebody turned it on.</summary>
    public bool LobbyWatchOn => _config.LobbyWatch ?? false;

    /// <summary>Turns the lobby watch on or off and saves it; says why when it could not be saved. Takes effect
    /// within one look - a game already running is followed from then on.</summary>
    public string? SetLobbyWatch(bool enabled)
    {
        var previous = _config.LobbyWatch;
        _config.LobbyWatch = enabled;
        try
        {
            _config.Save();
        }
        catch (Exception ex)
        {
            _config.LobbyWatch = previous;
            return $"Could not save the setting: {ex.Message}";
        }

        _log(enabled
            ? "Lobby TCP logging switched on: the game's TCP connections are written to this log while it runs."
            : "Lobby TCP logging switched off.");
        StatusChanged?.Invoke(Snapshot());
        return null;
    }

    /// <summary>The game's processes are looked up this often; the table every <see cref="LobbyTcpWatch.Every"/>.</summary>
    private static readonly TimeSpan LobbyWatchPidsEvery = TimeSpan.FromSeconds(2);

    public async Task RunLobbyWatchAsync(CancellationToken ct)
    {
        var watch = new LobbyTcpWatch((remote, port) => NameOfConnection?.Invoke(remote, port), () => _tunnel?.Session.ClientIp, _log);
        var clock = Stopwatch.StartNew();
        IReadOnlySet<uint> pids = new HashSet<uint>();
        TimeSpan? pidsAt = null;
        string? lastFailure = null;
        var watching = false;
        try
        {
            using var timer = new PeriodicTimer(LobbyTcpWatch.Every);
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    // Off (the default): nothing is read. Switched off mid-game: what was followed is written once.
                    if (!LobbyWatchOn)
                    {
                        if (watching) watch.Flush(clock.ElapsedMilliseconds);
                        watching = false;
                        pidsAt = null;
                        continue;
                    }
                    if (pidsAt is null || clock.Elapsed - pidsAt.Value >= LobbyWatchPidsEvery)
                    {
                        pidsAt = clock.Elapsed;
                        pids = _game is { } game ? GameProcessIds(game) : new HashSet<uint>();
                    }
                    if (pids.Count == 0)
                    {
                        if (watching) watch.Flush(clock.ElapsedMilliseconds);
                        watching = false;
                        continue;
                    }
                    if (!watching) _log($"Lobby TCP: following {_game?.Name}'s TCP connections (log only).");
                    watching = true;
                    watch.Poll(pids, clock.ElapsedMilliseconds);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Once per kind of failure, not twice a second.
                    if (ex.Message != lastFailure) _log($"Lobby TCP: a look at the game's connections failed ({ex.Message}).");
                    lastFailure = ex.Message;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        if (watching) watch.Flush(clock.ElapsedMilliseconds);
    }
}
