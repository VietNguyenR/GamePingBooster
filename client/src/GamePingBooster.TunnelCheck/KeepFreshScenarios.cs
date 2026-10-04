using System.Net;
using GamePingBooster.Service.Dns;

namespace GamePingBooster.TunnelCheck;

/// <summary>
/// The tunnel's verdicts kept fresh (WorkingEdges._keepFresh, 2026-10-04): PUBG asks its mainland China lobby mirror only
/// when a match ends, and on Viettel it was handed a 4-minute-old edge that no longer answered and waited 12 s on it.
/// Offline: which names a pass picks, not what their probes find - the probes run under a cancelled token.
/// </summary>
internal static partial class Program
{
    private static async Task OnlyAskedListedNamesAreKeptFresh()
    {
        var memory = new WorkingEdges.Memory();
        var now = DateTimeOffset.UtcNow;
        var edge = new[] { IPAddress.Parse("116.131.226.149") };
        WorkingEdges.Entry Ageing() => new(edge, now.AddSeconds(5), now.AddMinutes(30));
        memory.Known["prod-live-front.playbattlegrounds.com.cn"] = Ageing();   // listed, asked, about to age
        memory.Known["store.steampowered.com"] = Ageing();                     // asked, not listed
        memory.Known["accounts.pubg.com"] = Ageing();                          // listed, never asked
        memory.Known["prod-live-front.playbattlegrounds.com"] = new(edge, now.AddMinutes(2), now.AddMinutes(30));  // listed, asked, fresh
        memory.Known["acrt-pcprod.acs.pubg.com"] = new([], now.AddSeconds(5), now.AddMinutes(30));  // listed, asked, "no edge"

        string[] listed = ["prod-live-front.playbattlegrounds.com.cn", "accounts.pubg.com", "prod-live-front.playbattlegrounds.com", "acrt-pcprod.acs.pubg.com"];
        var edges = new WorkingEdges(new DohUpstream(_ => { }), _ => { }, memory: memory,
            keepFresh: TimeSpan.FromSeconds(30), keepFreshFor: listed.Contains);

        foreach (var name in new[] { "prod-live-front.playbattlegrounds.com.cn", "store.steampowered.com",
                     "prod-live-front.playbattlegrounds.com", "acrt-pcprod.acs.pubg.com" })
        {
            await edges.ForAsync(name, null, CancellationToken.None);
        }

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var started = edges.KeepFreshPass(TimeSpan.FromSeconds(10), cancelled.Token);
        Check("Only the listed name that was asked and is about to age is probed again", started == 1, $"{started} started");
        Check("  and its answers carry a 30 s TTL, not two minutes", edges.AnswerTtl == 30, $"{edges.AnswerTtl}");

        var plain = new WorkingEdges(new DohUpstream(_ => { }), _ => { }, memory: new WorkingEdges.Memory());
        Check("The line's own instance keeps the two-minute TTL and probes only when asked",
            plain.AnswerTtl == WorkingEdges.AnswerTtlSeconds && plain.KeepFreshPass(TimeSpan.FromSeconds(10), cancelled.Token) == 0,
            $"{plain.AnswerTtl}");
    }
}
