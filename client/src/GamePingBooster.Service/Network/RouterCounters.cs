using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace GamePingBooster.Service.Network;

/// <summary>
/// The home router's own WAN byte counters, read over UPnP IGD (WANCommonInterfaceConfig GetTotalBytesReceived /
/// GetTotalBytesSent). The router counts every device in the house, so its total less this PC's adapter is what the
/// OTHER devices are moving - the one proof that a slow line was somebody else at home (SpikeEvent.Household).
///
/// Measured 2026-10-20: exact on OpenWrt (miniupnpd) - WAN less this PC stayed at 0 while only this PC loaded the
/// line, and a phone's 780 Mbps speed test showed second by second. The stock ISP modems answered nothing: Viettel
/// ZTE F6601P and VNPT ZTE F671Y have UPnP off. So "no counters" is the common case and never an error - the caller
/// stays silent about other devices then. The match record says which it was (telemetry "router"), so the share of
/// players with counters is known from data.
///
/// Read-only: discovery (SSDP), the device description, and two SOAP queries. Nothing is ever set on the router.
/// No address leaves this class except to the router itself; the model name is the only thing recorded.
/// </summary>
internal sealed class RouterCounters : IDisposable
{
    private const string CommonService = "urn:schemas-upnp-org:service:WANCommonInterfaceConfig:1";

    private static readonly string[] SearchTargets =
    [
        "urn:schemas-upnp-org:device:InternetGatewayDevice:1",
        "urn:schemas-upnp-org:device:InternetGatewayDevice:2",
        CommonService,
    ];

    private readonly HttpClient _http = new(new SocketsHttpHandler
    {
        // A LAN address: never through a proxy, whatever LocalSystem's settings say.
        UseProxy = false,
        AllowAutoRedirect = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    }, disposeHandler: true)
    {
        Timeout = TimeSpan.FromSeconds(2),
    };

    private Uri? _control;

    /// <summary>The router's model, from its description, once found. Recorded with the match; nothing else is.</summary>
    public string? Model { get; private set; }

    /// <summary>
    /// Finds the router's counter service. Asked of the gateway by unicast first - the reply then passes the firewall
    /// as part of the flow even on a network Windows calls Public - and by multicast from the adapter's address.
    /// True when the counters answered a first read.
    /// </summary>
    public async Task<bool> DiscoverAsync(IPAddress gateway, IPAddress? local, CancellationToken ct)
    {
        _control = null;
        Model = null;
        foreach (var location in await SearchAsync(gateway, local, ct).ConfigureAwait(false))
        {
            try
            {
                var description = await _http.GetStringAsync(location, ct).ConfigureAwait(false);
                // Kept even when the counters are missing: "UPnP on, no counters" is worth knowing apart from "no UPnP".
                Model ??= Tag(description, "modelName") ?? Tag(description, "friendlyName");
                if (Model is { Length: > 80 }) Model = Model[..80];
                if (FindControl(location, description) is not { } control) continue;
                _control = control;
                if (await ReadAsync(ct).ConfigureAwait(false) is not null) return true;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException)
            {
            }
        }
        _control = null;
        return false;
    }

    /// <summary>The router's WAN byte totals (received, sent), or null when it did not answer this time.</summary>
    public async Task<(ulong Received, ulong Sent)?> ReadAsync(CancellationToken ct)
    {
        if (_control is not { } control) return null;
        try
        {
            var received = await QueryAsync(control, "GetTotalBytesReceived", ct).ConfigureAwait(false);
            var sent = await QueryAsync(control, "GetTotalBytesSent", ct).ConfigureAwait(false);
            return received is { } r && sent is { } s ? (r, s) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Seen at the peak of a 780 Mbps download on 2026-10-20: the router's CPU too busy to answer for two or
            // three seconds. The next read picks up where the counters are.
            return null;
        }
    }

    private async Task<ulong?> QueryAsync(Uri control, string action, CancellationToken ct)
    {
        var body = "<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" " +
                   "s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body>" +
                   $"<u:{action} xmlns:u=\"{CommonService}\"/></s:Body></s:Envelope>";
        using var request = new HttpRequestMessage(HttpMethod.Post, control)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/xml"),
        };
        request.Headers.TryAddWithoutValidation("SOAPAction", $"\"{CommonService}#{action}\"");
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var match = Regex.Match(text, @"<New\w+>\s*(\d+)\s*</New");
        return match.Success && ulong.TryParse(match.Groups[1].Value, out var value) ? value : null;
    }

    private static async Task<List<Uri>> SearchAsync(IPAddress gateway, IPAddress? local, CancellationToken ct)
    {
        var found = new List<Uri>();
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(local ?? IPAddress.Any, 0));
        socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 2);
        var multicast = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
        foreach (var target in SearchTargets)
        {
            var unicast = Encoding.ASCII.GetBytes(Search(gateway + ":1900", target));
            var group = Encoding.ASCII.GetBytes(Search("239.255.255.250:1900", target));
            try
            {
                await socket.SendToAsync(unicast, SocketFlags.None, new IPEndPoint(gateway, 1900), ct).ConfigureAwait(false);
                await socket.SendToAsync(group, SocketFlags.None, multicast, ct).ConfigureAwait(false);
            }
            catch (SocketException)
            {
            }
        }

        using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
        window.CancelAfter(TimeSpan.FromSeconds(3));
        var buffer = new byte[4096];
        try
        {
            while (true)
            {
                var result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), window.Token)
                    .ConfigureAwait(false);
                // Only the gateway itself: another UPnP box on the LAN (a mesh node, a NAS) does not count the line.
                if (result.RemoteEndPoint is not IPEndPoint from || !from.Address.Equals(gateway)) continue;
                var text = Encoding.ASCII.GetString(buffer, 0, result.ReceivedBytes);
                var location = Regex.Match(text, @"(?im)^location:\s*(\S+)");
                if (location.Success && Uri.TryCreate(location.Groups[1].Value, UriKind.Absolute, out var uri) &&
                    uri.Scheme == Uri.UriSchemeHttp && IPAddress.TryParse(uri.Host, out var host) && host.Equals(gateway) &&
                    !found.Contains(uri))
                {
                    found.Add(uri);
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
        }
        catch (SocketException)
        {
        }
        return found;
    }

    private static string Search(string host, string target) =>
        $"M-SEARCH * HTTP/1.1\r\nHOST: {host}\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\nST: {target}\r\n\r\n";

    /// <summary>The control URL of WANCommonInterfaceConfig in a device description, resolved against where it came from.</summary>
    private static Uri? FindControl(Uri location, string description)
    {
        foreach (Match service in Regex.Matches(description, @"<service>(.*?)</service>", RegexOptions.Singleline))
        {
            var type = Tag(service.Value, "serviceType");
            if (type is null || !type.StartsWith("urn:schemas-upnp-org:service:WANCommonInterfaceConfig:", StringComparison.Ordinal)) continue;
            var control = Tag(service.Value, "controlURL");
            if (control is null || !Uri.TryCreate(location, control, out var uri)) continue;
            // The router's own address only, whatever the description says.
            return uri.Host == location.Host ? uri : null;
        }
        return null;
    }

    private static string? Tag(string xml, string name)
    {
        var match = Regex.Match(xml, $"<{name}>(.*?)</{name}>", RegexOptions.Singleline);
        return match.Success ? WebUtility.HtmlDecode(match.Groups[1].Value.Trim()) : null;
    }

    public void Dispose() => _http.Dispose();
}
