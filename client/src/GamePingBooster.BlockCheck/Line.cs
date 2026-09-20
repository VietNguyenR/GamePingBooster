using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace GamePingBooster.BlockCheck;

/// <summary>
/// What this machine's network looks like before any probing, and whether it is fit to probe from.
///
/// <see cref="NetworkInterface"/> is used here, which the service is forbidden to do in its connect
/// path - enumerating adapters as LocalSystem cost seconds and was most of a 30 s connect. That
/// reason does not apply to a tool a human runs once from a terminal, and no other API reports the
/// adapter descriptions this needs. Do not copy the pattern back into the service.
/// </summary>
internal static class Line
{
    /// <summary>
    /// Names that mean traffic is already leaving this machine somewhere other than the ISP.
    ///
    /// The run has to start from an untouched line or it measures the workaround instead of the
    /// block - which is exactly the trap here, because the reason this tool exists is that Cloudflare
    /// WARP makes Steam work. With WARP up, every name comes back clean and the report says the ISP
    /// blocks nothing.
    /// </summary>
    private static readonly string[] Tunnels =
    [
        "cloudflare", "warp", "wireguard", "wintun", "openvpn", "tap-windows", "tap-win",
        "nordlynx", "proton", "tailscale", "zerotier", "game ping booster", "gpb",
    ];

    public static LineState Read()
    {
        var resolvers = new List<IPAddress>();
        var adapters = new List<string>();
        var interference = new List<string>();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            // Windows reports one entry per bound filter driver as well as per adapter, so a machine
            // with Npcap and a traffic shaper installed lists forty "adapters up" and the two that
            // matter scroll off the top. The filter entries carry no address of their own; anything
            // that can actually send a packet, tunnels included, carries one.
            var properties = nic.GetIPProperties();
            if (properties.UnicastAddresses.Count == 0) continue;

            adapters.Add($"{nic.Name} ({nic.Description})");

            var haystack = (nic.Name + " " + nic.Description).ToLowerInvariant();
            foreach (var tunnel in Tunnels)
            {
                if (haystack.Contains(tunnel))
                {
                    interference.Add($"adapter up: {nic.Name} ({nic.Description})");
                    break;
                }
            }

            foreach (var server in properties.DnsAddresses)
            {
                if (server.AddressFamily != AddressFamily.InterNetwork) continue;

                if (IPAddress.IsLoopback(server))
                {
                    // WARP and every other local DoH proxy publishes a 127.x address as the machine's
                    // resolver. A plaintext query to it would be answered over the proxy's encrypted
                    // upstream, which is the thing being tested for.
                    interference.Add($"Windows resolver is local: {server} - a DNS proxy is running");
                    continue;
                }

                if (!resolvers.Contains(server)) resolvers.Add(server);
            }
        }

        if (resolvers.Count == 0)
        {
            interference.Add("Windows has no non-loopback IPv4 resolver - nothing to compare DoH against");
        }

        return new LineState([.. resolvers], [.. adapters], [.. interference]);
    }
}

internal sealed record LineState(IPAddress[] Resolvers, string[] Adapters, string[] Interference);
