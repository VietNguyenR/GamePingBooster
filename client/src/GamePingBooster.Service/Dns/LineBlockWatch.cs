using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using GamePingBooster.Core.Ipc;
using GamePingBooster.Core.Net;
using GamePingBooster.Service.Native;

namespace GamePingBooster.Service.Dns;

/// <summary>
/// Whether the player's own line is breaking a service the profile unblocks (PUBG, Steam) right now - for the PC tune-up
/// panel, which says so for free while the fix (Connect + unblocking) stays paid (block-detect-handoff.md, agreed with
/// the owner 2026-10-22). Nothing is shown while the line is clean: telling a player "your line is fine" only hands
/// them the reason to stop paying, and a clean reading from one machine proves less than it seems to.
///
/// The checks are <see cref="UnblockDiagnosis"/>'s, judged more strictly about one thing and run only when they can
/// see the line:
///
///   the ISP's resolver, asked directly     NXDOMAIN or a sinkhole for a name encrypted DNS answers (VNPT, 2026-10-02)
///   a handshake per edge, real vs control  the line cuts the real name while www.microsoft.com completes on the same
///                                          address (FPT). Up to <see cref="Tries"/> handshakes for the real name and
///                                          the edge counts as cut at <see cref="CutsNeeded"/>: FPT cuts per flow, ~80%
///                                          on some edges, and one lucky handshake read "clean" (2026-10-16)
///
/// When it may run, all of them at once: a UI attached (the app is open), nothing connected or connecting, unblocking
/// off and no game running. Connected, the app's own fix would answer the questions and every name would read clean;
/// in a match, nothing may add traffic. A run that loses one of those part way is dropped and the last result kept.
///
/// Read-only like the diagnosis, and nothing leaves the machine: the result goes to the status and the log. Sending
/// it per ISP to the server is step 3, not built.
/// </summary>
internal sealed class LineBlockWatch : IAsyncDisposable
{
    /// <summary>After the service starts, so a boot's network has settled.</summary>
    private static readonly TimeSpan FirstWait = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How often the conditions and the network are looked at - two cheap table reads. Short, so opening the app,
    /// disconnecting or changing networks is answered in seconds, not at the next minute (owner 2026-10-23: FPT -&gt;
    /// Viettel kept the FPT warning, VNPT took two minutes).
    /// </summary>
    private static readonly TimeSpan LookEvery = TimeSpan.FromSeconds(5);

    /// <summary>A clean line is asked again this often while the app stays open: a block can start on the same network.</summary>
    private static readonly TimeSpan CleanEvery = TimeSpan.FromMinutes(10);

    /// <summary>
    /// A blocked one sooner: a warning left up after the player moved to a line that works (a router swapping its
    /// way out, which neither the resolvers nor the gateway show) is worse than none.
    /// </summary>
    private static readonly TimeSpan BlockedEvery = TimeSpan.FromMinutes(2);

    /// <summary>The app was opened again: a result older than this is asked again at once.</summary>
    private static readonly TimeSpan FreshOnOpen = TimeSpan.FromMinutes(1);

    /// <summary>No run starts sooner than this after the last one, whatever asked for it.</summary>
    private static readonly TimeSpan MinGap = TimeSpan.FromSeconds(20);

    /// <summary>
    /// The public address is asked this often (one STUN packet), and at once when the local network changes. It is the
    /// one thing that changes on a move between ISPs whose modems all look the same from the PC (owner 2026-10-23:
    /// 192.168.1.1 everywhere, resolvers fixed at 8.8.8.8).
    /// </summary>
    private static readonly TimeSpan AskIpEvery = TimeSpan.FromSeconds(15);

    /// <summary>A changed network is measured once it has looked the same for this long (DHCP, the resolvers arriving).</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(5);

    private const int NamesPerApp = 4;
    private const int EdgesPerName = 3;
    private const int Tries = 3;
    private const int CutsNeeded = 2;

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(4);

    /// <summary>A filtered edge hangs ~19 s before its reset; a stall at 6 s counts the same (steam-block-is-dns-only).</summary>
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(6);

    private readonly Func<UnblockPolicy> _policy;
    private readonly Func<TunnelState> _state;
    private readonly Func<bool> _unblocking;
    private readonly Func<bool> _gameRunning;
    private readonly Func<bool> _uiAttached;
    private readonly Action<string> _log;

    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    private volatile string[] _blocked = [];
    private DateTime _lastRun = DateTime.MinValue;
    private string? _lastNetwork;
    private string? _seenNetwork;
    private DateTime _seenSince;
    private bool _wasAttached;
    private string _publicIp = "?";
    private string? _ipAskedOn;
    private DateTime _ipAskedAt = DateTime.MinValue;

    public LineBlockWatch(Func<UnblockPolicy> policy, Func<TunnelState> state, Func<bool> unblocking,
        Func<bool> gameRunning, Func<bool> uiAttached, Action<string> log)
    {
        _policy = policy;
        _state = state;
        _unblocking = unblocking;
        _gameRunning = gameRunning;
        _uiAttached = uiAttached;
        _log = log;
    }

    /// <summary>The services the last finished run found broken by the line, by display name; empty when none or not run yet.</summary>
    public IReadOnlyList<string> Blocked => _blocked;

    /// <summary>The result changed. Raised from the watch's own thread.</summary>
    public event Action? Changed;

    public void Start() => _loop ??= Task.Run(() => RunAsync(_cts.Token));

    private bool MayRun() =>
        _uiAttached() &&
        _state() is TunnelState.Disconnected or TunnelState.Faulted &&
        !_unblocking() &&
        !_gameRunning() &&
        !_policy().IsEmpty;

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(FirstWait, ct).ConfigureAwait(false);
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var attached = _uiAttached();
                    var opened = attached && !_wasAttached;
                    _wasAttached = attached;
                    if (MayRun())
                    {
                        await RefreshPublicIpAsync(ct).ConfigureAwait(false);
                        if (Due(opened, out var network)) await CheckAsync(network, ct).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _log($"Line check: failed ({ex.Message}).");
                    _lastRun = DateTime.UtcNow;
                }
                await Task.Delay(LookEvery, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Asks the public address every <see cref="AskIpEvery"/>, or at once on another local network; a failed ask keeps the last one.</summary>
    private async Task RefreshPublicIpAsync(CancellationToken ct)
    {
        var local = LocalNetwork();
        if (local == _ipAskedOn && DateTime.UtcNow - _ipAskedAt < AskIpEvery) return;
        _ipAskedOn = local;
        _ipAskedAt = DateTime.UtcNow;
        if (await PublicAddress.AskAsync(ct).ConfigureAwait(false) is { } ip) _publicIp = ip.ToString();
    }

    private string NetworkNow() => $"{LocalNetwork()} from {_publicIp}";

    private bool Due(bool opened, out string network)
    {
        var now = DateTime.UtcNow;
        network = NetworkNow();
        if (network != _seenNetwork)
        {
            if (_seenNetwork is not null) _log($"Line check: the network changed ({_seenNetwork} -> {network}) - measured again once it settles.");
            _seenNetwork = network;
            _seenSince = now;
        }

        var since = now - _lastRun;
        if (since < MinGap) return false;
        if (network != _lastNetwork) return now - _seenSince >= Settle || _lastNetwork is null;
        if (opened && since >= FreshOnOpen) return true;
        return since >= (_blocked.Length > 0 ? BlockedEvery : CleanEvery);
    }

    /// <summary>
    /// The resolvers this machine uses and the gateway it leaves by: a different network is a different line. Both are
    /// table reads, cheap enough for every <see cref="LookEvery"/>. Not enough on their own - two ISPs' modems can both
    /// be 192.168.1.1 with the resolvers fixed at 8.8.8.8 - hence the public address in <see cref="NetworkNow"/>.
    /// </summary>
    private static string LocalNetwork()
    {
        string resolvers;
        try
        {
            resolvers = string.Join(",", AdapterDns.Read().Select(a => a.ToString()).Order(StringComparer.Ordinal));
        }
        catch (Exception)
        {
            resolvers = "?";
        }

        string gateway;
        try
        {
            gateway = GamePingBooster.Service.Network.RouteManager.GetRouteTo(IPAddress.Parse("1.1.1.1")) is { } route
                ? $"{route.InterfaceIndex}/{route.Gateway}"
                : "-";
        }
        catch (Exception)
        {
            gateway = "?";
        }

        return $"{resolvers} via {gateway}";
    }

    private async Task CheckAsync(string network, CancellationToken ct)
    {
        _lastRun = DateTime.UtcNow;
        var clock = Stopwatch.StartNew();

        // Stops the run the moment Connect is pressed or a game opens: no extra traffic in a match, and a connected
        // machine's answers would be the fix's.
        using var abandon = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var guard = new Timer(_ => { if (!MayRun()) TryCancel(abandon); }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

        Result result;
        try
        {
            result = await MeasureAsync(_policy(), abandon.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _log($"Line check: stopped after {clock.Elapsed.TotalSeconds:0} s (connected or a game opened) - last result kept.");
            return;
        }

        // The network changed while it ran: the answers are half one line, half another. Measured again once it settles.
        if (NetworkNow() != network)
        {
            _log($"Line check: the network changed during the run ({clock.Elapsed.TotalSeconds:0} s) - asked again once it settles.");
            return;
        }

        _lastNetwork = network;
        var (blocked, findings) = (result.Blocked, result.Findings);

        // Nothing could be measured - no encrypted answer, no control handshake anywhere: the line is down or something
        // sits in front of it. Not "clean", so the last result stands.
        if (!result.Measured)
        {
            _lastNetwork = network;     // asked again on the blocked schedule, not every MinGap while the line is down
            _log($"Line check: nothing measurable in {clock.Elapsed.TotalSeconds:0} s - last result kept.");
            return;
        }

        _log(blocked.Count == 0
            ? $"Line check: nothing broken by the line ({_policy().Describe()}, {clock.Elapsed.TotalSeconds:0} s)."
            : $"Line check: the line breaks {string.Join(", ", findings)} ({clock.Elapsed.TotalSeconds:0} s).");

        var before = _blocked;
        _blocked = [.. blocked];
        if (!before.SequenceEqual(_blocked)) Changed?.Invoke();
    }

    /// <summary>What one run found: services broken by the line, one line per service saying how, and whether anything could be judged.</summary>
    internal sealed record Result(IReadOnlyList<string> Blocked, IReadOnlyList<string> Findings, bool Measured);

    /// <summary>One run over every service in the policy, on the line as it is now. Internal for TunnelCheck's lineblocks.</summary>
    internal static async Task<Result> MeasureAsync(UnblockPolicy policy, CancellationToken ct)
    {
        var upstream = AdapterDns.Read();
        var direct = upstream.Where(a => a.AddressFamily == AddressFamily.InterNetwork).Take(1)
            .Concat(upstream.Where(a => a.AddressFamily == AddressFamily.InterNetworkV6).Take(1))
            .ToList();

        using var doh = new DohUpstream(_ => { });
        var blocked = new List<string>();
        var findings = new List<string>();
        var anyMeasured = false;

        // Every name at once: each waits mostly on round trips and handshake timeouts, and one after another a VNPT
        // line took 14 s. Different names go to different edges, so nothing is asked twice in parallel.
        var apps = policy.Apps.Select(app => (App: app, Names: NamesOf(app)
            .Select(name => CheckNameAsync(name, direct, doh, ct)).ToList())).ToList();
        await Task.WhenAll(apps.SelectMany(a => a.Names)).ConfigureAwait(false);

        foreach (var (app, names) in apps)
        {
            var appFindings = new List<string>();
            foreach (var check in names)
            {
                var (measured, finding) = check.Result;
                anyMeasured |= measured;
                if (finding is not null) appFindings.Add(finding);
            }

            if (appFindings.Count > 0)
            {
                blocked.Add(app.Name);
                findings.Add($"{app.Name}: {string.Join("; ", appFindings)}");
            }
        }

        return new Result(blocked, findings, anyMeasured);
    }

    /// <summary>The service's canary first - a name it is known to have been lied about - then its tunnel names.</summary>
    private static List<string> NamesOf(UnblockApp app)
    {
        var names = new List<string>();
        foreach (var raw in (IEnumerable<string>)[app.Canary, .. app.Tunnel ?? []])
        {
            var name = raw.Trim().TrimEnd('.').ToLowerInvariant();
            if (name.Length == 0 || !name.Contains('.') || names.Contains(name)) continue;
            names.Add(name);
            if (names.Count >= NamesPerApp) break;
        }
        return names;
    }

    /// <summary>
    /// One name: whether anything about it could be judged, and what the line does to it (null when nothing).
    /// </summary>
    private static async Task<(bool Measured, string? Finding)> CheckNameAsync(
        string name, List<IPAddress> direct, DohUpstream doh, CancellationToken ct)
    {
        var dohAddresses = new List<IPAddress>();
        foreach (var reply in await doh.ResolveEverywhereAsync(DnsWire.BuildQuery(0, name), ct).ConfigureAwait(false))
        {
            try
            {
                foreach (var a in DnsWire.Parse(reply, reply.Length).Addresses)
                {
                    if (a.AddressFamily == AddressFamily.InterNetwork && !dohAddresses.Contains(a)) dohAddresses.Add(a);
                }
            }
            catch (FormatException)
            {
            }
        }

        // No trustworthy answer to judge the ISP's against.
        if (dohAddresses.Count == 0) return (false, null);

        var found = new List<string>();
        var used = new List<IPAddress>();

        foreach (var server in direct)
        {
            var (answer, addresses, nx) = await UnblockDiagnosis.AskDirectAsync(server, name, ct).ConfigureAwait(false);
            if (nx || (addresses.Count > 0 && UnblockDiagnosis.Sinkhole(addresses)))
            {
                found.Add($"{name} dns-lie ({server} {answer})");
                used.Clear();
                break;
            }
            foreach (var a in addresses)
            {
                if (a.AddressFamily == AddressFamily.InterNetwork && !used.Contains(a)) used.Add(a);
            }
        }

        // The handshakes go to the addresses this PC is actually given, when its resolver answers honestly: an edge
        // nobody here is sent to says nothing about the player. Viettel 2026-10-23: the encrypted resolvers named
        // steamcommunity.com's foreign Akamai edge 23.15.140.216, where Viettel stalls the name, while 8.8.8.8 sends
        // the PC to Akamai inside Viettel (171.236.60.221), which serves it - Steam read "broken" while it worked.
        var edges = used.Count > 0 ? used : dohAddresses;

        var measured = direct.Count > 0;
        foreach (var address in edges.Where(a => !UnblockDiagnosis.Sinkhole([a])).Take(EdgesPerName))
        {
            // The control first: an edge that does not complete one for a name nobody filters says nothing about this one.
            var (control, _) = await EdgeProber.HandshakeAsync(address, EdgeProber.ControlName, ConnectTimeout, HandshakeTimeout, ct)
                .ConfigureAwait(false);
            if (control is not ("ok" or "cert-mismatch" or "tls-error")) continue;
            measured = true;

            var cuts = 0;
            for (var i = 0; i < Tries && cuts < CutsNeeded && Tries - i >= CutsNeeded - cuts; i++)
            {
                var (real, _) = await EdgeProber.HandshakeAsync(address, name, ConnectTimeout, HandshakeTimeout, ct)
                    .ConfigureAwait(false);
                if (EdgeProber.CutByName(real, control)) cuts++;
            }

            if (cuts >= CutsNeeded)
            {
                found.Add($"{name} sni-cut on {address}");
                break;
            }
        }

        return (measured, found.Count == 0 ? null : string.Join(", ", found));
    }

    private static void TryCancel(CancellationTokenSource cts)
    {
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_loop is { } loop)
        {
            try
            {
                await loop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }
        _cts.Dispose();
    }
}
