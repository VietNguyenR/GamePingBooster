using System.Reflection;
using GamePingBooster.Core.Updates;

namespace GamePingBooster.App.Services;

/// <summary>
/// Looks for a newer release on GitHub the moment the app opens and every six hours after, and says
/// so in the footer. Since 0.5.0 the SERVICE installs releases by itself (AutoUpdater in
/// gpb-service), so this only announces: the update window it used to open at every start is offered
/// now only when the service cannot do it - see MainViewModel.AutoUpdateCovers. UpdateInstaller (the
/// window's Update button, UAC and all) stays as that fallback and as the footer's manual way in.
///
/// Until 2026-09-30 a new release was announced only by that footer line, grey and 11 px, and after
/// a week 20-30% of players were still on the old version (0.3.3-0.3.5, from the match records);
/// only the releases the licence server made compulsory reached everybody. Then the window opened at
/// every start, and players asked why the app wanted updating so often (2026-10-13) - which is what
/// moved installing into the service.
///
/// Deliberately quiet about failure. Offline, rate limited, GitHub down, a repository with no
/// release yet - every one of those means "no update to announce", and an update notice is not
/// worth an error message on a screen whose whole job is a Connect button.
/// </summary>
public sealed class UpdateChecker : IAsyncDisposable
{
    /// <summary>
    /// Every six hours. A release is not urgent, and unauthenticated calls to GitHub's API are
    /// limited to 60 an hour per address - an internet café full of players shares one.
    /// </summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    /// <summary>
    /// None: the update window is offered as the app opens, while the person is looking at it. It
    /// used to wait twenty seconds so as not to compete with the first connect, but connecting waits
    /// for a button press, and one small request does not slow it.
    /// </summary>
    public static readonly TimeSpan FirstDelay = TimeSpan.Zero;

    private readonly Action<AvailableUpdate, bool> _onUpdate;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    /// <param name="onUpdate">
    /// Called from a background thread when a newer release is found, with true when it was found by the
    /// check made as the app opened - the one that may open the update window by itself.
    /// </param>
    public UpdateChecker(Action<AvailableUpdate, bool> onUpdate) => _onUpdate = onUpdate;

    public void Start() => _loop ??= Task.Run(() => LoopAsync(_cts.Token));

    /// <summary>
    /// One check now, off the six-hour schedule: the licence server has just said this version is
    /// too old, and nobody should wait hours for the update that lets them connect again.
    /// </summary>
    public void CheckNow() => _ = Task.Run(async () =>
    {
        try
        {
            var update = await CheckAsync(CurrentVersion(), _cts.Token).ConfigureAwait(false);
            if (update is not null) _onUpdate(update, false);
        }
        catch (Exception)
        {
            // As in the loop: nothing to announce. The server's own message already says where to go.
        }
    });

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(FirstDelay, ct).ConfigureAwait(false);
            var atStart = true;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var update = await CheckAsync(CurrentVersion(), ct).ConfigureAwait(false);
                    if (update is not null) _onUpdate(update, atStart);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception)
                {
                    // See the class summary: no update to announce, and nothing worth saying.
                }
                atStart = false;

                await Task.Delay(Interval, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    /// <summary>
    /// The newest published release when it is newer than <paramref name="currentVersion"/>,
    /// otherwise null. Throws on network failure; the loop is what makes that quiet. See ReleaseFeed.
    /// </summary>
    public static Task<AvailableUpdate?> CheckAsync(string? currentVersion, CancellationToken ct) =>
        ReleaseFeed.CheckAsync(currentVersion, CachePath, ct);

    /// <summary>Where the last answer from GitHub is kept, per Windows user. The service keeps its own.</summary>
    internal static string CachePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GamePingBooster", "update-check.json");

    /// <summary>What this build is, as Directory.Build.props stamped it from VERSION.</summary>
    public static string? CurrentVersion() =>
        typeof(UpdateChecker).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        _cts.Dispose();
    }
}
