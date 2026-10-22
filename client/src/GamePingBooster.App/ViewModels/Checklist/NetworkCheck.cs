using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Avalonia.Media;
using Avalonia.Threading;
using GamePingBooster.App.Services;
using GamePingBooster.App.Services.Localization;
using GamePingBooster.Core.Ipc;
using GamePingBooster.Core.Native;

namespace GamePingBooster.App.ViewModels.Checklist;

/// <summary>
/// Item 2: the link from this PC to the home router (pc-checklist-handoff.md). Advice only - the app cannot move the
/// PC closer to the router or plug a cable in.
///
/// Why it is here: over the 7 days to 2026-10-16, Wi-Fi players had 75.6 spikes an hour against 33.1 wired, and
/// router-caused spikes 34.6/h against 2.2/h. Routing cannot fix any of that.
///
/// Wired: one line "Mạng dây", and jitter to the router is still measured but only named when it is bad (a bad cable
/// or port does show). Wi-Fi: signal as Windows draws it, the band, and jitter to the router.
///
/// When the router is busy with another device at home (<see cref="HomeLineCheck.RouterBusy"/>), the same queue is what
/// raises the jitter to the router: the jitter is still shown but not judged, and the card says where it comes from, so
/// the player is not told to move closer to the router for a phone's download (owner, Viettel, 2026-10-20). A weak
/// signal is still judged.
///
/// Runs from the first status on, connected or not (owner, 2026-10-22: the panel is free and measures as the app opens).
/// The adapter is looked up from this user process, never in the service's route path
/// (slow-connect-was-netsh-and-adapter-enumeration).
/// </summary>
public sealed class NetworkCheck : CheckItem
{
    private static readonly Geometry WifiIcon = Geometry.Parse(
        "M1,6 C5,2 11,2 15,6 M3.5,8.5 C6,6 10,6 12.5,8.5 M6,11 C7.2,9.8 8.8,9.8 10,11 M8,13.5 V13.6");

    // Thresholds agreed in the handoff, to be confirmed with field data.
    private const double JitterWarnMs = 5;
    private const double JitterCriticalMs = 15;
    private const int SignalWarn = 70;
    private const int SignalCritical = 40;

    /// <summary>Echoes kept for the jitter figure: the last 30 s at one a second.</summary>
    private const int Window = 30;

    /// <summary>Answers needed before a jitter figure is shown at all.</summary>
    private const int MinAnswers = 10;

    private static readonly TimeSpan AdapterEvery = TimeSpan.FromSeconds(30);

    public override string Id => "network";
    public override Geometry Icon => WifiIcon;
    public override string Title => T("check.net.title");

    // Written by the measuring loop, read on the UI thread through Evaluate (posted after each change).
    private volatile Finding? _finding;
    private CancellationTokenSource? _loop;
    private HomeLineStatus? _homeLine;

    /// <summary>The relay while connected, so the line its traffic leaves by is the one measured; null before that.</summary>
    private volatile IPAddress? _relay;

    public override void OnStatus(StatusMessage status)
    {
        _homeLine = status.HomeLine;
        _relay = status.State == TunnelState.Connected && IPAddress.TryParse(status.RelayAddress, out var a) ? a : null;
        if (_loop is null)
        {
            _loop = new CancellationTokenSource();
            var token = _loop.Token;
            _ = Task.Run(() => MeasureAsync(token));
        }
        Evaluate();
    }

    public override void OnServiceGone()
    {
        _homeLine = null;
        Stop();
        Evaluate();
    }

    public override void OnLanguageChanged()
    {
        base.OnLanguageChanged();
        Evaluate();
    }

    /// <summary>Stops measuring. The last finding stays on screen: it is still the best thing known about the link.</summary>
    private void Stop()
    {
        _loop?.Cancel();
        _loop?.Dispose();
        _loop = null;
    }

    // ------------------------------------------------------------ measuring (thread pool)

    private sealed record Finding(bool Wireless, WifiLink? Wifi, double? JitterMs);

    private async Task MeasureAsync(CancellationToken token)
    {
        var rtts = new Queue<double?>();
        IPAddress? gateway = null;
        string? nicId = null;
        bool wireless = false;
        var adapterAt = DateTime.MinValue;
        IPAddress? relayAt = null;

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            do
            {
                // Looked up again at once when Connect names a relay: it may leave by another line than the default.
                var relay = _relay;
                if (DateTime.UtcNow - adapterAt >= AdapterEvery || !Equals(relay, relayAt))
                {
                    adapterAt = DateTime.UtcNow;
                    relayAt = relay;
                    var found = FindAdapter(relay);
                    if (found?.Id != nicId || !Equals(found?.Gateway, gateway)) rtts.Clear(); // another network
                    nicId = found?.Id;
                    gateway = found?.Gateway;
                    wireless = found?.Wireless ?? false;
                }
                if (nicId is null) continue;

                var wifi = wireless && Guid.TryParse(nicId, out var guid) ? WlanInfo.Query(guid) : null;

                if (gateway is not null)
                {
                    rtts.Enqueue(await EchoAsync(gateway));
                    while (rtts.Count > Window) rtts.Dequeue();
                }

                Publish(new Finding(wireless, wifi, Jitter(rtts)));
            }
            while (await timer.WaitForNextTickAsync(token));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Publish(Finding finding)
    {
        if (finding == _finding) return;
        _finding = finding;
        Dispatcher.UIThread.Post(Evaluate);
    }

    private static async Task<double?> EchoAsync(IPAddress gateway)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(gateway, 1000);
            return reply.Status == IPStatus.Success ? reply.RoundtripTime : null;
        }
        catch (Exception e) when (e is PingException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Mean change between consecutive answers (the RFC 3550 idea), rounded to whole ms because Ping reports whole ms.
    /// Null while too few answers came back to say: plenty of routers answer pings to themselves late or not at all
    /// while forwarding everything else fine, and a router that does not answer is not the player's problem.
    /// </summary>
    private static double? Jitter(IEnumerable<double?> rtts)
    {
        var answered = rtts.Where(r => r is not null).Select(r => r!.Value).ToList();
        if (answered.Count < MinAnswers) return null;
        double sum = 0;
        for (var i = 1; i < answered.Count; i++) sum += Math.Abs(answered[i] - answered[i - 1]);
        return Math.Round(sum / (answered.Count - 1));
    }

    private sealed record Adapter(string Id, IPAddress? Gateway, bool Wireless);

    /// <summary>
    /// The adapter the relay's traffic leaves by (asked of the routing table, same as the service's spike recorder),
    /// or the one carrying the default route before a relay is known. Never the Wintun adapter.
    /// </summary>
    private static Adapter? FindAdapter(IPAddress? relay)
    {
        try
        {
            var index = IpHelperInterop.BestInterfaceFor(relay ?? IPAddress.Parse("1.1.1.1"));
            Adapter? fallback = null;
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (nic.Description.Contains("Wintun", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var props = nic.GetIPProperties();
                    var gateway = props.GatewayAddresses
                        .Select(g => g.Address)
                        .FirstOrDefault(g => g.AddressFamily == AddressFamily.InterNetwork && !g.Equals(IPAddress.Any));
                    var adapter = new Adapter(nic.Id, gateway, nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211);
                    if (index is not null && props.GetIPv4Properties()?.Index == (int)index) return adapter;
                    if (gateway is not null) fallback ??= adapter;
                }
                catch (NetworkInformationException)
                {
                }
            }
            return fallback;
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------ what the line says (UI thread)

    private void Evaluate()
    {
        var f = _finding;
        if (f is null)
        {
            Severity = CheckSeverity.Unknown;
            Summary = T("check.net.measuring");
            Detail = null;
            return;
        }

        var routerBusy = f.JitterMs is not null && HomeLineCheck.RouterBusy(_homeLine, DateTimeOffset.UtcNow);
        var jitterSeverity = f.JitterMs switch
        {
            null => CheckSeverity.Good,
            _ when routerBusy => CheckSeverity.Good,
            >= JitterCriticalMs => CheckSeverity.Critical,
            >= JitterWarnMs => CheckSeverity.Warning,
            _ => CheckSeverity.Good,
        };

        if (!f.Wireless)
        {
            Severity = jitterSeverity;
            Summary = jitterSeverity == CheckSeverity.Good && !routerBusy
                ? T("check.net.wired")
                : Loc.F("check.net.wiredJitter", Ms(f.JitterMs!.Value));
            Detail = routerBusy ? T("check.net.routerBusy")
                : jitterSeverity == CheckSeverity.Good ? null : T("check.net.wiredAdvice");
            return;
        }

        var signalSeverity = f.Wifi?.SignalPercent switch
        {
            null => CheckSeverity.Good,
            < SignalCritical => CheckSeverity.Critical,
            < SignalWarn => CheckSeverity.Warning,
            _ => CheckSeverity.Good,
        };
        // CheckSeverity is ordered worst first.
        Severity = (CheckSeverity)Math.Min((int)jitterSeverity, (int)signalSeverity);

        var parts = new List<string> { "Wi-Fi" + BandText(f.Wifi?.Band) };
        if (f.Wifi is { } wifi) parts.Add(Loc.F("check.net.signal", wifi.SignalPercent));
        if (f.JitterMs is { } jitter) parts.Add(Loc.F("check.net.jitter", Ms(jitter)));
        Summary = string.Join(" · ", parts);

        var busyNote = routerBusy ? T("check.net.routerBusy") : null;
        if (Severity == CheckSeverity.Good)
        {
            Detail = busyNote;
            return;
        }
        var advice = f.Wifi?.Band == WifiBand.GHz24
            ? T("check.net.wifiAdvice") + " " + T("check.net.use5")
            : T("check.net.wifiAdvice");
        Detail = busyNote is null ? advice : busyNote + " " + advice;
    }

    /// <summary>" 5 GHz ✓" on the better bands, " 2.4 GHz" on the crowded one.</summary>
    private static string BandText(WifiBand? band) => band switch
    {
        WifiBand.GHz24 => " 2.4 GHz",
        WifiBand.GHz5 => " 5 GHz ✓",
        WifiBand.GHz6 => " 6 GHz ✓",
        _ => "",
    };

    private static string Ms(double ms) => ms.ToString("0", CultureInfo.InvariantCulture);
}
