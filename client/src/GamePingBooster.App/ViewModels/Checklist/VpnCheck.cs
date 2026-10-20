using System.Net;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using Avalonia.Media;
using Avalonia.Threading;
using GamePingBooster.App.Services;
using GamePingBooster.App.Services.Localization;
using GamePingBooster.Core.Ipc;
using GamePingBooster.Core.Native;

namespace GamePingBooster.App.ViewModels.Checklist;

/// <summary>
/// Item 7: a VPN holding the line, or another booster open (pc-checklist-handoff.md). Advice only - closing someone
/// else's software is the player's call.
///
/// Judged by what each one does, measured on the owner's PC 2026-10-20:
/// - A VPN shows as an adapter and takes the routes. Cloudflare WARP does it with ~30 split routes (0.0.0.0/5,
///   8.0.0.0/7 ... via 192.0.2.1), never a 0.0.0.0/0, so "who holds the default route" misses it. The question asked
///   is Windows' own: which interface does a packet to the internet leave by (GetBestInterfaceEx). A tunnel there means
///   the game's lobby, its ping probes and DNS go through the VPN, not the player's ISP. Hyper-V switches are not
///   tunnels - on a host with an external switch the vEthernet adapter is the real line.
/// - GearUP and LagoFast change no adapter and no route: they capture the game's packets in a kernel filter
///   (gunetfilter, JYNetFilter; ExitLag ships nfextlag). The drivers stay loaded after the app is closed, so only the
///   app's own process says it is open. Two boosters both grabbing the game's traffic is the conflict.
///
/// Reads without Connect, like item 5: a player sees it before pressing "Bắt đầu tăng tốc".
/// </summary>
public sealed class VpnCheck : CheckItem
{
    private static readonly Geometry ShieldIcon = Geometry.Parse("M8,1 L14,3.5 V8 C14,11.5 11.3,14 8,15 C4.7,14 2,11.5 2,8 V3.5 Z");

    private static readonly TimeSpan Every = TimeSpan.FromSeconds(5);

    /// <summary>Internet addresses asked about. Two, so a VPN that excludes one well-known resolver is still seen.</summary>
    private static readonly IPAddress[] Probes = [IPAddress.Parse("1.1.1.1"), IPAddress.Parse("8.8.8.8")];

    /// <summary>
    /// Adapter names and descriptions that are VPN tunnels even when the driver reports plain Ethernet (TAP). The
    /// programs to name - boosters by their app process, VPNs by adapter words - are <see cref="ConflictApps"/>: built in,
    /// changed from the server.
    /// </summary>
    private static readonly Regex TunnelNames = new(
        "tap-windows|tap-win32|wireguard|wintun|openvpn|nordlynx|tailscale|zerotier|radmin|hamachi|cloudflare|warp|" +
        "vpn|fortinet|forticlient|anyconnect|globalprotect|pangp|softether|proton|surfshark|expressvpn",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>IANA ifTypes of a tunnel: ppp, propVirtual (WARP, Wintun, OpenVPN DCO), tunnel.</summary>
    private static readonly HashSet<int> TunnelTypes = [23, 53, 131];

    public override string Id => "vpn";
    public override Geometry Icon => ShieldIcon;
    public override string Title => T("check.vpn.title");

    private sealed record Reading(string? Vpn, IReadOnlyList<string> Boosters);

    private volatile Reading? _reading;
    private bool _gameRunning;

    /// <summary>The adapter's name for our own Wintun adapter - never a VPN in the way.</summary>
    private readonly string _ownAdapter = "Game Ping Booster";

    public VpnCheck()
    {
        Evaluate();
        _ = Task.Run(MeasureAsync);
    }

    public override void OnStatus(StatusMessage status)
    {
        // The list can change on the licence server: asked once per run, used from the next scan.
        _ = ConflictApps.RefreshOnceAsync(status.LicenceUrl);

        if (status.GameRunning == _gameRunning) return;
        _gameRunning = status.GameRunning;
        Evaluate();
    }

    public override void OnServiceGone()
    {
        _gameRunning = false;
        Evaluate();
    }

    public override void OnLanguageChanged()
    {
        base.OnLanguageChanged();
        Evaluate();
    }

    // ------------------------------------------------------------ measuring (thread pool)

    private async Task MeasureAsync()
    {
        using var timer = new PeriodicTimer(Every);
        uint? lineIndex = null; // the last interface found not to be a tunnel: no adapter walk while it stays
        do
        {
            try
            {
                string? vpn = null;
                foreach (var probe in Probes)
                {
                    var index = IpHelperInterop.BestInterfaceFor(probe);
                    if (index is null || index == lineIndex) continue;
                    vpn = TunnelOn(index.Value);
                    if (vpn is not null) break;
                    lineIndex = index;
                }

                var reading = new Reading(vpn, OpenBoosters());
                var old = _reading;
                if (old is null || old.Vpn != reading.Vpn || !old.Boosters.SequenceEqual(reading.Boosters))
                {
                    _reading = reading;
                    Dispatcher.UIThread.Post(Evaluate);
                }
            }
            catch (Exception e) when (e is NetworkInformationException or InvalidOperationException)
            {
            }
        }
        while (await timer.WaitForNextTickAsync());
    }

    /// <summary>The VPN's name when interface <paramref name="index"/> is a tunnel (not ours), else null.</summary>
    private string? TunnelOn(uint index)
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            int? nicIndex;
            try
            {
                nicIndex = nic.GetIPProperties().GetIPv4Properties()?.Index;
            }
            catch (NetworkInformationException)
            {
                continue;
            }
            if (nicIndex != (int)index) continue;

            if (nic.Name.Equals(_ownAdapter, StringComparison.OrdinalIgnoreCase)) return null;
            var text = (nic.Name + " " + nic.Description).ToLowerInvariant();
            var known = ConflictApps.Current.FirstOrDefault(a => a.IsVpn && a.AdapterWords.Any(text.Contains));
            if (known is null && !TunnelTypes.Contains((int)nic.NetworkInterfaceType) && !TunnelNames.IsMatch(text)) return null;
            return known?.Name ?? nic.Description;
        }
        return null;
    }

    private static List<string> OpenBoosters()
    {
        var boosters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in ConflictApps.Current)
            if (!app.IsVpn)
                foreach (var name in app.ProcessNames) boosters.TryAdd(name, app.Name);

        var open = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var row in PcResources.TakeSnapshot().Rows.Values)
            if (boosters.TryGetValue(row.Name, out var shown)) open.Add(shown);
        return [.. open];
    }

    // ------------------------------------------------------------ what the line says (UI thread)

    private void Evaluate()
    {
        var r = _reading;
        if (r is null)
        {
            Severity = CheckSeverity.Unknown;
            Summary = T("check.vpn.measuring");
            Lines = [];
            return;
        }

        if (r.Vpn is null && r.Boosters.Count == 0)
        {
            Severity = CheckSeverity.Good;
            Summary = T("check.vpn.none");
            Lines = [];
            return;
        }

        // While a game runs the conflict is happening now; before, it is about to.
        Severity = _gameRunning ? CheckSeverity.Critical : CheckSeverity.Warning;

        var names = new List<string>();
        if (r.Vpn is not null) names.Add(r.Vpn);
        names.AddRange(r.Boosters);
        Summary = Loc.F("check.vpn.found", string.Join(", ", names));

        var lines = new List<DetailLine>();
        if (r.Vpn is not null)
        {
            lines.Add(new DetailLine(Loc.F("check.vpn.vpnEntry", r.Vpn), IsEntry: true));
            lines.Add(new DetailLine(T("check.vpn.vpnAdvice")));
        }
        if (r.Boosters.Count > 0)
        {
            foreach (var booster in r.Boosters) lines.Add(new DetailLine(Loc.F("check.vpn.boosterEntry", booster), IsEntry: true));
            lines.Add(new DetailLine(T("check.vpn.boosterAdvice")));
        }
        Lines = lines;
    }
}
