using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using Avalonia.Media;
using Avalonia.Threading;
using GamePingBooster.App.Services;
using GamePingBooster.App.Services.Localization;
using GamePingBooster.Core.Ipc;
using GamePingBooster.Core.Native;

namespace GamePingBooster.App.ViewModels.Checklist;

/// <summary>
/// Item 3: apps downloading or uploading while the game runs (pc-checklist-handoff.md). A download fills the line and
/// the game's packets queue behind it - ping climbs and spikes for as long as it runs.
///
/// Two stages, so nothing per-process ever runs all the time (the 2026-10-17 resource audit's rule):
/// 1. Always while connected, every <see cref="Every"/>: the adapter's own byte counters (one GetIfEntry2 per adapter,
///    nothing per process), minus what the tunnel carries - the game's traffic, counted on the Wintun adapter and
///    grossed up by the tunnel's per-packet overhead. What is left is background.
/// 2. Only while a game runs and background is over the bar: the service counts TCP bytes per app for a few seconds
///    ("net-apps"), at most every <see cref="AskEvery"/>, and names them.
///
/// What the app can do about each: Windows Update gets "Tạm dừng" (the service pauses it until the game closes - see
/// the service's NetworkApps); Steam and Epic have no pause the app can press, so the line says where theirs is;
/// anything else is the player's to pause or close.
/// </summary>
public sealed class NetAppsCheck : CheckItem
{
    private static readonly Geometry ArrowsIcon = Geometry.Parse(
        "M5,2 V13 M2,10 L5,13 L8,10 M11,14 V3 M8,6 L11,3 L14,6");

    private static readonly TimeSpan Every = TimeSpan.FromSeconds(2);
    private const int Window = 3; // 3 x 2 s = 6 s

    private static readonly TimeSpan AdapterEvery = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan AskEvery = TimeSpan.FromSeconds(10);

    /// <summary>
    /// What the tunnel adds to each packet on the line: its data header and tag plus the outer IPv4/UDP headers, rounded
    /// up. Game packets are small, so leaving this out would count a third of the game's traffic as background.
    /// </summary>
    private const double TunnelOverheadBytes = 64;

    // Background traffic while playing, MB/s. A game itself moves well under 0.3 MB/s.
    private const double DownWarn = 1, UpWarn = 0.5, DownCritical = 5, UpCritical = 2;

    private const string WindowsUpdate = "windows-update", Steam = "steam", Epic = "epic";

    public override string Id => "net-apps";
    public override Geometry Icon => ArrowsIcon;
    public override string Title => T("check.apps.title");

    private readonly PipeClient _pipe;
    private volatile Reading? _reading;
    private CancellationTokenSource? _loop;
    private bool _gameRunning;

    /// <summary>The service's last answer, or null when none applies now. UI thread.</summary>
    private List<NetAppUsage>? _apps;
    private DateTime _askedAt = DateTime.MinValue;

    private bool _updatesPaused;
    private bool _pausing;
    private string? _pauseError;

    /// <summary>Background download when "Tạm dừng" was pressed, for the "3.2 → 0.1 MB/s" line once re-measured.</summary>
    private double? _pausedFrom;
    private DateTime _pausedAt;

    public NetAppsCheck(PipeClient pipe) => _pipe = pipe;

    public override void OnStatus(StatusMessage status)
    {
        if (status.AckVerb == "net-apps")
            _apps = status.CommandError is null ? status.NetApps ?? [] : _apps;
        if (status.AckVerb == "pause-updates")
        {
            _pausing = false;
            _pauseError = status.CommandError;
        }
        if (status.UpdatesPaused != _updatesPaused)
        {
            _updatesPaused = status.UpdatesPaused;
            if (!_updatesPaused) _pausedFrom = null;
            RaiseActions();
        }
        _gameRunning = status.GameRunning;
        if (!_gameRunning) _pauseError = null;

        if (status.State == TunnelState.Connected)
        {
            if (_loop is null)
            {
                _loop = new CancellationTokenSource();
                var token = _loop.Token;
                var relay = IPAddress.TryParse(status.RelayAddress, out var a) ? a : null;
                _ = Task.Run(() => MeasureAsync(relay, token));
            }
        }
        else if (status.State is TunnelState.Disconnected or TunnelState.Faulted)
        {
            Stop();
        }
        Evaluate();
    }

    public override void OnServiceGone()
    {
        Stop();
        _updatesPaused = false;
        RaiseActions();
        Evaluate();
    }

    public override void OnLanguageChanged()
    {
        base.OnLanguageChanged();
        Evaluate();
    }

    public override bool CanRestore => _updatesPaused;

    public override async Task RestoreAsync()
    {
        if (!_updatesPaused) return;
        await _pipe.SendAsync(new CommandMessage { Verb = "pause-updates", Enabled = false });
    }

    private void Stop()
    {
        _loop?.Cancel();
        _loop?.Dispose();
        _loop = null;
        _reading = null;
        _apps = null;
    }

    // ------------------------------------------------------------ measuring (thread pool)

    /// <summary>Background traffic on the line over the last few seconds, MB/s.</summary>
    private sealed record Reading(double DownMbps, double UpMbps);

    private sealed record Counts(long In, long Out, long PacketsIn, long PacketsOut);

    private async Task MeasureAsync(IPAddress? relay, CancellationToken token)
    {
        NetworkInterface? line = null, tunnel = null;
        var adapterAt = DateTime.MinValue;
        (Counts Line, Counts Tunnel, long At)? previous = null;
        var rates = new Queue<(double Down, double Up)>();

        using var timer = new PeriodicTimer(Every);
        try
        {
            do
            {
                if (DateTime.UtcNow - adapterAt >= AdapterEvery)
                {
                    adapterAt = DateTime.UtcNow;
                    var (foundLine, foundTunnel) = FindAdapters(relay);
                    if (foundLine?.Id != line?.Id)
                    {
                        previous = null;
                        rates.Clear();
                    }
                    (line, tunnel) = (foundLine, foundTunnel);
                }
                if (line is null) continue;

                var now = (Line: Read(line), Tunnel: tunnel is null ? null : Read(tunnel), At: Stopwatch.GetTimestamp());
                if (now.Line is null) continue;
                var tunnelNow = now.Tunnel ?? new Counts(0, 0, 0, 0);
                if (previous is { } prev)
                {
                    var seconds = Stopwatch.GetElapsedTime(prev.At, now.At).TotalSeconds;
                    var t = tunnelNow;
                    var tp = prev.Tunnel;
                    double down = (now.Line.In - prev.Line.In)
                                  - (t.In - tp.In) - (t.PacketsIn - tp.PacketsIn) * TunnelOverheadBytes;
                    double up = (now.Line.Out - prev.Line.Out)
                                - (t.Out - tp.Out) - (t.PacketsOut - tp.PacketsOut) * TunnelOverheadBytes;
                    if (seconds > 0)
                    {
                        rates.Enqueue((Math.Max(0, down) / seconds / (1024 * 1024), Math.Max(0, up) / seconds / (1024 * 1024)));
                        while (rates.Count > Window) rates.Dequeue();
                        _reading = new Reading(rates.Average(r => r.Down), rates.Average(r => r.Up));
                        Dispatcher.UIThread.Post(Evaluate);
                    }
                }
                previous = (now.Line, tunnelNow, now.At);
            }
            while (await timer.WaitForNextTickAsync(token));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static Counts? Read(NetworkInterface nic)
    {
        try
        {
            var s = nic.GetIPStatistics();
            return new Counts(s.BytesReceived, s.BytesSent,
                s.UnicastPacketsReceived + s.NonUnicastPacketsReceived, s.UnicastPacketsSent + s.NonUnicastPacketsSent);
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }

    /// <summary>The adapter the relay's traffic leaves by (as NetworkCheck finds it), and the Wintun adapter if up.</summary>
    private static (NetworkInterface? Line, NetworkInterface? Tunnel) FindAdapters(IPAddress? relay)
    {
        try
        {
            var index = IpHelperInterop.BestInterfaceFor(relay ?? IPAddress.Parse("1.1.1.1"));
            NetworkInterface? line = null, tunnel = null;
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (nic.Description.Contains("Wintun", StringComparison.OrdinalIgnoreCase))
                {
                    tunnel ??= nic;
                    continue;
                }
                try
                {
                    if (index is not null && nic.GetIPProperties().GetIPv4Properties()?.Index == (int)index) line = nic;
                }
                catch (NetworkInformationException)
                {
                }
            }
            return (line, tunnel);
        }
        catch (NetworkInformationException)
        {
            return (null, null);
        }
    }

    // ------------------------------------------------------------ what the line says (UI thread)

    private void Evaluate()
    {
        var r = _reading;
        if (r is null)
        {
            Severity = CheckSeverity.Unknown;
            Summary = _loop is null ? T("check.pending") : T("check.apps.measuring");
            Lines = [];
            return;
        }

        var rate = Rate(r.DownMbps, r.UpMbps);
        var lines = new List<DetailLine>();
        if (_updatesPaused)
        {
            lines.Add(new DetailLine(T("check.apps.paused")));
            if (_pausedFrom is { } from && DateTime.UtcNow - _pausedAt > TimeSpan.FromSeconds(8))
                lines.Add(new DetailLine(Loc.F("check.apps.pausedResult", Mb(from), Mb(r.DownMbps))));
        }
        if (_pauseError is not null) lines.Add(new DetailLine(T("check.apps.pauseFailed")));

        if (!_gameRunning)
        {
            // Before the game a download is the player's business: the bars are about the match.
            Severity = CheckSeverity.Good;
            Summary = Loc.F("check.apps.idle", rate);
            _apps = null;
            Lines = lines;
            return;
        }

        var severity = r.DownMbps >= DownCritical || r.UpMbps >= UpCritical ? CheckSeverity.Critical
            : r.DownMbps >= DownWarn || r.UpMbps >= UpWarn ? CheckSeverity.Warning
            : CheckSeverity.Good;
        if (severity == CheckSeverity.Good)
        {
            Severity = CheckSeverity.Good;
            Summary = Loc.F("check.apps.quiet", rate);
            _apps = null;
            Lines = lines;
            return;
        }

        Severity = severity;
        Summary = Loc.F("check.apps.busy", rate);
        if (DateTime.UtcNow - _askedAt >= AskEvery)
        {
            _askedAt = DateTime.UtcNow;
            _ = AskAsync();
        }

        if (_apps is null)
        {
            lines.Add(new DetailLine(T("check.apps.naming")));
        }
        else if (_apps.Count == 0)
        {
            lines.Add(new DetailLine(T("check.apps.unknown")));
        }
        else
        {
            lines.Add(new DetailLine(T("check.apps.using")));
            lines.AddRange(_apps.Select(a => AppLine(a, r)));
            if (_apps.Any(a => a.Key == Steam)) lines.Add(new DetailLine(T("check.apps.steamAdvice")));
            if (_apps.Any(a => a.Key == Epic)) lines.Add(new DetailLine(T("check.apps.epicAdvice")));
            if (_apps.Any(a => a.Key is not (WindowsUpdate or Steam or Epic))) lines.Add(new DetailLine(T("check.apps.advice")));
        }
        Lines = lines;
    }

    private DetailLine AppLine(NetAppUsage app, Reading r)
    {
        var text = "• " + app.Name + " " + Rate(app.DownMbps, app.UpMbps);
        List<LineButton>? buttons = null;
        if (app.Key == WindowsUpdate && !_updatesPaused)
            buttons = [new LineButton(_pausing ? T("check.apps.pausing") : T("check.apps.pause"), () => Pause(r))];
        else if (app.Key == Steam)
            buttons = [new LineButton(T("check.apps.openSteam"), OpenSteamDownloads)];
        return new DetailLine(text, IsEntry: true, buttons);
    }

    private async Task AskAsync()
    {
        try
        {
            await _pipe.SendAsync(new CommandMessage { Verb = "net-apps" });
        }
        catch (Exception)
        {
            _askedAt = DateTime.MinValue; // asked again on the next reading
        }
    }

    private void Pause(Reading r)
    {
        if (_pausing) return;
        _pausing = true;
        _pauseError = null;
        _pausedFrom = r.DownMbps;
        _pausedAt = DateTime.UtcNow;
        Evaluate();
        _ = SendPauseAsync();
    }

    private async Task SendPauseAsync()
    {
        try
        {
            await _pipe.SendAsync(new CommandMessage { Verb = "pause-updates", Enabled = true });
        }
        catch (Exception)
        {
            _pausing = false;
            _pauseError = "unreachable";
            Evaluate();
        }
    }

    /// <summary>Steam's own Downloads page, where its pause button is. A fixed URL to Steam's registered handler.</summary>
    private static void OpenSteamDownloads()
    {
        try
        {
            Process.Start(new ProcessStartInfo("steam://open/downloads") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception)
        {
            // Steam not installed or its handler broken: the advice line says where to look.
        }
    }

    /// <summary>"↓3.2 MB/s", with "↑0.4 MB/s" beside it when the upload is worth a mention.</summary>
    private static string Rate(double down, double up) =>
        up >= 0.1 ? "↓" + Mb(down) + " ↑" + Mb(up) : "↓" + Mb(down);

    private static string Mb(double mbps) =>
        mbps.ToString(mbps < 10 ? "0.0" : "0", CultureInfo.InvariantCulture) + " MB/s";
}
