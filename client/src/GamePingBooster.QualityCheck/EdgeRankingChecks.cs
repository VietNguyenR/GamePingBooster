using System.Net;
using GamePingBooster.Core.Net;

namespace GamePingBooster.QualityCheck;

/// <summary>
/// EdgeRanking: which addresses the unblock resolver probes and hands out (2026-10-02). The addresses
/// and handshake times are the owner's VNPT line that day.
/// </summary>
internal static partial class Program
{
    private static IPAddress Ip(string s) => IPAddress.Parse(s);

    private static (IPAddress, TimeSpan) Hs(string ip, double ms) => (Ip(ip), TimeSpan.FromMilliseconds(ms));

    private static void EdgeRankingChecks()
    {
        // PUBG's lobby front: Cloudflare named thirteen Tencent addresses, Google one Akamai.
        IReadOnlyList<IPAddress> cloudflare =
        [
            Ip("43.175.120.74"), Ip("43.168.152.145"), Ip("43.174.128.39"), Ip("43.175.115.189"),
            Ip("43.174.128.40"), Ip("43.175.44.26"), Ip("43.175.115.126"), Ip("43.175.120.249"),
            Ip("43.174.128.71"), Ip("43.175.115.188"), Ip("43.174.128.60"), Ip("43.168.152.146"),
            Ip("43.175.115.180"),
        ];
        IReadOnlyList<IPAddress> google = [Ip("23.77.20.34")];

        var tried = EdgeRanking.Interleave([cloudflare, google], 6);
        Check("PUBG front: Google's one Akamai address is probed despite Cloudflare's thirteen", tried.Contains(Ip("23.77.20.34")),
            string.Join(", ", tried.Select(a => a.ToString())));
        Check("  ...and the cap still holds at six", tried.Length == 6, $"{tried.Length}");
        Check("  ...first of each upstream first: Cloudflare's first, then Google's", tried[0].Equals(cloudflare[0]) && tried[1].Equals(google[0]),
            string.Join(", ", tried.Select(a => a.ToString())));

        var front = EdgeRanking.Rank([Hs("43.175.120.74", 128), Hs("23.77.20.34", 94), Hs("43.168.152.145", 124)]);
        Check("PUBG front: Akamai at 94 ms is handed out, the Tencent edges at 124-128 (which redirect to .com.cn) are not",
            front.Length == 1 && front[0].Equals(Ip("23.77.20.34")), string.Join(", ", front.Select(a => a.ToString())));

        // Steam content: Google names the in-country VNPT cache, Cloudflare foreign Akamai edges.
        var steam = EdgeRanking.Rank([Hs("23.32.91.196", 82), Hs("23.32.91.205", 85), Hs("113.171.230.32", 28)]);
        Check("Steam content: the in-country cache at 28 ms alone, the foreign edges at 82-85 dropped",
            steam.Length == 1 && steam[0].Equals(Ip("113.171.230.32")), string.Join(", ", steam.Select(a => a.ToString())));

        var twins = EdgeRanking.Rank([Hs("13.227.227.24", 131), Hs("13.227.227.110", 127), Hs("13.227.227.117", 140)]);
        Check("Three CloudFront edges within a few ms of each other - all kept, fastest first",
            twins.Length == 3 && twins[0].Equals(Ip("13.227.227.110")), string.Join(", ", twins.Select(a => a.ToString())));

        var fast = EdgeRanking.Rank([Hs("113.171.10.171", 3), Hs("113.171.10.184", 8)]);
        Check("Two caches at 3 and 8 ms - the allowance keeps both, a few ms is noise, not a different CDN",
            fast.Length == 2, string.Join(", ", fast.Select(a => a.ToString())));

        Check("One survivor is handed out as it is", EdgeRanking.Rank([Hs("103.10.124.4", 120)]).Length == 1, "");
        Check("No survivor, nothing handed out (the caller relays the upstream's answer)", EdgeRanking.Rank([]).Length == 0, "");

        var dup = EdgeRanking.Interleave([[Ip("103.10.124.84")], [Ip("103.10.124.84")]], 6);
        Check("The same address from both upstreams is probed once", dup.Length == 1, $"{dup.Length}");

        var one = EdgeRanking.Interleave([[], [Ip("52.223.4.221"), Ip("35.71.163.61")]], 6);
        Check("An upstream with no answer does not stop the other's being used", one.Length == 2, $"{one.Length}");

        Check("No upstream answered - nothing to probe", EdgeRanking.Interleave([], 6).Length == 0, "");

        // FPT, 2026-10-02: through the tunnel, timed from Singapore, Tencent beat the Akamai edge Google named for the
        // player's network - and Tencent's page sends the lobby to mainland China.
        IReadOnlySet<IPAddress> playersNetwork = new HashSet<IPAddress> { Ip("23.66.150.216") };
        var viaRelay = new[] { Ip("43.174.128.40"), Ip("43.175.115.189"), Ip("23.66.150.216") };
        var kept = EdgeRanking.PreferPlayersNetwork(viaRelay, a => a, playersNetwork);
        Check("Through the tunnel: the edge named for the player's network is kept over nearer ones named elsewhere",
            kept.Count == 1 && kept[0].Equals(Ip("23.66.150.216")), string.Join(", ", kept.Select(a => a.ToString())));
        var none = EdgeRanking.PreferPlayersNetwork(new[] { Ip("43.174.128.40") }, a => a, playersNetwork);
        Check("  ...and when none of those works, what works is used rather than nothing", none.Count == 1, $"{none.Count}");
    }
}
