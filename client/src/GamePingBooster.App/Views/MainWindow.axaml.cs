using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
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
            await vm.ToggleAsync();
        }
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
