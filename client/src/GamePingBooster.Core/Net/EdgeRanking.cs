using System.Net;

namespace GamePingBooster.Core.Net;

/// <summary>
/// Which of a name's addresses the unblock resolver probes, and which of the ones that worked it hands out.
///
/// Pure arithmetic over addresses and timings, kept out of the service so QualityCheck can reach every
/// branch without a network. The service's WorkingEdges does the probing and calls these two.
///
/// <b>Why both exist - measured on VNPT, 2026-10-02.</b> PUBG's lobby front has two very different
/// answers depending on who asks. Cloudflare's resolver gets Tencent's CDN (43.17x), whose page is a
/// script that sends the game to <c>prod-live-front.playbattlegrounds.com.cn</c> - mainland China, 190 to
/// 370 ms away - and the shop then pulled 14 MB from there. Google's resolver, which passes the player's
/// subnet, gets Akamai, which serves the real page. The resolver used to take every upstream's addresses
/// in order and probe the first six: Cloudflare returned thirteen, so Google's one Akamai address was
/// never even tried. The same ordering put foreign Akamai edges ahead of the in-country caches Google
/// returns for Steam's content (113.171.x, a quarter of the round trip).
/// </summary>
public static class EdgeRanking
{
    /// <summary>
    /// How much slower than the fastest survivor an edge may be and still be handed out.
    ///
    /// The order alone is not enough: nothing promises the caller uses the first address. So an edge
    /// clearly slower than the best one is dropped, not just listed last. A quarter plus a few
    /// milliseconds keeps edges that are the same thing measured twice (two Akamai addresses a
    /// millisecond apart) and drops a different, further CDN: PUBG's Akamai front handshakes in about
    /// 94 ms on VNPT and the Tencent one in 124-136; Steam's in-country caches in about 30 and the
    /// foreign edges in about 80.
    /// </summary>
    public const double SlowerFactor = 1.25;

    public static readonly TimeSpan SlowerAllowance = TimeSpan.FromMilliseconds(5);

    /// <summary>
    /// Every upstream's answer taken in turn - first of each, then second of each - de-duplicated and
    /// cut to <paramref name="max"/>, so one upstream returning a long list cannot crowd out another's.
    /// </summary>
    public static IPAddress[] Interleave(IReadOnlyList<IReadOnlyList<IPAddress>> perUpstream, int max)
    {
        var result = new List<IPAddress>();
        var longest = perUpstream.Count == 0 ? 0 : perUpstream.Max(list => list.Count);

        for (var i = 0; i < longest && result.Count < max; i++)
        {
            foreach (var list in perUpstream)
            {
                if (i >= list.Count || result.Contains(list[i])) continue;
                result.Add(list[i]);
                if (result.Count == max) break;
            }
        }

        return [.. result];
    }

    /// <summary>
    /// Of the edges that work, the ones a resolver named FOR THE PLAYER'S OWN NETWORK, when there are any - else all.
    ///
    /// For an edge measured through the tunnel, where speed says nothing about the player. On FPT on 2026-10-02 PUBG's
    /// lobby front went through the tunnel and was probed from the relay in Singapore; from there Tencent's edges
    /// (named by Cloudflare, which does not pass the player's subnet) are nearer than the Akamai edge Google names for
    /// a Vietnamese network, so ranking kept Tencent - whose page sends the game to mainland China for the lobby, and
    /// the lobby took minutes. What a CDN names for the player's network is what it built for that network, whatever
    /// it measures from somewhere else.
    /// </summary>
    public static IReadOnlyList<T> PreferPlayersNetwork<T>(
        IReadOnlyList<T> working, Func<T, IPAddress> address, IReadOnlySet<IPAddress> playersNetwork)
    {
        var named = working.Where(w => playersNetwork.Contains(address(w))).ToList();
        return named.Count > 0 ? named : working;
    }

    /// <summary>
    /// The edges that completed a handshake, fastest first, without any that were clearly slower than
    /// the fastest. Never empty when <paramref name="survivors"/> is not.
    /// </summary>
    public static IPAddress[] Rank(IReadOnlyList<(IPAddress Address, TimeSpan Handshake)> survivors)
    {
        if (survivors.Count == 0) return [];

        var ordered = survivors.OrderBy(s => s.Handshake).ToList();
        var limit = ordered[0].Handshake * SlowerFactor + SlowerAllowance;

        return [.. ordered.Where(s => s.Handshake <= limit).Select(s => s.Address).Distinct()];
    }
}
