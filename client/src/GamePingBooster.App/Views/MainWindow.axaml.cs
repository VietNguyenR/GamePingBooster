using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using GamePingBooster.App.Services;
using GamePingBooster.App.ViewModels;
using GamePingBooster.Core.Ipc;

namespace GamePingBooster.App.Views;

public partial class MainWindow : SurfaceWindow
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private PipeClient? _pipe;

    public void Attach(PipeClient pipe) => _pipe = pipe;

    private ProfileSync? _profileSync;

    /// <summary>The sign-in window fetches the game list as soon as it has a credential.</summary>
    public void AttachProfileSync(ProfileSync sync) => _profileSync = sync;

    private async void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || _pipe is null) return;

        // Opened with what is CONFIGURED, never with the key: the service does not send one up,
        // deliberately. See the set-relay verb in PipeServer.
        var dialog = new SettingsWindow
        {
            DataContext = new SettingsViewModel(vm.RelayEndpoints, vm.Configured, vm.LicenceUrl),
        };
        dialog.Attach(_pipe);
        await dialog.ShowDialog(this);
    }

    /// <summary>
    /// Opens the sign-in window.
    ///
    /// It needs the device public key, which arrives with the status rather than being read
    /// here: the UI cannot read %ProgramData% and has no business generating a device identity
    /// of its own. If the status has not arrived yet there is nothing sensible to show, so say
    /// so rather than opening a window that cannot work.
    /// </summary>
    /// <summary>
    /// Sign in, or the account screen - whichever the machine is in.
    ///
    /// It used to always open the sign-in window, which on a machine that was already signed in
    /// asked for a password to reach a state it was already in. HasToken is the discriminator:
    /// it is what the service reports, so the window matches what the service believes rather
    /// than what this process last remembered.
    /// </summary>
    private async void OnAccountClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || _pipe is null) return;

        if (string.IsNullOrWhiteSpace(vm.LicenceUrl) || string.IsNullOrWhiteSpace(vm.DevicePublicKey))
        {
            return;
        }

        if (vm.HasToken)
        {
            var account = new AccountWindow
            {
                DataContext = new AccountViewModel(vm.LicenceUrl, vm.DevicePublicKey, _pipe),
            };
            await account.ShowDialog(this);
            return;
        }

        var dialog = new LoginWindow
        {
            DataContext = new LoginViewModel(vm.LicenceUrl, vm.DevicePublicKey, _pipe, _profileSync),
        };
        await dialog.ShowDialog(this);
    }

    /// <summary>
    /// Who wrote this, under what licence, and which version is installed.
    ///
    /// The licence text is in the window rather than a link to LICENSE, because MIT requires the
    /// notice to travel with the software and somebody who installed a .exe has no LICENSE file
    /// in front of them.
    /// </summary>
    /// <summary>
    /// Opens the lag report dialog. Nothing is measured or sent until the person consents there -
    /// this only opens a window.
    /// </summary>
    private async void OnReportLagClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;

        await new ReportLagWindow(() => vm.LastStatus, vm.LicenceUrl, vm.DevicePublicKey)
            .ShowDialog(this);
    }

    private async void OnAboutClick(object? sender, RoutedEventArgs e)
    {
        await new AboutWindow().ShowDialog(this);
    }

    /// <summary>Opens the folder the service writes its log to. The first thing support asks for.</summary>
    private void OnLogsClick(object? sender, RoutedEventArgs e)
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "GamePingBooster", "logs");
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // Nothing useful to do. Opening a folder is a convenience, not a feature.
        }
    }

    private async void OnActionClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            // The profile sync goes along so Connect fetches the latest profile first - see
            // ToggleAsync. Null before App has attached it, in which case Connect behaves as before.
            await vm.ToggleAsync(_profileSync);
        }
    }

    /// <summary>
    /// Opens the release page of the newer version. The URL comes from UpdateChecker, which only
    /// ever hands over a page of this project's GitHub releases.
    /// </summary>
    private void OnUpdateClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel { Update: { } update }) return;
        try
        {
            Process.Start(new ProcessStartInfo(update.Url) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // No default browser, or the shell refused. Nothing more useful to do from a menu item.
        }
    }

    // ------------------------------------------------------------ minimise to tray

    /// <summary>
    /// Off until <see cref="SystemTray"/> turns it on. The window must never hide itself while
    /// there is no icon to bring it back - see the comment on that class.
    /// </summary>
    private bool _minimizeToTray;

    /// <summary>
    /// What to restore to. Not always Normal: a maximised window that is minimised and then
    /// brought back from the tray should come back maximised, the way every other Windows app
    /// behaves.
    /// </summary>
    private WindowState _restoreTo = WindowState.Normal;

    public void EnableMinimizeToTray() => _minimizeToTray = true;

    /// <summary>
    /// Minimising hides the window instead of parking it in the taskbar.
    ///
    /// This is where a booster differs from an ordinary app: it is meant to be left running for
    /// a whole session, so the taskbar button is the thing to get rid of, not the window. Closing
    /// is untouched and still means quit - with the tunnel brought down first, below.
    ///
    /// The hide is posted rather than done inline: this runs while the platform is still applying
    /// the state change it is reporting, and hiding a window from inside its own WindowState
    /// notification leaves Win32 minimising a window that is no longer on screen.
    /// </summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property != WindowStateProperty) return;

        if (change.GetNewValue<WindowState>() is not WindowState.Minimized)
        {
            _restoreTo = change.GetNewValue<WindowState>();
            return;
        }

        if (!_minimizeToTray) return;
        Dispatcher.UIThread.Post(HideToTray);
    }

    private void HideToTray()
    {
        // Restored again in the gap between the post and here - by a taskbar click, or by the
        // tray icon itself. Hiding now would take away a window the user has just asked for.
        if (WindowState is not WindowState.Minimized) return;
        Hide();
    }

    /// <summary>
    /// Brings the window back from the notification area, whichever way it was asked for.
    ///
    /// Show() first, then the state: setting WindowState on a window the platform has hidden is
    /// the order that leaves Avalonia's IsVisible and the real window disagreeing, which shows up
    /// as a window that is on screen but will not take focus.
    /// </summary>
    public void RestoreFromTray()
    {
        Show();
        WindowState = _restoreTo;
        Activate();
    }

    // ------------------------------------------------------------ closing

    /// <summary>Set once the tunnel is down, so the second Close is allowed through.</summary>
    private bool _readyToClose;

    /// <summary>
    /// Closing the window brings the tunnel down first.
    ///
    /// It did not, and the result was an app that looked closed while the adapter, the pinned
    /// relay route and every game route stayed exactly where they were - with no window to press
    /// Disconnect in. The service is a Windows service and carries on quite happily without a UI,
    /// which is what made this invisible rather than obviously broken.
    ///
    /// The close is cancelled, not delayed: the window stays on screen saying "Disconnecting..."
    /// while the service tears down, and closes for real afterwards. Hiding it and letting the
    /// process linger would look like a hang, and this can genuinely take a second or two -
    /// netsh runs one process per route.
    ///
    /// async void is right here and only here: this overrides an event-shaped method, and there
    /// is nothing to hand a Task to.
    /// </summary>
    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);

        if (_readyToClose || e.Cancel) return;
        if (DataContext is not MainViewModel vm) return;
        if (vm.State is TunnelState.Disconnected) return;

        e.Cancel = true;

        // Capped, because a window that will not close is worse than a tunnel that takes a
        // moment longer to go down. The service finishes on its own either way.
        await vm.DisconnectAndWaitAsync(TimeSpan.FromSeconds(6));

        _readyToClose = true;
        Close();
    }
}
