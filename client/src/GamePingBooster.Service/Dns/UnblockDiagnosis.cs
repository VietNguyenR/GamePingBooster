using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using System.Text.RegularExpressions;
using GamePingBooster.Core.Net;
using GamePingBooster.Service.Native;

namespace GamePingBooster.Service.Dns;

/// <summary>
/// The checks tools\Check-Unblock.ps1 runs by hand, run by the service on the player's machine when unblocking
/// goes wrong, and read into one verdict - so a new block reaches /admin/unblock/reports without anyone having to
/// remote into the player's PC.
///
/// Every check answers one question that a real failure on 2026-10-02 needed:
///
///   the ISP's own resolvers, asked directly    VNPT answered NXDOMAIN, Viettel and FPT 127.0.0.1, and an FPT
///                                              modem answered over IPv6 even with 8.8.8.8 set for IPv4
///   encrypted DNS                              the truth to compare against
///   what Windows answers                       what the game actually gets - the black screen
///   a handshake per edge, real name vs other   FPT reset every edge by name within 25 ms; the same address
///                                              completed for any other name - only the tunnel helps
///   the resolver's own state                   an FPT machine's self-test timed out every minute, so the
///                                              policy never went on at all
///
/// Read-only, like the script: nothing here changes the machine.
/// </summary>
internal static class UnblockDiagnosis
{
    /// <summary>The name a handshake is compared against. Nobody filters it.</summary>
    private const string ControlName = EdgeProber.ControlName;

    private const int MaxNames = 8;
    private const int EdgesPerName = 3;

    private static readonly TimeSpan IspTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(6);

    /// <summary>Most decisive first: the order the verdict is picked in.</summary>
    private static readonly string[] Priority =
        ["leftover-rules", "resolver-failing", "modem-ipv6", "sni-filter", "ip-block", "windows-sinkhole", "dns-lie"];

    /// <summary>Programs that have been seen to sit in DNS or in front of the network on players' machines.</summary>
    private static readonly Regex Suspects = new(
        "kaspersky|^avp|eset|ekrn|bitdefender|vsserv|bdagent|avast|^avg|norton|mcafee|sophos|adguard|dnscrypt|acrylic|" +
        "nextdns|cfos|netlimiter|killer|dragon|lagofast|gearup|exitlag|wtfast|noping|outfox|haste|warp",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The service log's lines worth sending: unblocking, DoH, the profile, connecting.</summary>
    private static readonly Regex LogLines = new(
        @"Unblock|NRPT|DoH|resolver|poison|profile|Connected|Disconnect|lobby route|Tunnel is down",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal sealed record Context(
        string Trigger,
        string Detail,
        string? Name,
        UnblockPolicy Policy,
        bool Enabled,
        string? LastError,
        IUnblockRoutes? Routes,
        string? LogPath);

    internal sealed record Edge(string Address, string From, string Real, long RealMs, string Control, long ControlMs);

    internal sealed record NameCheck(
        string Name,
        IReadOnlyList<(string Server, string Answer, bool Lie)> Isp,
        IReadOnlyList<string> Doh,
        IReadOnlyList<string> Windows,
        bool WindowsUnusable,
        bool ViaTunnel,
        IReadOnlyList<Edge> Edges);

    internal sealed record Findings(
        string Verdict,
        IReadOnlyList<string> Flags,
        string Summary,
        IReadOnlyList<string> DnsV4,
        IReadOnlyList<string> DnsV6,
        int NrptRules,
        bool Enabled,
        string? LastError,
        bool TunnelUp,
        IReadOnlyList<NameCheck> Names,
        IReadOnlyList<string> Processes,
        string Log);

    public static async Task<Findings> RunAsync(Context context, CancellationToken ct)
    {
        var upstream = SafeRead();
        var v4 = upstream.Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToList();
        var v6 = upstream.Where(a => a.AddressFamily == AddressFamily.InterNetworkV6).ToList();

        // The resolvers to ask directly: the first of each family, which is where Windows starts.
        var direct = v4.Take(1).Concat(v6.Take(1)).ToList();

        using var doh = new DohUpstream(_ => { });
        var names = NamesToCheck(context);
        var checks = new List<NameCheck>();
        foreach (var name in names)
        {
            ct.ThrowIfCancellationRequested();
            checks.Add(await CheckNameAsync(name, direct, doh, context, ct).ConfigureAwait(false));
        }

        var processes = SuspectProcesses();
        var nrpt = NrptPolicy.Count();
        var tunnelUp = context.Routes?.Ready ?? false;

        var flags = new List<string>();
        // Asked, not inferred from Enabled: two reports on 2026-10-02 said "leftover rules" for a resolver that was
        // running and answering - checked mid-enable, before Enabled turned true. Rules are only left over when
        // nothing answers them.
        var resolverAnswers = await LocalResolverAnswersAsync(context.Policy, ct).ConfigureAwait(false);
        if (nrpt > 0 && !resolverAnswers) flags.Add("leftover-rules");
        if (!resolverAnswers && !context.Enabled && context.LastError is not null) flags.Add("resolver-failing");
        if (checks.Any(c => c.Isp.Any(i => i.Lie && IPAddress.TryParse(i.Server, out var s) &&
                                           s.AddressFamily == AddressFamily.InterNetworkV6)))
        {
            flags.Add("modem-ipv6");
        }
        if (checks.Any(c => c.Edges.Any(Filtered))) flags.Add("sni-filter");
        if (checks.Any(c => c.Edges.Count > 0 && c.Edges.All(e => Failed(e.Real) && Failed(e.Control)))) flags.Add("ip-block");
        if (checks.Any(c => c.WindowsUnusable && c.Doh.Count > 0)) flags.Add("windows-sinkhole");
        if (checks.Any(c => c.Isp.Any(i => i.Lie) && c.Doh.Count > 0)) flags.Add("dns-lie");

        var verdict = Priority.FirstOrDefault(flags.Contains) ?? "ok";
        if (flags.Count == 0) flags.Add("ok");
        flags.Remove(verdict);
        flags.Insert(0, verdict);

        return new Findings(
            verdict, flags, Summarise(verdict, context, checks),
            [.. v4.Select(a => a.ToString())], [.. v6.Select(a => a.ToString())],
            nrpt, context.Enabled, context.LastError, tunnelUp, checks, processes, ReadLog(context.LogPath));
    }

    /// <summary>
    /// Whether the unblock resolver on 127.0.0.53 answers at all - any reply, for any service's canary, within three
    /// seconds. What "the rules point at something that is not there" actually means.
    /// </summary>
    private static async Task<bool> LocalResolverAnswersAsync(UnblockPolicy policy, CancellationToken ct)
    {
        var canary = policy.Apps.Select(a => a.Canary).FirstOrDefault() ?? "steamcommunity.com";
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(3));
            await socket.SendToAsync(DnsWire.BuildQuery((ushort)Random.Shared.Next(1, ushort.MaxValue), canary),
                SocketFlags.None, new IPEndPoint(IPAddress.Parse("127.0.0.53"), 53), limit.Token).ConfigureAwait(false);
            var buffer = new byte[DnsWire.MaxUdpMessage];
            var received = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), limit.Token)
                .ConfigureAwait(false);
            return received.ReceivedBytes >= DnsWire.HeaderLength;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>The line cuts the real name on this edge - see <see cref="EdgeProber.CutByName"/>.</summary>
    private static bool Filtered(Edge e) => EdgeProber.CutByName(e.Real, e.Control);

    private static bool Failed(string outcome) => outcome is "reset" or "stall" or "no-tcp" or "refused";

    /// <summary>The name that set it off, every service's canary, and the names routed through the tunnel.</summary>
    private static List<string> NamesToCheck(Context context)
    {
        var names = new List<string>();
        void Add(string? name)
        {
            if (string.IsNullOrWhiteSpace(name) || !name.Contains('.')) return;
            if (!names.Contains(name, StringComparer.OrdinalIgnoreCase)) names.Add(name.Trim().TrimEnd('.').ToLowerInvariant());
        }

        Add(context.Name);
        foreach (var app in context.Policy.Apps) Add(app.Canary);
        foreach (var app in context.Policy.Apps)
        {
            foreach (var name in app.Tunnel ?? []) Add(name);
        }
        return [.. names.Take(MaxNames)];
    }

    private static async Task<NameCheck> CheckNameAsync(
        string name, List<IPAddress> direct, DohUpstream doh, Context context, CancellationToken ct)
    {
        var isp = new List<(string, string, bool)>();
        foreach (var server in direct)
        {
            var (answer, addresses, nx) = await AskDirectAsync(server, name, ct).ConfigureAwait(false);
            isp.Add((server.ToString(), answer, nx || (addresses.Count > 0 && Sinkhole(addresses))));
        }

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

        IPAddress[] windows;
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(5));
            windows = await System.Net.Dns.GetHostAddressesAsync(name, AddressFamily.InterNetwork, limit.Token).ConfigureAwait(false);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            windows = [];
        }

        var app = context.Policy.ClaimedBy(name);
        var viaTunnel = context.Routes is { Ready: true } && app is not null && app.RoutesThroughTunnel(name);

        // The edges the game could be handed: encrypted DNS's first, then what the ISP said when it said
        // something real, then what Windows holds.
        var candidates = new List<(IPAddress Address, string From)>();
        void Take(IEnumerable<IPAddress> addresses, string from)
        {
            foreach (var a in addresses)
            {
                if (candidates.Count >= EdgesPerName) return;
                // The split's address is ours, not an edge: handshaking it only reaches the proxy.
                if (a.AddressFamily != AddressFamily.InterNetwork || Sinkhole([a]) || SplitProxy.IsOurs(a)) continue;
                if (candidates.Any(c => c.Address.Equals(a))) continue;
                candidates.Add((a, from));
            }
        }
        Take(dohAddresses.Take(2), "doh");
        Take(windows, "windows");
        Take(dohAddresses.Skip(2), "doh");

        var edges = new List<Edge>();
        foreach (var (address, from) in candidates)
        {
            var (real, realMs) = await HandshakeAsync(address, name, ct).ConfigureAwait(false);
            var (control, controlMs) = await HandshakeAsync(address, ControlName, ct).ConfigureAwait(false);
            edges.Add(new Edge(address.ToString(), from, real, realMs, control, controlMs));
        }

        return new NameCheck(
            name, isp, [.. dohAddresses.Select(a => a.ToString())], [.. windows.Select(a => a.ToString())],
            Sinkhole(windows), viaTunnel, edges);
    }

    /// <summary>
    /// One A query straight to a resolver, past the name resolution policy - the ISP's own answer, which is what
    /// the line says before anything of ours touches it.
    /// </summary>
    private static async Task<(string Answer, List<IPAddress> Addresses, bool NxDomain)> AskDirectAsync(
        IPAddress server, string name, CancellationToken ct)
    {
        try
        {
            using var socket = new Socket(server.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(IspTimeout);

            var query = DnsWire.BuildQuery((ushort)Random.Shared.Next(1, ushort.MaxValue), name);
            await socket.SendToAsync(query, SocketFlags.None, new IPEndPoint(server, 53), limit.Token).ConfigureAwait(false);

            var buffer = new byte[DnsWire.MaxUdpMessage];
            var received = await socket.ReceiveFromAsync(buffer, SocketFlags.None,
                new IPEndPoint(server.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0),
                limit.Token).ConfigureAwait(false);

            var parsed = DnsWire.Parse(buffer, received.ReceivedBytes);
            if (parsed.RCode == 3) return ("NXDOMAIN", [], true);
            if (parsed.RCode != 0) return (DnsWire.RCodeName(parsed.RCode), [], false);
            var addresses = parsed.Addresses.ToList();
            return (addresses.Count == 0 ? "no address" : string.Join(",", addresses), addresses, false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ("no answer", [], false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ($"error: {ex.Message}", [], false);
        }
    }

    private static Task<(string Outcome, long Ms)> HandshakeAsync(IPAddress address, string sni, CancellationToken ct) =>
        EdgeProber.HandshakeAsync(address, sni, ConnectTimeout, HandshakeTimeout, ct);

    /// <summary>Loopback, unspecified or private: the shape of a resolver's lie, as UnblockDns judges it.</summary>
    private static bool Sinkhole(IReadOnlyCollection<IPAddress> addresses)
    {
        if (addresses.Count == 0) return true;
        foreach (var address in addresses)
        {
            if (SplitProxy.IsOurs(address)) return false;
            if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) continue;
            var b = address.GetAddressBytes();
            if (b.Length == 4 && (b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168))) continue;
            return false;
        }
        return true;
    }

    private static List<IPAddress> SafeRead()
    {
        try { return AdapterDns.Read(); }
        catch (Exception) { return []; }
    }

    private static List<string> SuspectProcesses()
    {
        try
        {
            var processes = Process.GetProcesses();
            try
            {
                return [.. processes.Select(p => p.ProcessName).Where(n => Suspects.IsMatch(n))
                    .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];
            }
            finally
            {
                foreach (var p in processes) p.Dispose();
            }
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>The service log's last lines about unblocking, DoH and connecting - at most 250 of them.</summary>
    private static string ReadLog(string? path)
    {
        if (path is null || !File.Exists(path)) return "";
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            // The last 1 MB is plenty for 250 interesting lines, and the file can be 4 MB.
            if (stream.Length > 1024 * 1024) stream.Seek(-1024 * 1024, SeekOrigin.End);
            using var reader = new StreamReader(stream);
            var lines = reader.ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r')).Where(l => LogLines.IsMatch(l)).ToList();
            return string.Join('\n', lines.Skip(Math.Max(0, lines.Count - 250)));
        }
        catch (Exception)
        {
            return "";
        }
    }

    private static string Summarise(string verdict, Context context, List<NameCheck> checks)
    {
        string Names(Func<NameCheck, bool> where) => string.Join(", ", checks.Where(where).Select(c => c.Name).Take(3));

        return verdict switch
        {
            "leftover-rules" => "Unblock rules are installed but the resolver is off - every claimed name fails.",
            "resolver-failing" => $"Unblocking would not turn on: {context.LastError}",
            "modem-ipv6" => $"The IPv6 resolver lies about {Names(c => c.Isp.Any(i => i.Lie))} - Windows asks it too.",
            "sni-filter" => $"The line resets handshakes naming {Names(c => c.Edges.Any(Filtered))}; another name on the same address completes.",
            "ip-block" => $"No handshake completes on any address of {Names(c => c.Edges.Count > 0 && c.Edges.All(e => Failed(e.Real) && Failed(e.Control)))}.",
            "windows-sinkhole" => $"Windows gets no usable address for {Names(c => c.WindowsUnusable && c.Doh.Count > 0)}.",
            "dns-lie" => $"The ISP lies about {Names(c => c.Isp.Any(i => i.Lie))}; the fix is answering it.",
            _ => $"Nothing wrong found when the checks ran ({context.Trigger}: {context.Detail}).",
        };
    }

    /// <summary>The report's "report" object - the structured checks.</summary>
    public static void WriteChecks(Utf8JsonWriter json, Findings f, Context context)
    {
        json.WriteStartObject();
        json.WriteString("detail", context.Detail);
        if (context.Name is not null) json.WriteString("name", context.Name);

        json.WriteStartObject("resolver");
        json.WriteBoolean("enabled", f.Enabled);
        if (f.LastError is not null) json.WriteString("lastError", f.LastError);
        json.WriteNumber("nrptRules", f.NrptRules);
        json.WriteString("services", context.Policy.Describe());
        json.WriteEndObject();

        json.WriteStartObject("network");
        WriteStrings(json, "dnsV4", f.DnsV4);
        WriteStrings(json, "dnsV6", f.DnsV6);
        json.WriteBoolean("tunnelUp", f.TunnelUp);
        WriteStrings(json, "processes", f.Processes);
        json.WriteEndObject();

        json.WriteStartArray("names");
        foreach (var n in f.Names)
        {
            json.WriteStartObject();
            json.WriteString("name", n.Name);
            json.WriteStartArray("isp");
            foreach (var (server, answer, lie) in n.Isp)
            {
                json.WriteStartObject();
                json.WriteString("server", server);
                json.WriteString("answer", answer);
                json.WriteBoolean("lie", lie);
                json.WriteEndObject();
            }
            json.WriteEndArray();
            WriteStrings(json, "doh", n.Doh);
            WriteStrings(json, "windows", n.Windows);
            json.WriteBoolean("windowsUnusable", n.WindowsUnusable);
            json.WriteBoolean("viaTunnel", n.ViaTunnel);
            json.WriteStartArray("edges");
            foreach (var e in n.Edges)
            {
                json.WriteStartObject();
                json.WriteString("address", e.Address);
                json.WriteString("from", e.From);
                json.WriteString("real", e.Real);
                json.WriteNumber("realMs", e.RealMs);
                json.WriteString("control", e.Control);
                json.WriteNumber("controlMs", e.ControlMs);
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WriteEndObject();
    }

    private static void WriteStrings(Utf8JsonWriter json, string name, IEnumerable<string> values)
    {
        json.WriteStartArray(name);
        foreach (var v in values) json.WriteStringValue(v);
        json.WriteEndArray();
    }
}
