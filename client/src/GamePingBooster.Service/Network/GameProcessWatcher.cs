using System.Diagnostics;

namespace GamePingBooster.Service.Network;

/// <summary>
/// Watches whether the game process is running, so routes go in and come out at the right time.
///
/// How: it enumerates running processes - exactly what Task Manager does, through a public
/// Windows API. It does NOT open a handle into the game, read its memory, hook it, or inject
/// anything. That is a hard constraint of this project and the reason BattlEye has nothing to
/// object to.
///
/// Why watch at all: PUBG's IP ranges live on AWS/Azure alongside thousands of other services.
/// Leaving the routes in place permanently would drag unrelated traffic through the relay.
/// </summary>
internal sealed class GameProcessWatcher : IDisposable
{
    private readonly string[] _processNames;
    private readonly TimeSpan _interval;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    /// <summary>Raised on change: true when the game just started, false when it just exited.</summary>
    public event Action<bool, string?>? GameStateChanged;

    public bool IsGameRunning { get; private set; }
    public string? RunningProcessName { get; private set; }

    public GameProcessWatcher(IEnumerable<string> processNames, TimeSpan? interval = null)
    {
        // Process.GetProcessesByName expects names WITHOUT the .exe suffix.
        _processNames = processNames
            .Select(n => n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n[..^4] : n)
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
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
                var found = FindRunningGame();
                var running = found is not null;
                if (running != IsGameRunning)
                {
                    IsGameRunning = running;
                    RunningProcessName = found;
                    GameStateChanged?.Invoke(running, found);
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

    private string? FindRunningGame()
    {
        foreach (var name in _processNames)
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

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(3)); } catch (AggregateException) { /* shutting down */ }
        _cts.Dispose();
    }
}
