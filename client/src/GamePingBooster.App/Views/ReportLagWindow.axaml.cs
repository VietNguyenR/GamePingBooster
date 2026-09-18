using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using GamePingBooster.App.Services;
using GamePingBooster.Core.Ipc;
using GamePingBooster.App.Services.Localization;

namespace GamePingBooster.App.Views;

/// <summary>
/// "Report lag": measures every segment of the connection at once and sends the result.
///
/// Three panels, one at a time - consent, running, result - rather than three windows, because
/// the whole flow is one decision and one wait.
///
/// THE CONSENT IS THE FEATURE, not a formality in front of it. The tool this grew out of says in
/// its own documentation that nothing is sent anywhere, and the honest way to change that was to
/// ask every time, in words that name what leaves the machine: the trace carries the player's
/// gateway address and their ISP's routers. So the button that starts it is disabled until the
/// box is ticked, and unticking re-disables it - there is no path through this window that
/// measures anything before somebody has agreed to it.
///
/// Nothing is disconnected to take the measurement, and the text says so. That is not politeness:
/// rungs 6 and 7 are read from the LIVE tunnel, and a report taken with the tunnel down would
/// have four empty rows and nothing to compare them against. See LagDiagnostics.
/// </summary>
public partial class ReportLagWindow : SurfaceWindow
{
    private readonly Func<StatusMessage?> _latestStatus;
    private readonly string? _licenceUrl;
    private readonly string? _devicePublicKey;
    private readonly CancellationTokenSource _cts = new();

    public ReportLagWindow() : this(() => null, null, null)
    {
    }

    public ReportLagWindow(Func<StatusMessage?> latestStatus, string? licenceUrl, string? devicePublicKey)
    {
        _latestStatus = latestStatus;
        _licenceUrl = licenceUrl;
        _devicePublicKey = devicePublicKey;
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnConsentChanged(object? sender, RoutedEventArgs e)
    {
        // Re-read the box rather than toggling a flag: unticking has to disable the button again,
        // and a one-way flag is how a consent gate quietly stops being one.
        var check = this.FindControl<CheckBox>("ConsentCheck");
        var start = this.FindControl<Button>("StartButton");
        if (check is not null && start is not null) start.IsEnabled = check.IsChecked == true;
    }

    private async void OnStartClick(object? sender, RoutedEventArgs e)
    {
        var check = this.FindControl<CheckBox>("ConsentCheck");
        if (check?.IsChecked != true) return;

        var comment = this.FindControl<TextBox>("CommentBox")?.Text;
        Show("RunningPanel");

        var progressText = this.FindControl<TextBlock>("ProgressText");
        var progress = new Progress<string>(message =>
        {
            if (progressText is not null) progressText.Text = message;
        });

        try
        {
            var report = await LagDiagnostics
                .RunAsync(_latestStatus, LagDiagnostics.DefaultSeconds, progress, _cts.Token)
                .ConfigureAwait(true);

            await SendAsync(report, comment).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            Close();
        }
        catch (Exception ex)
        {
            Finish(Loc.T("lag.result.failed"), ex.Message);
        }
    }

    /// <summary>
    /// Uploads the report, and says plainly when it could not.
    ///
    /// A failed upload is NOT a failed measurement, and the two are reported differently: the run
    /// took twenty seconds of somebody's evening and its verdict is worth showing even when the
    /// licence server is unreachable. Whoever is helping can still be told what it said.
    /// </summary>
    private async Task SendAsync(LagDiagnostics.Report report, string? comment)
    {
        var title = Loc.T(report.Verdict == "clean" ? "lag.result.clean" : "lag.result.sent");
        var verdict = VerdictText(report);

        var refresh = RefreshTokenStore.Load();
        if (string.IsNullOrWhiteSpace(_licenceUrl) || refresh is null)
        {
            Finish(Loc.T("lag.result.notSent"), verdict + "\n\n" + Loc.T("lag.result.noLicence"));
            return;
        }

        try
        {
            using var client = new LicenceClient(_licenceUrl);
            await client.SendDiagnosticAsync(refresh, report, comment, _devicePublicKey, _cts.Token)
                .ConfigureAwait(true);
            Finish(title, verdict + "\n\n" + Loc.T("lag.result.thanks"));
        }
        catch (Exception ex)
        {
            Finish(Loc.T("lag.result.notSent"), verdict + "\n\n" + Loc.F("lag.result.uploadFailed", ex.Message));
        }
    }

    /// <summary>
    /// The verdict in the language the person chose.
    ///
    /// The report itself keeps its English VerdictText - that is what support reads on the server,
    /// in one language whoever sent it. This is only what goes on screen: the same sentence looked
    /// up by the verdict's key, with the measured reason filled in exactly as it was measured. A
    /// verdict this build has no sentence for shows the report's own English.
    /// </summary>
    private static string VerdictText(LagDiagnostics.Report report)
    {
        var key = "lag.verdict." + report.Verdict;
        if (!Loc.Has(key)) return report.VerdictText;

        var reason = report.Rungs.FirstOrDefault(r => r.Key == report.Verdict)?.BadReason;
        return Loc.F(key, reason is null ? "" : $" ({reason})");
    }

    private void Finish(string title, string text)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var titleBlock = this.FindControl<TextBlock>("ResultTitle");
            var textBlock = this.FindControl<TextBlock>("ResultText");
            if (titleBlock is not null) titleBlock.Text = title;
            if (textBlock is not null) textBlock.Text = text;
            Show("ResultPanel");
        });
    }

    private void Show(string panel)
    {
        foreach (var name in new[] { "ConsentPanel", "RunningPanel", "ResultPanel" })
        {
            var control = this.FindControl<StackPanel>(name);
            if (control is not null) control.IsVisible = name == panel;
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        _cts.Cancel();
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _cts.Cancel();
        _cts.Dispose();
        base.OnClosed(e);
    }
}
