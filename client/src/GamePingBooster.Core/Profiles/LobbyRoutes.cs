using System.Net;
using System.Net.Sockets;

namespace GamePingBooster.Core.Profiles;

/// <summary>
/// Decides which of a game's <see cref="GameEntry.LobbyAddresses"/> may become routes, and says
/// why the others may not.
///
/// Stricter than anything applied to the game ranges, on purpose. Game routes exist only while
/// the game runs; these exist for as long as the tunnel is up, so a mistake here is not a bad
/// match but a player's whole evening of other traffic going somewhere it should not. Every rule
/// below is a way that has actually happened, or would, with a list somebody typed by hand:
///
///   wider than /32   drags everybody else's traffic through the relay while the game is closed
///   private, CGNAT   breaks the player's own network - the router, the NAS, the printer
///   a relay's IP     competes with the pinned /32 that keeps relay traffic OFF the tunnel;
///                    equal prefix, equal metric, and the tunnel's own packets can loop into it
///   a landmark       makes the game measure one region through the relay and the rest direct
///
/// Refused entries are reported, never silently dropped, and never fail the connection: a lobby
/// that stays on the normal path is exactly what the player had before this existed.
/// </summary>
public static class LobbyRoutes
{
    public sealed record Rejection(string Entry, string Reason);

    /// <summary>
    /// Returns the entries that are safe to route, as "a.b.c.d/32", in their original order and
    /// without duplicates. Everything refused lands in <paramref name="rejected"/> with a reason.
    /// </summary>
    /// <param name="entries">The profile's lobby addresses, as written.</param>
    /// <param name="relayEndpoints">Every relay the client might pin, as "ip:port". Not just the
    /// current one: failover pins a different relay without reinstalling these.</param>
    /// <param name="landmarks">Every landmark of every region of the game.</param>
    public static List<string> ToHostRoutes(IEnumerable<string> entries, IEnumerable<string> relayEndpoints,
        IEnumerable<string> landmarks, List<Rejection> rejected)
    {
        var relayIps = new HashSet<uint>();
        foreach (var endpoint in relayEndpoints)
        {
            // A hostname cannot be compared without resolving it, and resolving here would make
            // route installation depend on DNS. Test-Profile.ps1 already warns about hostnames.
            var host = (endpoint ?? "").Split(':')[0];
            if (TryParseStrict(host, out var value)) relayIps.Add(value);
        }

        var landmarkIps = new HashSet<uint>();
        foreach (var landmark in landmarks)
        {
            if (TryParseStrict((landmark ?? "").Trim(), out var value)) landmarkIps.Add(value);
        }

        var routes = new List<string>();
        var seen = new HashSet<uint>();
        foreach (var raw in entries)
        {
            var entry = (raw ?? "").Trim();
            var address = entry;

            var slash = entry.IndexOf('/');
            if (slash >= 0)
            {
                if (entry[(slash + 1)..] != "32")
                {
                    rejected.Add(new Rejection(entry,
                        "only single addresses are allowed (/32). These stay routed while the game is closed, " +
                        "so a range would send other programs' traffic through the relay"));
                    continue;
                }
                address = entry[..slash];
            }

            if (!TryParseStrict(address, out var value))
            {
                rejected.Add(new Rejection(entry, "not an IPv4 address"));
                continue;
            }

            if (SpecialUse(value) is { } why)
            {
                rejected.Add(new Rejection(entry, why));
                continue;
            }

            if (relayIps.Contains(value))
            {
                rejected.Add(new Rejection(entry,
                    "it is a relay's own address, which is pinned to the physical adapter so tunnel " +
                    "traffic cannot loop back into the tunnel"));
                continue;
            }

            if (landmarkIps.Contains(value))
            {
                rejected.Add(new Rejection(entry,
                    "it is a landmark. The game measures regions against those, and routing one makes it " +
                    "compare a tunnelled path against direct ones"));
                continue;
            }

            if (!seen.Add(value)) continue;
            routes.Add($"{address}/32");
        }

        return routes;
    }

    /// <summary>
    /// Dotted-quad IPv4 and nothing else. IPAddress.TryParse alone accepts "1" as 0.0.0.1 and
    /// "10.1" as 10.0.0.1, which would turn a typo into a route to somewhere nobody meant; the
    /// round trip through ToString() rejects every form except the canonical one.
    /// </summary>
    private static bool TryParseStrict(string text, out uint value)
    {
        value = 0;
        if (!IPAddress.TryParse(text, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork) return false;
        if (ip.ToString() != text) return false;

        var bytes = ip.GetAddressBytes();
        value = (uint)(bytes[0] << 24 | bytes[1] << 16 | bytes[2] << 8 | bytes[3]);
        return true;
    }

    private static string? SpecialUse(uint ip)
    {
        static bool In(uint ip, uint network, int bits) => (ip >> (32 - bits)) == (network >> (32 - bits));

        if (In(ip, 0x00000000, 8)) return "0.0.0.0/8 is not a destination";
        if (In(ip, 0x0A000000, 8)) return "10.0.0.0/8 is private - it would break the player's own network";
        if (In(ip, 0x64400000, 10)) return "100.64.0.0/10 is carrier-grade NAT - it is the ISP's network, not a server";
        if (In(ip, 0x7F000000, 8)) return "127.0.0.0/8 is this machine";
        if (In(ip, 0xA9FE0000, 16)) return "169.254.0.0/16 is link-local";
        if (In(ip, 0xAC100000, 12)) return "172.16.0.0/12 is private - it would break the player's own network";
        if (In(ip, 0xC0A80000, 16)) return "192.168.0.0/16 is private - it would break the player's own network";
        if (In(ip, 0xE0000000, 4)) return "224.0.0.0/4 is multicast";
        if (In(ip, 0xF0000000, 4)) return "240.0.0.0/4 is reserved";
        return null;
    }
}
