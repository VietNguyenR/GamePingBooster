using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using GamePingBooster.App.Services;
using GamePingBooster.App.ViewModels;
using GamePingBooster.Core.Ipc;

namespace GamePingBooster.App.Views;

/// <summary>
/// The in-app update: confirm, download, install. One window with a panel per step, like
/// ReportLagWindow, because it is one decision followed by one wait.
///
/// The order of the steps is deliberate. The download comes BEFORE the disconnect: it can take a
/// minute on a slow line and can fail or be cancelled, and none of that is a reason to have dropped
/// somebody's connection. Only a verified installer in hand is worth disconnecting for. See
/// UpdateInstaller for why the installer does the actual updating.
/// </summary>
public partial class UpdateWindow : SurfaceWindow
{
    private readonly AvailableUpdate _update;
    private readonly MainViewModel? _vm;
    private readonly CancellationTokenSource _cts = new();

    public UpdateWindow() : this(new AvailableUpdate("0.0.0", ""), null)
    {
    }

    public UpdateWindow(AvailableUpdate update, MainViewModel? vm)
    {
        _update = update;
        _vm = vm;
        InitializeComponent();

        Find<TextBlock>("TitleText").Text = $"Update to v{update.Version}";
        var current = UpdateChecker.CurrentVersion();
        Find<TextBlock>("SubtitleText").Text = update.InstallerSize is { } size
            ? $"You have v{current}. Download size {FormatMb(size)}."
            : $"You have v{current}.";

        if (Warning() is { } warning)
        {
            Find<TextBlock>("WarningText").Text = warning;
            Find<Border>("WarningBox").IsVisible = true;
        }
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private T Find<T>(string name) where T : Control =>
        this.FindControl<T>(name) ?? throw new InvalidOperationException($"{name} is missing from UpdateWindow.axaml");

    /// <summary>
    /// What the update will interrupt, or null when nothing. A running game gets the stronger
    /// wording: an update in the middle of a match takes the relay away from it.
    /// </summary>
    private string? Warning()
    {
        if (_vm is null) return null;

        if (_vm.GameRunning)
        {
            var game = _vm.GameName ?? "A game";
            return $"{game} is running. If you are in a match, finish it first: updating disconnects you, " +
                   "and the game goes back to your normal internet route until you connect again.";
        }

        return _vm.State is TunnelState.Disconnected
            ? null
            : "You are connected. The update will disconnect you while it installs; press Connect again when the app reopens.";
    }

    private void Show(string panel)
    {
        foreach (var name in new[] { "ConfirmPanel", "DownloadPanel", "InstallPanel", "FailedPanel" })
        {
            Find<StackPanel>(name).IsVisible = name == panel;
        }
    }

    private async void OnUpdateClick(object? sender, RoutedEventArgs e)
    {
        Show("DownloadPanel");

        var bar = Find<ProgressBar>("DownloadBar");
        var text = Find<TextBlock>("DownloadText");
        var total = _update.InstallerSize;
        bar.IsIndeterminate = total is null;

        // Progress<T> is created here, on the UI thread, so its reports arrive back on it.
        var progress = new Progress<long>(received =>
        {
            if (total is { } t && t > 0)
            {
                bar.Value = Math.Min(1.0, (double)received / t);
                text.Text = $"{FormatMb(received)} / {FormatMb(t)}";
            }
            else
            {
                text.Text = FormatMb(received);
            }
        });

        string installer;
        try
        {
            installer = await UpdateInstaller.DownloadAsync(_update, progress, _cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            return; // Cancelled, or the window was closed. Nothing was changed.
        }
        catch (Exception ex)
        {
            Fail(ex switch
            {
                InvalidDataException or TimeoutException => ex.Message,
                _ => "The download failed. Check your internet connection and try again, or download the installer from the release page.",
            });
            return;
        }

        Find<TextBlock>("InstallText").Text = "Waiting for Windows permission to install...";
        Show("InstallPanel");

        // Now, and not before: see the class summary.
        if (_vm is not null && _vm.State is not TunnelState.Disconnected)
        {
            Find<TextBlock>("InstallText").Text = "Disconnecting...";
            await _vm.DisconnectAndWaitAsync(TimeSpan.FromSeconds(6)).ConfigureAwait(true);
            Find<TextBlock>("InstallText").Text =
                "Waiting for Windows permission to install. Game Ping Booster will close and open again by itself.";
        }

        // When setup goes ahead it closes this app, so everything after this line only runs when
        // it did not.
        var code = await UpdateInstaller.RunAsync(installer).ConfigureAwait(true);

        Fail(code switch
        {
            null => "The installer did not start. If you answered No when Windows asked for permission, press Update to try again.",
            0 => "The update is installed. Close Game Ping Booster and open it again to use the new version.",
            // PrepareToInstall refused: the old service could not be removed. See GamePingBooster.iss.
            7 => "The update could not replace the running service. If the Services window is open, close it and try again.",
            _ => $"The update was not installed (setup ended with code {code}). If you answered No when Windows " +
                 "asked for permission, press Update to try again. Otherwise, download the installer from the release page.",
        });
    }

    private void Fail(string message)
    {
        Find<TextBlock>("FailedText").Text = message;
        Show("FailedPanel");
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        _cts.Cancel();
        Close();
    }

    private void OnReleasePageClick(object? sender, RoutedEventArgs e) => BrowserLauncher.TryOpen(_update.Url);

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        // A download still running when the window goes stops with it.
        _cts.Cancel();
        _cts.Dispose();
        base.OnClosed(e);
    }

    private static string FormatMb(long bytes) =>
        (bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
}
