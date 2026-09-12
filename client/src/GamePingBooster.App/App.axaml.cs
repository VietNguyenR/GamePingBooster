using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using GamePingBooster.App.Services;
using GamePingBooster.App.ViewModels;
using GamePingBooster.App.Views;

namespace GamePingBooster.App;

public partial class App : Application
{
    private PipeClient? _pipe;
    private TokenRefresher? _refresher;
    private ProfileSync? _profileSync;
    private UpdateChecker? _updates;
    private SystemTray? _tray;

    /// <summary>Guards the second pass: the Shutdown below raises ShutdownRequested again.</summary>
    private bool _shuttingDown;

    // Held so the exit can be started from somewhere other than the ShutdownRequested handler -
    // the tray menu's Exit item, which has no window and no event args to work with.
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private MainViewModel? _vm;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            _pipe = new PipeClient();
            var vm = new MainViewModel(_pipe);
            _vm = vm;

            // The window needs the pipe as well as the view model: the settings screen sends
            // on it directly, and listens on it for the service's verdict.
            var window = new MainWindow { DataContext = vm };
            window.Attach(_pipe);
            desktop.MainWindow = window;
            // Renews the licence token on its own, at half of its remaining life. It reads what
            // it needs from the view model rather than holding its own copy, so there is one
            // answer to "what does this client believe" and it is the one on screen.
            //
            // Harmless on a self-hosted installation: with no licence URL and no refresh token
            // it never sends anything, it just sleeps.
            _refresher = new TokenRefresher(
                _pipe,
                () => vm.LicenceUrl,
                () => vm.DevicePublicKey,
                // Marshalled: the refresher reports from its own loop, and raising
                // PropertyChanged off the UI thread breaks Avalonia's bindings in ways that
                // surface much later and somewhere else.
                message => Dispatcher.UIThread.Post(() => vm.LicenceNotice = message));
            _pipe.StatusReceived += _refresher.OnStatus;

            // Fetches the game list and hands it to the service. In the UI because only the UI
            // holds the credential the licence server asks for - see ProfileSync.
            _profileSync = new ProfileSync(
                _pipe,
                message => Dispatcher.UIThread.Post(() => vm.LicenceNotice = message));
            window.AttachProfileSync(_profileSync);

            // One attempt shortly after the service has had time to report its configuration.
            // Not on the first status push: that arrives before the pipe has settled, and a
            // fetch that races the connection reports a failure nobody needs to see.
            _pipe.StatusReceived += OnFirstStatus;

            void OnFirstStatus(Core.Ipc.StatusMessage status)
            {
                _pipe.StatusReceived -= OnFirstStatus;
                var written = status.ProfileUpdatedAt is { } unix
                    ? DateTimeOffset.FromUnixTimeSeconds(unix)
                    : (DateTimeOffset?)null;
                _ = _profileSync.SyncAsync(status.LicenceUrl, status.DevicePublicKey, "pubg", false, written);
            }

            // The catch-all: closing the main window is handled in MainWindow.OnClosing,
            // where the window can stay up and say "Disconnecting...", but that is not the only
            // way a desktop app ends. A log-off and a session end arrive here and nowhere else,
            // and each of them used to leave the tunnel up.
            //
            // Not desktop.Shutdown() though - that one is a forced shutdown and skips this event
            // entirely, which is why the tray's Exit calls RequestExit directly rather than
            // asking the lifetime to shut down and trusting this to notice.
            //
            // The old handler was `async (_, _) =>`, which is fire-and-forget on an event: the
            // shutdown carried straight on while the disposal ran, so even the cleanup that WAS
            // written here was not reliably finished. Cancelling and shutting down explicitly
            // afterwards is the only way to await anything from here.
            desktop.ShutdownRequested += (_, e) =>
            {
                if (_shuttingDown) return;
                e.Cancel = true;
                RequestExit();
            };

            // The notification area. Created last, once there is a window and a view model for it
            // to work with, and allowed to fail: a machine whose shell will not give us an icon
            // still gets a working app, just without minimise-to-tray. SystemTray only switches
            // that behaviour on if it got this far.
            try
            {
                _tray = new SystemTray(window, vm, RequestExit);
            }
            catch (Exception)
            {
                _tray = null;
            }

            // Start listening to the service. Bringing the tunnel up waits for the user to press
            // the button - the app never rearranges the machine's routing on its own at startup.
            _pipe.Start();
            _refresher.Start();

            // Checks GitHub for a newer release now and then, and puts a line in the menu when
            // there is one. Marshalled for the same reason as the refresher's messages.
            _updates = new UpdateChecker(update => Dispatcher.UIThread.Post(() => vm.Update = update));
            _updates.Start();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Starts the exit, from wherever it was asked for: the tray menu, or the ShutdownRequested
    /// that a log-off raises. Guarded, because both can arrive for the same exit and the teardown
    /// must not run twice.
    /// </summary>
    private void RequestExit()
    {
        if (_shuttingDown) return;
        if (_desktop is null || _vm is null) return;

        _shuttingDown = true;
        _ = ShutdownAsync(_desktop, _vm);
    }

    /// <summary>
    /// Ends the app in the right order: tunnel down, then background work stopped, then the pipe
    /// closed, then shut down for real.
    ///
    /// The order is not cosmetic. Disposing the pipe first would throw away the disconnect that
    /// has not been written yet, which is precisely the bug this exists to fix; and Shutdown has
    /// to come last, because it is what lets the process exit.
    ///
    /// Nothing here is allowed to prevent the exit. A user closing an application gets to close
    /// it, so every step is wrapped and the shutdown happens in the finally.
    /// </summary>
    private async Task ShutdownAsync(IClassicDesktopStyleApplicationLifetime desktop, MainViewModel vm)
    {
        try
        {
            // Usually already done by MainWindow.OnClosing, which returns at once when the state
            // is already Disconnected. This is for the paths that never touch a window.
            await vm.DisconnectAndWaitAsync(TimeSpan.FromSeconds(6));

            // Before the shutdown, not after: the process ending does not clear the notification
            // area on its own, and the icon left behind is one the user can click.
            _tray?.Dispose();

            if (_refresher is not null) await _refresher.DisposeAsync();
            if (_updates is not null) await _updates.DisposeAsync();
            if (_pipe is not null) await _pipe.DisposeAsync();
        }
        catch (Exception)
        {
            // Nothing to report to: there is no window left to report it in.
        }
        finally
        {
            desktop.Shutdown();
        }
    }
}
