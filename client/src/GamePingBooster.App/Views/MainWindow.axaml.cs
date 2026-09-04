using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using GamePingBooster.App.Services;
using GamePingBooster.App.ViewModels;

namespace GamePingBooster.App.Views;

public partial class MainWindow : Window
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

    // ------------------------------------------------------------ our own title bar

    /// <summary>
    /// Drag the window by the strip under the caption.
    ///
    /// Avalonia's own title bar handles dragging in its area; this covers the rest of the strip,
    /// so the whole top of the window behaves the way people expect rather than only the part
    /// with the buttons on it.
    /// </summary>
    private void OnTitleBarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }



    private async void OnActionClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            await vm.ToggleAsync();
        }
    }
}
