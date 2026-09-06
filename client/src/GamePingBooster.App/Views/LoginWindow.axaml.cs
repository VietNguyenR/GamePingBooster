using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using GamePingBooster.App.ViewModels;

namespace GamePingBooster.App.Views;

public partial class LoginWindow : SurfaceWindow
{
    private readonly CancellationTokenSource _cts = new();

    public LoginWindow()
    {
        InitializeComponent();
        Closed += (_, _) =>
        {
            // A sign-in in flight when the window closes has nowhere to report to, and its
            // continuation would touch a dead view model. Cancel rather than let it land.
            _cts.Cancel();
            _cts.Dispose();
        };
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private async void OnSubmitClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LoginViewModel vm) return;

        await vm.SubmitAsync(_cts.Token);

        // Close only on success. On failure the window stays with the message in it, because
        // closing would leave somebody looking at the main window wondering what happened.
        if (vm.Succeeded) Close();
    }

    private async void OnBrowserClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LoginViewModel vm) return;

        await vm.SignInWithBrowserAsync(_cts.Token);

        // Same rule as the form: close only on success, so a failure leaves its message on
        // screen next to the form that can still get past it.
        if (vm.Succeeded) Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();
}
