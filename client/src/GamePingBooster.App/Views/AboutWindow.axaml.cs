using System.Diagnostics;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using GamePingBooster.App.ViewModels;

namespace GamePingBooster.App.Views;

public partial class AboutWindow : SurfaceWindow
{
    public AboutWindow()
    {
        InitializeComponent();
        DataContext = new AboutViewModel();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnRepositoryClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is AboutViewModel vm) Open(vm.RepositoryUrl);
    }

    private void OnEmailClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is AboutViewModel vm) Open($"mailto:{vm.Email}");
    }

    /// <summary>
    /// Hands a URL to the shell, which is the only way to open a browser without knowing or
    /// caring which one the person uses.
    ///
    /// UseShellExecute is required: without it Process.Start wants an executable, and a URL is
    /// not one. Failures are swallowed - a machine with no default browser, or no mail client
    /// for the mailto, is not a reason to throw out of a click handler on an About box.
    /// </summary>
    private static void Open(string target)
    {
        // Web links go through BrowserLauncher, which survives a broken default-browser setting.
        // mailto: has no such fallback - there is no "installed mail clients" list worth trusting -
        // so it stays with the shell.
        if (target.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            Services.BrowserLauncher.TryOpen(target);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // Nothing useful to do, and nothing lost: the address is on screen and can be
            // copied.
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
