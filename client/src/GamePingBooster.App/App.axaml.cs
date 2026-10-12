using System.Diagnostics;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using GamePingBooster.App.Services;
using GamePingBooster.App.Services.Localization;
using GamePingBooster.App.ViewModels;
using GamePingBooster.App.Views;

namespace GamePingBooster.App;

public partial class App : Application
{
    private PipeClient? _pipe;
    private TokenRefresher? _refresher;
    private ProfileSync? _profileSync;
    private PurchaseWatcher? _purchases;
    private SignInWatcher? _signIn;
    private QualityUploader? _quality;
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

            // Before anything with words on it exists: the language the person chose last time,
            // or the one Windows is in on a machine that has never chosen. See Loc.
            Loc.Initialize(this);

            _pipe = new PipeClient();
            var vm = new MainViewModel(_pipe);
            _vm = vm;

            // The window needs the pipe as well as the view model: the settings screen sends
            // on it directly, and listens on it for the service's verdict.
            var window = new MainWindow { DataContext = vm };
            window.Attach(_pipe);
            _window = window;
            // Shown at once only when the updater window has just started this app after an update. Otherwise the
            // updater window checks for a release first, and the main window follows when there is nothing to install
            // (see StartUpdateGate) - it must not appear and then vanish when setup closes the app a second later.
            if (desktop.Args?.Contains("--after-update") == true) OpenGate();
            else StartUpdateGate();
            // Renews the licence token on its own, at half of its remaining life. It takes the
            // licence URL and device key from the status push directly, not from the view model,
            // which only sees a status once the UI thread gets round to it - see TokenRefresher.
            //
            // Harmless on a self-hosted installation: with no licence URL and no refresh token
            // it never sends anything, it just sleeps.
            // Last sign-in wins: a long poll that hears at once when the account signs in on another machine,
            // and then takes this one down and out - see SignInWatcher and SignedOutElsewhereAsync below.
            var signIn = new SignInWatcher(what => Dispatcher.UIThread.Post(() => _ = SignedOutElsewhereAsync(window, vm, what)));
            _signIn = signIn;
            _pipe.StatusReceived += signIn.OnStatus;
            window.AttachSignInWatcher(signIn);

            _refresher = new TokenRefresher(
                _pipe,
                // Marshalled: the refresher reports from its own loop, and raising
                // PropertyChanged off the UI thread breaks Avalonia's bindings in ways that
                // surface much later and somewhere else.
                message => Dispatcher.UIThread.Post(() => vm.LicenceNotice = message),
                OnUpgradeRequired,
                notice => Dispatcher.UIThread.Post(() => window.OfferPlans(notice)),
                signIn.Report);
            _pipe.StatusReceived += _refresher.OnStatus;
            // Coming back to the app - typically from the payment page - asks again at once when
            // there is no usable token, rather than waiting out the refusal backoff.
            window.Activated += (_, _) => _refresher.Nudge();

            // Fetches the game list and hands it to the service. In the UI because only the UI
            // holds the credential the licence server asks for - see ProfileSync.
            _profileSync = new ProfileSync(
                _pipe,
                message => Dispatcher.UIThread.Post(() => vm.LicenceNotice = message),
                OnUpgradeRequired);
            window.AttachProfileSync(_profileSync);

            // Buying a plan in the app: watches an open order until the server says it is paid,
            // then renews the licence at once - so the plan is in force without signing in again,
            // even when the upgrade window was closed before the transfer landed.
            _purchases = new PurchaseWatcher(
                RefreshTokenStore.Load,
                () => _refresher.RenewNow(),
                action => Dispatcher.UIThread.Post(action));
            window.AttachPurchases(_purchases);

            // One attempt shortly after the service has had time to report its configuration.
            // Not on the first status push: that arrives before the pipe has settled, and a
            // fetch that races the connection reports a failure nobody needs to see.
            _pipe.StatusReceived += OnFirstStatus;

            // Sends the service's connection-quality records between matches. See QualityUploader.
            _quality = new QualityUploader(_pipe);
            _pipe.StatusReceived += _quality.OnStatus;

            void OnFirstStatus(Core.Ipc.StatusMessage status)
            {
                _pipe.StatusReceived -= OnFirstStatus;
                var written = status.ProfileUpdatedAt is { } unix
                    ? DateTimeOffset.FromUnixTimeSeconds(unix)
                    : (DateTimeOffset?)null;
                _ = _profileSync.SyncAsync(status.LicenceUrl, status.DevicePublicKey, false, written);

                if (QualityNotice.ShouldShow(status))
                {
                    Dispatcher.UIThread.Post(() => vm.LicenceNotice = QualityNotice.Text);
                }
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
            // The service is about to install an update and offers it ("ready"): show the updater window, which asks
            // for it (update-now) and follows every step, instead of this app simply vanishing when setup closes it.
            _pipe.StatusReceived += status =>
            {
                if (status.UpdateState == "ready") Dispatcher.UIThread.Post(OnUpdateReady);
            };

            _pipe.Start();
            _refresher.Start();
            signIn.Start();
            _quality.Start();

            // Checks GitHub for a newer release as the app opens and every six hours after, and puts a
            // line in the footer when there is one. The service installs it by itself (0.5.0); the update
            // window is offered (MainWindow.OfferUpdate) only when it will not. Marshalled for the same
            // reason as the refresher's messages.
            _updates = new UpdateChecker((update, atStart) => Dispatcher.UIThread.Post(() =>
            {
                vm.Update = update;
                if (atStart) window.OfferUpdate();
                window.OfferRequiredUpdate();
            }));
            _updates.Start();

            // The licence server refused this version as too old (426). Look for the release now
            // rather than in up to six hours, and offer it once it is known - OfferRequiredUpdate
            // does nothing until both the refusal and the release are in.
            void OnUpgradeRequired() => Dispatcher.UIThread.Post(() =>
            {
                vm.UpdateRequired = true;
                window.OfferRequiredUpdate();
                _updates?.CheckNow();
                // And the service, which installs it (0.5.0): its next hourly check could be an hour away.
                _ = _pipe?.SendAsync(new GamePingBooster.Core.Ipc.CommandMessage { Verb = "check-update" });
            });
        }

        base.OnFrameworkInitializationCompleted();
    }

    // ------------------------------------------------------------ the updater window (0.5.0)

    private MainWindow? _window;

    /// <summary>gpb-updater.exe while it is open. One at a time.</summary>
    private Process? _updater;

    /// <summary>The main window may be shown: the check at start is over, or was never needed.</summary>
    private bool _gateOpen;

    /// <summary>The main window was on screen when the updater window replaced it, and comes back if nothing is installed.</summary>
    private bool _windowWasVisible;

    /// <summary>gpb-updater.exe could not be started once: this run does without it rather than trying every second.</summary>
    private bool _updaterUnavailable;

    /// <summary>
    /// The updater window checks for a release before the main window appears (owner, 2026-10-13: open the app and see
    /// "checking for updates", then the install with every step, then the app). Only when it is worth it: the service
    /// installs updates by itself, nothing is connected, and no game is running - a player who opens the app mid-game
    /// wants to connect, not wait. No answer from the service at all and the app opens as it always did.
    /// </summary>
    private void StartUpdateGate()
    {
        if (_pipe is null)
        {
            OpenGate();
            return;
        }
        _pipe.StatusReceived += OnGateStatus;
        DispatcherTimer.RunOnce(OpenGate, TimeSpan.FromSeconds(1.5));

        void OnGateStatus(Core.Ipc.StatusMessage status)
        {
            _pipe.StatusReceived -= OnGateStatus;
            Dispatcher.UIThread.Post(() =>
            {
                if (_gateOpen) return;
                var worth = status.AutoUpdate &&
                            status.State is Core.Ipc.TunnelState.Disconnected or Core.Ipc.TunnelState.Faulted &&
                            !status.GameRunning &&
                            status.UpdateState is not ("refused" or "installing");
                if (!worth || !ShowUpdater("check")) OpenGate();
            });
        }
    }

    /// <summary>Shows the main window - once the check at start is over, and never while the updater window is open.</summary>
    private void OpenGate()
    {
        if (_gateOpen || _updater is not null || _desktop is null || _window is null || _shuttingDown) return;
        _gateOpen = true;
        _desktop.MainWindow = _window;
        _window.Show();
    }

    private void OnUpdateReady()
    {
        if (_updater is not null || !_gateOpen || _shuttingDown || _window is null) return;
        _windowWasVisible = _window.IsVisible;
        if (ShowUpdater("install") && _windowWasVisible) _window.Hide();
    }

    /// <summary>
    /// Starts gpb-updater.exe and asks the service to go ahead (update-now). False when the window could not be started -
    /// the update then happens as before 0.5.0's window existed. When it closes with this app still running, nothing was
    /// installed (none to install, a game started, a download failed): the main window comes back.
    /// </summary>
    private bool ShowUpdater(string mode)
    {
        if (_updater is not null) return true;
        if (_updaterUnavailable || _pipe is null) return false;

        var process = UpdaterLauncher.Start(mode);
        if (process is null)
        {
            _updaterUnavailable = true;
            return false;
        }
        _updater = process;
        process.Exited += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(_updater, process)) return;
            _updater = null;
            process.Dispose();
            if (!_gateOpen) OpenGate();
            else if (_windowWasVisible && _window is not null && !_shuttingDown) _window.RestoreFromTray();
            _windowWasVisible = false;
        });
        process.EnableRaisingEvents = true;

        _ = Task.Run(async () =>
        {
            try { await _pipe.SendAsync(new Core.Ipc.CommandMessage { Verb = "update-now" }); }
            catch (Exception) { /* The window gives up on its own after a few seconds of silence. */ }
        });
        return true;
    }

    /// <summary>Set while <see cref="SignedOutElsewhereAsync"/> runs, so a report from the watch and one from a renewal do it once.</summary>
    private bool _signingOut;

    /// <summary>
    /// This machine was signed out because the account signed in on another one. In the owner's order (2026-10-08):
    /// disconnected FIRST - game routes, the lobby proxy and unblocking all go with Disconnect - then signed out here,
    /// and only then told. The relay refuses the old licence by itself within ~20 s; this is the app agreeing with it
    /// at once, instead of sitting on "Reconnecting..." with no explanation.
    ///
    /// No /auth/logout: the server has already ended this sign-in.
    /// </summary>
    private async Task SignedOutElsewhereAsync(MainWindow window, MainViewModel vm, SignedOutElsewhere what)
    {
        if (_signingOut || _shuttingDown || _pipe is null) return;
        _signingOut = true;
        try
        {
            await vm.DisconnectAndWaitAsync(TimeSpan.FromSeconds(6));
            // Again, unconditionally: the window can believe it is down while a connect is still on its way to the
            // service. Idempotent there.
            try { await _pipe.DisconnectTunnelAsync(); } catch (Exception) { }

            RefreshTokenStore.Clear();
            try { await _pipe.SendAsync(new Core.Ipc.CommandMessage { Verb = "set-token", Token = null }); }
            catch (Exception) { }
            _purchases?.Clear();

            var text = what.Text();
            vm.SignedOutNotice = text;
            vm.RefreshSignedIn();
            if (!window.IsVisible) _tray?.Notify(Loc.T("signedOut.elsewhere.title"), text);
            window.ShowSignedOutElsewhere(text);
        }
        finally
        {
            _signingOut = false;
        }
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
            _purchases?.Dispose();

            if (_refresher is not null) await _refresher.DisposeAsync();
            if (_signIn is not null) await _signIn.DisposeAsync();
            if (_quality is not null) await _quality.DisposeAsync();
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
