using Avalonia.Controls;
using Avalonia.Input.Platform;
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

    private async void OnBrowserClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LoginViewModel vm) return;

        await vm.SignInWithBrowserAsync(_cts.Token);

        // Close only on success. On failure the window stays with the message in it, because
        // closing would leave somebody looking at the main window wondering what happened.
        if (vm.Succeeded) Close();
    }

    private async void OnCopyLinkClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LoginViewModel { ManualUrl: { } url } vm) return;

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;

        try
        {
            await clipboard.SetTextAsync(url);
            vm.MarkLinkCopied();
        }
        catch (Exception)
        {
            // The clipboard can be held open by another program. The link is still in the box and
            // selectable, so there is nothing worth interrupting the person with.
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();
}
