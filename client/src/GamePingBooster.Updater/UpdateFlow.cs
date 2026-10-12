using System.Diagnostics;
using System.Globalization;
using GamePingBooster.Core.Ipc;
using GamePingBooster.Core.Updates;

namespace GamePingBooster.Updater;

internal enum StepState { Pending, Active, Done, Failed }

/// <summary>What the window draws. Rebuilt by <see cref="UpdateFlow"/>; read by <see cref="SplashWindow"/> on paint.</summary>
internal sealed class View
{
    public const int StepCount = 8;
    public const int Check = 0, Download = 1, Verify = 2, Stop = 3, Copy = 4, Configure = 5, Restart = 6, Open = 7;

    /// <summary>Only the title, a line and the bar - while checking, before there is anything to install.</summary>
    public bool Compact = true;
    public string Title = "";
    public string Detail = "";
    public string? Versions;
    /// <summary>0..1, or null for the moving bar.</summary>
    public double? Progress;
    public bool Error;
    public readonly StepState[] Steps = new StepState[StepCount];
    /// <summary>A short figure beside the active step: "62%".</summary>
    public string? Note;
    public string? Footer;
}

/// <summary>Arguments the app starts the window with. See UpdaterLauncher in the app.</summary>
internal sealed class Options
{
    /// <summary>"check": the app is starting and asked for a check. "install": the service offered an update ("ready").</summary>
    public string Mode = "check";
    /// <summary>Progress written before this (UTC) is from an earlier run and ignored.</summary>
    public DateTime Since = DateTime.UtcNow;
    /// <summary>The app that started this window, while it is still open.</summary>
    public int? UiPid;
    /// <summary>GamePingBooster.exe, installed - started again once the update is in.</summary>
    public string? AppPath;
    public string? Lang;
    /// <summary>--demo: plays a whole update with made-up progress, to look at the window. Touches nothing.</summary>
    public bool Demo;

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            switch (args[i])
            {
                case "--mode": o.Mode = Next() ?? o.Mode; break;
                case "--since":
                    if (long.TryParse(Next(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms))
                        o.Since = DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
                    break;
                case "--ui-pid":
                    if (int.TryParse(Next(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid)) o.UiPid = pid;
                    break;
                case "--app": o.AppPath = Next(); break;
                case "--lang": o.Lang = Next(); break;
                case "--demo": o.Demo = true; break;
            }
        }
        return o;
    }
}

/// <summary>
/// Follows one automatic update from the outside and decides what the window says.
///
/// Three sources, one after the other (UpdateProgress has the details):
///   1. The progress file the service writes while it checks, downloads and verifies.
///   2. The same file, written by setup while it closes the app, stops the service, copies and configures.
///   3. After setup: the service itself - running, its pipe listening, and the installed version the one asked for.
/// Then the window starts the app (as the user - this process already is the user) and closes once its window is up.
///
/// Every wait has an end. A window that sits on "Installing…" forever is the very failure this exists to
/// prevent, so a stalled step is reported as a failure and the app is opened again whatever happened: the
/// version that was there keeps working when setup gives up (AutoUpdater.SetupCommand starts it again).
/// </summary>
internal sealed class UpdateFlow
{
    private const string ServiceName = "GamePingBooster";
    private const uint ServiceRunning = 4;

    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan DownloadStall = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan SetupStall = TimeSpan.FromSeconds(150);
    private static readonly TimeSpan ServiceWait = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan OldServiceWait = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan AppWindowWait = TimeSpan.FromSeconds(15);
    /// <summary>Never shorter than this on screen: a check that answers in 50 ms must not look like a flicker.</summary>
    private static readonly TimeSpan MinimumShown = TimeSpan.FromSeconds(1.2);

    private enum Stage { Checking, Downloading, Verifying, Installing, WaitingService, FailedWaitingOld, Opening, Closing }

    private readonly Options _o;
    private readonly Strings _s;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly string? _appDir;

    private Stage _stage = Stage.Checking;
    private bool _committed;
    private bool _failed;
    private string? _target;
    private string? _from;
    private DateTime _lastWrite = DateTime.MinValue;
    private TimeSpan _lastChangeAt = TimeSpan.Zero;
    private TimeSpan _stageStartedAt = TimeSpan.Zero;
    private TimeSpan? _closeAt;
    private Process? _app;
    private readonly Queue<(TimeSpan At, long Bytes)> _samples = new();

    public View View { get; } = new();

    /// <summary>The window should close now.</summary>
    public bool Finished { get; private set; }

    public UpdateFlow(Options options, Strings strings)
    {
        _o = options;
        _s = strings;
        _appDir = options.AppPath is { } app ? Path.GetDirectoryName(app) : null;
        View.Title = _s.Checking;
        View.Detail = _s.CheckingDetail;
        if (options.Mode == "install")
        {
            // Offered by the service: the release is downloaded and verified already, setup is next.
            View.Compact = false;
            View.Title = _s.Preparing;
            View.Detail = _s.StartingSetup;
            MarkUpTo(View.Stop);
            View.Footer = _s.FooterInstalling;
        }
        else
        {
            View.Steps[View.Check] = StepState.Active;
        }
    }

    private TimeSpan Now => _clock.Elapsed;

    /// <summary>Called a few times a second from the window's timer.</summary>
    public void Tick()
    {
        if (Finished) return;
        if (_closeAt is { } closeAt)
        {
            if (Now >= closeAt) Finished = true;
            // Still watch the app's window while the last line is up.
            if (_stage == Stage.Opening) WatchApp();
            return;
        }

        if (_o.Demo) DemoTick();
        else ReadProgress();

        switch (_stage)
        {
            case Stage.Checking when Now - _lastChangeAt > CheckTimeout && Now - _stageStartedAt > CheckTimeout:
                // No answer at all: open the app. The service carries on, and offers the update again if it finds one.
                CloseSoon(TimeSpan.Zero);
                break;
            case Stage.Downloading when Now - _lastChangeAt > DownloadStall:
                FailBeforeInstall(_s.DownloadFailed);
                break;
            case Stage.Installing when Now - _lastChangeAt > SetupStall:
                FailAfterCommit(_s.ReasonTimeout);
                break;
            case Stage.WaitingService when !_o.Demo:
                WatchNewService();
                break;
            case Stage.FailedWaitingOld:
                WatchOldService();
                break;
            case Stage.Opening:
                WatchApp();
                break;
        }

        // The app that asked closed before anything was installed: there is nobody left to show anything to.
        if (!_committed && _closeAt is null && _o.UiPid is not null && UiGone()) CloseSoon(TimeSpan.Zero);
    }

    // ------------------------------------------------------------------ reading the file

    private void ReadProgress()
    {
        string text;
        DateTime written;
        try
        {
            var path = UpdateProgress.FilePath;
            if (!File.Exists(path)) return;
            written = File.GetLastWriteTimeUtc(path);
            // Anything from before this window was started belongs to another run.
            if (written < _o.Since.AddSeconds(-1) || written == _lastWrite) return;
            text = File.ReadAllText(path);
        }
        catch (Exception)
        {
            return; // Being replaced right now: next tick.
        }
        var values = UpdateProgress.Parse(text);
        if (values is null) return;
        _lastWrite = written;
        _lastChangeAt = Now;
        Apply(values);
    }

    private void Apply(Dictionary<string, string> v)
    {
        var phase = v["phase"];
        if (v.TryGetValue("version", out var version) && version.Length > 0) _target = version;
        if (v.TryGetValue("from", out var from) && from.Length > 0) _from ??= from;
        long? done = v.TryGetValue("done", out var d) && long.TryParse(d, CultureInfo.InvariantCulture, out var dn) ? dn : null;
        long? total = v.TryGetValue("total", out var t) && long.TryParse(t, CultureInfo.InvariantCulture, out var tn) && tn > 0 ? tn : null;
        v.TryGetValue("reason", out var reason);
        View.Versions = _from is not null && _target is not null ? $"v{_from} → v{_target}" : null;

        // Once failed, only the way back to the old version is followed.
        if (_failed) return;

        switch (phase)
        {
            case UpdateProgress.Checking:
                SetStage(Stage.Checking);
                View.Title = _s.Checking;
                View.Detail = _s.CheckingDetail;
                View.Progress = null;
                break;

            case UpdateProgress.None when !_committed:
                NothingToInstall(reason);
                break;

            case UpdateProgress.Downloading:
                SetStage(Stage.Downloading);
                View.Compact = false;
                MarkUpTo(View.Download);
                View.Title = _s.Downloading + (_target is not null ? $" v{_target}" : "");
                View.Footer = _s.FooterDownloading;
                ShowDownload(done ?? 0, total);
                break;

            case UpdateProgress.Verifying:
                SetStage(Stage.Verifying);
                View.Compact = false;
                MarkUpTo(View.Verify);
                View.Title = _s.Verifying;
                View.Detail = _s.VerifyingDetail;
                View.Progress = null;
                View.Note = null;
                break;

            case UpdateProgress.Starting or UpdateProgress.ClosingApp or UpdateProgress.StoppingService:
                Commit();
                MarkUpTo(View.Stop);
                View.Detail = phase switch
                {
                    UpdateProgress.ClosingApp => _s.ClosingApp,
                    UpdateProgress.StoppingService => _s.StoppingService,
                    _ => _s.StartingSetup,
                };
                View.Progress = null;
                break;

            case UpdateProgress.Copying:
                Commit();
                MarkUpTo(View.Copy);
                var percent = done is { } c && total is { } all ? Math.Clamp((double)c / all, 0, 1) : (double?)null;
                View.Progress = percent;
                View.Note = percent is { } p ? $"{p * 100:F0}%" : null;
                View.Detail = string.Format(_s.Copying, percent is { } q ? (q * 100).ToString("F0", CultureInfo.InvariantCulture) : "…");
                break;

            case UpdateProgress.RegisteringService or UpdateProgress.StartingService
                or UpdateProgress.InstallingDriver or UpdateProgress.Firewall:
                Commit();
                MarkUpTo(View.Configure);
                View.Progress = null;
                View.Note = null;
                View.Detail = phase switch
                {
                    UpdateProgress.RegisteringService => _s.RegisteringService,
                    UpdateProgress.StartingService => _s.StartingService,
                    UpdateProgress.InstallingDriver => _s.InstallingDriver,
                    _ => _s.Firewall,
                };
                break;

            case UpdateProgress.SetupDone:
                Commit();
                MarkUpTo(View.Restart);
                SetStage(Stage.WaitingService);
                View.Title = _s.Restarting;
                View.Detail = _s.WaitingService;
                View.Progress = null;
                View.Note = null;
                break;

            case UpdateProgress.SetupFailed or UpdateProgress.SetupEnded or UpdateProgress.Failed:
                if (!_committed)
                {
                    FailBeforeInstall(reason == UpdateProgress.ReasonSignature ? _s.SignatureFailed : _s.DownloadFailed);
                }
                else
                {
                    // Ours, not setup's sentence: one line has room for a few words, and setup's reason is in setup.log.
                    FailAfterCommit(_s.ReasonSetup);
                }
                break;
        }
    }

    private void SetStage(Stage stage)
    {
        if (_stage == stage) return;
        _stage = stage;
        _stageStartedAt = Now;
    }

    /// <summary>Setup has started: the app is being closed, and from here only the way to a running app is followed.</summary>
    private void Commit()
    {
        View.Compact = false;
        View.Footer = _s.FooterInstalling;
        if (_target is not null) View.Title = string.Format(_s.Installing, _target);
        if (_committed) return;
        _committed = true;
        SetStage(Stage.Installing);
    }

    /// <summary>Every step before <paramref name="active"/> done, it active, the rest pending.</summary>
    private void MarkUpTo(int active)
    {
        for (var i = 0; i < View.StepCount; i++)
        {
            View.Steps[i] = i < active ? StepState.Done : i == active ? StepState.Active : StepState.Pending;
        }
    }

    private void ShowDownload(long done, long? total)
    {
        _samples.Enqueue((Now, done));
        while (_samples.Count > 1 && Now - _samples.Peek().At > TimeSpan.FromSeconds(3)) _samples.Dequeue();
        var first = _samples.Peek();
        var seconds = (Now - first.At).TotalSeconds;
        var speed = seconds > 0.4 ? (done - first.Bytes) / seconds : 0;
        var speedText = speed > 0 ? $"{Mb(speed)} MB/s" : "…";

        if (total is { } all)
        {
            View.Progress = Math.Clamp((double)done / all, 0, 1);
            View.Note = $"{View.Progress * 100:F0}%";
            View.Detail = string.Format(_s.DownloadDetail, Mb(done), Mb(all), speedText);
        }
        else
        {
            View.Progress = null;
            View.Note = null;
            View.Detail = string.Format(_s.DownloadDetailNoTotal, Mb(done), speedText);
        }
    }

    /// <summary>Megabytes with one decimal, written the way the reader writes numbers.</summary>
    private string Mb(double bytes)
    {
        var text = (bytes / (1024 * 1024)).ToString("F1", CultureInfo.InvariantCulture);
        return ReferenceEquals(_s, Strings.Vietnamese) ? text.Replace('.', ',') : text;
    }

    // ------------------------------------------------------------------ endings

    private void NothingToInstall(string? reason)
    {
        View.Progress = 1;
        View.Note = null;
        if (reason is null or UpdateProgress.ReasonLatest)
        {
            View.Title = _s.Latest;
            View.Detail = _from is not null ? string.Format(_s.LatestDetail, _from) : "";
            View.Steps[View.Check] = StepState.Done;
            CloseSoon(TimeSpan.FromSeconds(0.7));
            return;
        }
        View.Title = _s.NotNow;
        View.Detail = reason switch
        {
            UpdateProgress.ReasonBusy => _s.ReasonBusy,
            UpdateProgress.ReasonGame => _s.ReasonGame,
            UpdateProgress.ReasonLater => _s.ReasonLater,
            UpdateProgress.ReasonRefused => _s.ReasonRefused,
            UpdateProgress.ReasonOff => _s.ReasonOff,
            _ => _s.ReasonUnavailable,
        };
        CloseSoon(TimeSpan.FromSeconds(2.5));
    }

    /// <summary>Nothing was touched yet - download or signature. The app that is still open shows itself when this closes.</summary>
    private void FailBeforeInstall(string title)
    {
        _failed = true;
        View.Error = true;
        View.Progress = 1;
        View.Note = null;
        View.Title = title;
        View.Detail = _s.TryLater;
        View.Footer = _s.FooterFailed;
        for (var i = 0; i < View.StepCount; i++)
        {
            if (View.Steps[i] == StepState.Active) View.Steps[i] = StepState.Failed;
        }
        CloseSoon(TimeSpan.FromSeconds(3.5));
    }

    /// <summary>Setup ran and did not finish. The old service comes back (AutoUpdater.SetupCommand); the app is opened on it.</summary>
    private void FailAfterCommit(string why)
    {
        if (_failed) return;
        _failed = true;
        View.Error = true;
        View.Progress = 1;
        View.Note = null;
        View.Title = _s.SetupFailed;
        View.Detail = why + " " + _s.ReopeningOld;
        View.Footer = _s.FooterFailed;
        for (var i = 0; i < View.StepCount; i++)
        {
            if (View.Steps[i] == StepState.Active) View.Steps[i] = StepState.Failed;
        }
        SetStage(Stage.FailedWaitingOld);
    }

    private void CloseSoon(TimeSpan after)
    {
        var at = Now + after;
        if (at < MinimumShown) at = MinimumShown;
        _closeAt = at;
        _stage = _stage == Stage.Opening ? Stage.Opening : Stage.Closing;
    }

    // ------------------------------------------------------------------ after setup

    private void WatchNewService()
    {
        var running = Native.ServiceState(ServiceName) == ServiceRunning;
        if (running && Native.PipeListening(IpcConstants.PipeName))
        {
            var installed = InstalledVersion();
            if (_target is not null && installed is not null && !ReleaseFeed.SameVersion(installed, _target))
            {
                FailAfterCommit(_s.ReasonSetup);
                return;
            }
            View.Steps[View.Restart] = StepState.Done;
            View.Steps[View.Open] = StepState.Active;
            View.Title = string.Format(_s.Updated, _target ?? installed ?? "");
            View.Detail = _s.Opening;
            View.Progress = 1;
            OpenApp();
            return;
        }
        if (Now - _stageStartedAt > ServiceWait)
        {
            // Running without a pipe after a minute is still a service; nothing at all is a failure.
            if (running) OpenApp();
            else FailAfterCommit(_s.ReasonTimeout);
        }
    }

    private void WatchOldService()
    {
        var running = Native.ServiceState(ServiceName) == ServiceRunning;
        if (running || Now - _stageStartedAt > OldServiceWait) OpenApp();
    }

    private string? InstalledVersion()
    {
        try
        {
            if (_appDir is null) return null;
            var info = FileVersionInfo.GetVersionInfo(Path.Combine(_appDir, "gpb-service.exe"));
            return info.ProductVersion?.Split('+')[0];
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Starts the app as this user - unless the one that started this window is still open, which shows itself.</summary>
    private void OpenApp()
    {
        SetStage(Stage.Opening);
        if (_o.Demo || !UiGone() || _o.AppPath is null || !File.Exists(_o.AppPath))
        {
            FinishOpening();
            return;
        }
        try
        {
            _app = Process.Start(new ProcessStartInfo(_o.AppPath, "--after-update")
            {
                UseShellExecute = false,
                WorkingDirectory = _appDir ?? "",
            });
        }
        catch (Exception)
        {
            _app = null;
        }
        if (_app is null) FinishOpening();
    }

    private void WatchApp()
    {
        if (_app is null)
        {
            FinishOpening();
            return;
        }
        try
        {
            _app.Refresh();
            if (_app.HasExited || _app.MainWindowHandle != 0 || Now - _stageStartedAt > AppWindowWait) FinishOpening();
        }
        catch (Exception)
        {
            FinishOpening();
        }
    }

    private bool _finishing;

    private void FinishOpening()
    {
        if (_finishing) return;
        _finishing = true;
        if (!_failed) View.Steps[View.Open] = StepState.Done;
        CloseSoon(TimeSpan.FromSeconds(_failed ? 3 : 1.2));
    }

    private bool UiGone()
    {
        if (_o.UiPid is not { } pid) return true;
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.HasExited;
        }
        catch (Exception)
        {
            return true;
        }
    }

    // ------------------------------------------------------------------ --demo

    /// <summary>A whole update in about 25 seconds, with made-up numbers, through the same Apply as the real one.</summary>
    private void DemoTick()
    {
        var t = Now.TotalSeconds;
        const long size = 20_233_584;
        var script = new (double At, string Phase, long? Done, long? Total)[]
        {
            (0.3, UpdateProgress.Checking, null, null),
            (1.6, UpdateProgress.Downloading, 0, size),
            (8.0, UpdateProgress.Verifying, null, null),
            (9.2, UpdateProgress.Starting, null, null),
            (10.0, UpdateProgress.ClosingApp, null, null),
            (10.8, UpdateProgress.StoppingService, null, null),
            (12.0, UpdateProgress.Copying, 0, 100),
            (17.0, UpdateProgress.RegisteringService, null, null),
            (17.8, UpdateProgress.StartingService, null, null),
            (18.6, UpdateProgress.InstallingDriver, null, null),
            (19.6, UpdateProgress.Firewall, null, null),
            (20.4, UpdateProgress.SetupDone, null, null),
        };
        var current = script.LastOrDefault(s => s.At <= t);
        if (current.Phase is null) return;

        long? done = current.Done;
        if (current.Phase == UpdateProgress.Downloading) done = (long)(Math.Min(1, (t - 1.6) / 6.2) * size);
        if (current.Phase == UpdateProgress.Copying) done = (long)(Math.Min(1, (t - 12.0) / 4.8) * 100);

        _lastChangeAt = Now;
        var values = new Dictionary<string, string>
        {
            ["phase"] = current.Phase,
            ["version"] = "0.5.1",
            ["from"] = "0.5.0",
        };
        if (done is not null) values["done"] = done.Value.ToString(CultureInfo.InvariantCulture);
        if (current.Total is not null) values["total"] = current.Total.Value.ToString(CultureInfo.InvariantCulture);
        Apply(values);

        if (_stage == Stage.WaitingService && t > 22.5)
        {
            View.Steps[View.Restart] = StepState.Done;
            View.Steps[View.Open] = StepState.Active;
            View.Title = string.Format(_s.Updated, _target);
            View.Detail = _s.Opening;
            View.Progress = 1;
            SetStage(Stage.Opening);
            _app = null;
            _stageStartedAt = Now;
            FinishOpening();
        }
    }
}
