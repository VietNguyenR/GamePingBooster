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
/// process that is open decides which game's routes are installed.
/// </summary>
internal sealed class GameProcessWatcher : IDisposable
{
    private readonly string[] _processNames;
    private readonly TimeSpan _interval;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    /// <summary>
    /// Raised on change: (true, process) when a game started or a different game's process took
    /// over, (false, null) when the last one exited.
    /// </summary>
    public event Action<bool, string?>? GameStateChanged;

    public bool IsGameRunning { get; private set; }
    public string? RunningProcessName { get; private set; }

    public GameProcessWatcher(IEnumerable<string> processNames, TimeSpan? interval = null)
    {
        _processNames = Normalise(processNames);
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
                var found = FindRunning(_processNames);

                // Compared by NAME, not only running-or-not. Closing one game and opening another
                // within a single poll would otherwise look like no change at all, and the first
                // game's routes would stay in while the second game played.
                if (!string.Equals(found, RunningProcessName, StringComparison.OrdinalIgnoreCase))
                {
                    IsGameRunning = found is not null;
                    RunningProcessName = found;
                    GameStateChanged?.Invoke(IsGameRunning, found);
                }
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
    /// The first of <paramref name="processNames"/> that is running, or null. Also used once at
    /// connect, before a watcher exists, to measure relays for a game that is already open.
    /// </summary>
    public static string? FindRunning(IEnumerable<string> processNames)
    {
        foreach (var name in Normalise(processNames))
        {
            var procs = Process.GetProcessesByName(name);
            try
            {
                if (procs.Length > 0) return name;
            }
            finally
            {
                foreach (var p in procs) p.Dispose();
            }
        }
        return null;
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
