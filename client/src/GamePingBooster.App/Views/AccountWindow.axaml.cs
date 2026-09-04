using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using GamePingBooster.App.ViewModels;

namespace GamePingBooster.App.Views;

public partial class AccountWindow : SurfaceWindow
{
    private readonly CancellationTokenSource _cts = new();

    public AccountWindow()
    {
        InitializeComponent();

        // Loaded rather than the constructor: the DataContext is set by the caller after
        // construction, so there is nothing to load from yet.
        Opened += async (_, _) =>
        {
            if (DataContext is AccountViewModel vm) await vm.LoadAsync(_cts.Token);
        };

        Closed += (_, _) =>
        {
            // A request in flight has nowhere to report to once the window is gone, and its
            // continuation would touch a dead view model.
            _cts.Cancel();
            _cts.Dispose();
        };
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private async void OnSignOutClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AccountViewModel vm) return;

        await vm.SignOutAsync(_cts.Token);

        // Closed even when the server could not be reached: the credential is off this machine,
        // which is what the button promised. The message, if any, was already shown.
        if (vm.SignedOut) Close();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
