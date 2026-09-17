using System.Net;
using System.Net.Sockets;

namespace GamePingBooster.Service.Discovery;

/// <summary>
/// What the service and gpb-etwwatch agree on about a game's destinations: which ones can be
/// ignored, what counts as a server, and how a table of them is written. One copy, so the tool that
/// was checked against `./gpb capture` and the service that runs on players' machines cannot drift
/// into reporting different things.
/// </summary>
internal static class Destinations
{
    /// <summary>
    /// Outbound packets above which a destination is a server. Capture's -MinPackets and the same
    /// number: raised to 500 on 2026-09-05 from a measured gap of nearly three orders of magnitude
    /// between a match server and everything else the game talks to.
    /// </summary>
    public const int ServerMinPackets = 500;

    /// <summary>
    /// Loopback, LAN, link-local, CGNAT and multicast. The game talking to its launcher, its
    /// anti-cheat or the router - never a game server, and the relay refuses private ranges anyway.
    /// </summary>
    public static bool IsLocal(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6UniqueLocal || address.IsIPv6Multicast;
        }
        var b = address.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && (b[1] & 0xF0) == 16) || (b[0] == 192 && b[1] == 168) ||
               (b[0] == 169 && b[1] == 254) || b[0] >= 224 || (b[0] == 100 && (b[1] & 0xC0) == 64);
    }

    /// <summary>
    /// An address as the service's log may show it: the first part only - "20.*.*.*", "2001:*". Enough
    /// to tell an Azure block from an AWS one while reading a log, not enough to name the server; the
    /// owner decided on 2026-09-17 that the player's machine never shows a found server's full address.
    /// </summary>
    public static string Mask(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return address.AddressFamily == AddressFamily.InterNetwork
            ? address.GetAddressBytes()[0] + ".*.*.*"
            : address.ToString().Split(':')[0] + ":*";
    }

    /// <inheritdoc cref="Mask(IPAddress)"/>
    public static string Mask(string address) => IPAddress.TryParse(address, out var parsed) ? Mask(parsed) : "*";

    /// <summary>
    /// The table both write, header first: one row per destination, busiest first within each verdict.
    /// </summary>
    public static List<string> FormatTable(IEnumerable<FlowRow> rows, Func<FlowRow, string> verdict,
        Func<IPAddress, string> known, bool ipv6Verified)
    {
        var lines = new List<string>
        {
            $"{"address",-40} {"sent",9} {"recv",9} {"secs",6}  {"verdict",-8} {"known",-28} udp ports",
        };
        foreach (var row in rows)
        {
            var address = row.Address + (row.Address.AddressFamily == AddressFamily.InterNetworkV6 && !ipv6Verified ? " (v6?)" : "");
            lines.Add($"{address,-40} {row.Sent,9} {row.Received,9} {row.Seconds,6:F0}  {verdict(row),-8} {known(row.Address),-28} " +
                      string.Join(",", row.Ports.Take(8)) + (row.Ports.Count > 8 ? ",..." : ""));
        }
        return lines;
    }
}
