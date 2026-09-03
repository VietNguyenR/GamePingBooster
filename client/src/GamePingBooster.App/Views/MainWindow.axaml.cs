using Avalonia.Controls;
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
    private async void OnSignInClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || _pipe is null) return;

        if (string.IsNullOrWhiteSpace(vm.LicenceUrl) || string.IsNullOrWhiteSpace(vm.DevicePublicKey))
        {
            return;
        }

        var dialog = new LoginWindow
        {
            DataContext = new LoginViewModel(vm.LicenceUrl, vm.DevicePublicKey, _pipe),
        };
        await dialog.ShowDialog(this);
    }

    private async void OnActionClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            await vm.ToggleAsync();
        }
    }
}
