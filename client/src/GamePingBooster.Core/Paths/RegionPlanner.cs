using GamePingBooster.Core.Quality;

namespace GamePingBooster.Core.Paths;

/// <summary>How a region's packets leave. See docs/MULTI-TUNNEL.md, section 4.</summary>
public enum PathKind
{
    /// <summary>Through the home tunnel - the one connect chose, exactly as today.</summary>
    Home,

    /// <summary>Through a tunnel to another relay.</summary>
    Relay,

    /// <summary>Not routed: over the player's own line.</summary>
    Direct,
}

public readonly record struct RegionPath(PathKind Kind, string? RelayId = null)
{
    public static readonly RegionPath HomePath = new(PathKind.Home);
    public static readonly RegionPath DirectPath = new(PathKind.Direct);
    public static RegionPath Via(string relayId) => new(PathKind.Relay, relayId);

    public override string ToString() => Kind switch
    {
        PathKind.Relay => RelayId!,
        PathKind.Direct => "direct",
        _ => "home",
    };
}

/// <summary>
/// What one pass measured for one region: the median of <see cref="RescanScore.Samples"/> echoes to the
/// region's landmark on each path, null where too few answered. The same instrument on every path - a
/// median is never compared with a best-of.
/// </summary>
/// <param name="HomeMs">Through the home tunnel.</param>
/// <param name="ViaRelayMs">Through each other relay, by relay id. The home relay is not in here.</param>
/// <param name="DirectMs">Over the player's own line.</param>
/// <param name="HomeLossy">The home tunnel is losing packets (<see cref="RelayLoss"/>): home is compared as if it were
/// <see cref="RelayLoss.PenaltyMs"/> slower.</param>
/// <param name="LossyVia">The other relays, by id, whose way measured for this region lost packets - the same penalty.</param>
/// <param name="HomeLegMs">The round trip to home's relayd alone, the first leg of <paramref name="HomeMs"/>. Null when not
/// measured; then home is never taken to be inside the region (<see cref="RegionPlanner.InsideRegionMs"/>).</param>
/// <param name="ViaRelayLegMs">The same for each other relay: the first leg of its number in <paramref name="ViaRelayMs"/>,
/// down the same way in. A relay with none is never taken to be inside the region.</param>
public sealed record RegionMeasurement(
    string RegionId,
    bool HasLandmark,
    double? HomeMs,
    IReadOnlyDictionary<string, double> ViaRelayMs,
    double? DirectMs,
    bool HomeLossy = false,
    IReadOnlySet<string>? LossyVia = null,
    double? HomeLegMs = null,
    IReadOnlyDictionary<string, double>? ViaRelayLegMs = null)
{
    public bool IsLossyVia(string relayId) => LossyVia?.Contains(relayId) ?? false;

    /// <summary>
    /// The relay's second leg - from it to the landmark - at most <paramref name="limitMs"/>: <see cref="RegionPlanner.InsideRegionMs"/>
    /// to be taken, <see cref="RegionPlanner.InsideHoldMs"/> to be kept.
    /// </summary>
    public bool IsInsideVia(string relayId, double limitMs = RegionPlanner.InsideRegionMs) =>
        ViaRelayLegMs is { } legs && legs.TryGetValue(relayId, out var leg) && ViaRelayMs.TryGetValue(relayId, out var ms) &&
        ms - leg <= limitMs;

    /// <summary>The same for home.</summary>
    public bool IsHomeInside => HomeLegMs is { } leg && HomeMs is { } ms && ms - leg <= RegionPlanner.InsideRegionMs;
}

/// <param name="AllowDirect">The game may leave a region unrouted (Game.regionDirect).</param>
/// <param name="MaxTunnels">Tunnels open at once, home included. At least 1.</param>
/// <param name="RelayOrder">Relay ids in profile order - the last tie-break, so a plan is deterministic.</param>
/// <param name="TargetRegionId">The region the game will most likely put this player in - the nearest over the player's
/// own line as connect measured it, or the last match's. Over the cap it counts double (TargetWeight). Null when unknown.</param>
public sealed record PlannerOptions(bool AllowDirect, int MaxTunnels, IReadOnlyList<string> RelayOrder, string? TargetRegionId = null);

/// <param name="ChosenMs">What the chosen path measured; null only when the region could not be measured.</param>
/// <param name="Reason">One line for the log and the quality record.</param>
/// <param name="ChosenScore">What the chosen path was compared on: <paramref name="ChosenMs"/>, plus
/// <see cref="RelayLoss.PenaltyMs"/> when it loses packets. Null where <paramref name="ChosenMs"/> is.</param>
/// <param name="HomeScore">The same for home.</param>
/// <param name="Inside">The path is a relay inside the region taken by rule 4b - no slower than home, not by the margin.</param>
/// <param name="BestOtherId">The fastest other relay measured for the region, whether or not it was taken; null with none.
/// The app names it beside a region that stays home, with what it gained and what the margin asked.</param>
/// <param name="BestOtherMs">What <paramref name="BestOtherId"/> measured.</param>
/// <param name="MarginMs">What leaving home asked: <see cref="RegionPlanner.LeaveMargin"/> of home's score.</param>
public sealed record RegionDecision(string RegionId, RegionPath Path, double? ChosenMs, double? HomeMs, string Reason,
    double? ChosenScore = null, double? HomeScore = null, bool Inside = false,
    string? BestOtherId = null, double? BestOtherMs = null, double? MarginMs = null);

/// <summary>
/// Chooses a path for every region of a game from what a measurement pass found. Pure: same inputs, same
/// plan. The rules are docs/MULTI-TUNNEL.md 5.5, and three of them are the guarantees the whole design
/// stands on:
///
///   G2  a region is never given a path that scores worse than home - it leaves home only by
///       <see cref="LeaveMargin"/> (max(5 ms, 10%)) or for a relay inside the region no slower than home (rule 4b), and
///       hysteresis may keep an old path only while that path still scores no worse than home. The one exception: a region
///       already on a relay inside it stays there until home beats it by the margin, so near-equal passes do not trade it. A path's score is its round trip, plus
///       <see cref="RelayLoss.PenaltyMs"/> when it loses packets (since 2026-09-29): without loss that is the
///       round trip exactly, and a home that loses packets is left for a clean relay up to that much slower;
///   G3  a region that cannot be measured fairly - no landmark, or no number through home - stays home,
///       which is exactly what today's client does with every region;
///   G8  direct only when the game allows it and it beats both the chosen path and the best relay by the
///       same margin.
///
/// It chooses RELAYS, never ways into one. Two paths into one relayd would be one session with its return
/// address fought over (G5); which way in a relay is reached by stays entry switching's business.
/// </summary>
public static class RegionPlanner
{
    /// <summary>
    /// What a region's path must beat home by, and an open path by, to be taken: max(5 ms, 10%), connect's margin
    /// (<see cref="Profiles.RelayPaths.HelpMargin"/>). It is what one pass varies by.
    ///
    /// On 2026-10-02 it went to max(3.5, 10%), max(3, 10%) and 3 flat in one night, and came back: a Hanoi player's Naraka
    /// Ho Chi Minh City matches stayed home at 37.6 ms with vn-3 at 33.3 (41 ms in the game against 36), and no margin
    /// held them on vn-3 - passes read vn-3 1.3 to 4.3 ms faster. The margin was not the fault: vn-3 sits next to the
    /// servers, and rule 4b now takes it on that. Replayed over fourteen days of prod plans, path changes inside a session
    /// were 399 at max(5, 10%), 546 at 3 flat and 931 with no margin; flips back 32, 51 and 162.
    /// </summary>
    public static double LeaveMargin(double ms) => Profiles.RelayPaths.HelpMargin(ms);

    /// <summary>
    /// A relay whose second leg - from it to the region's landmark, its number less the round trip to its relayd - is at
    /// most this is inside the region: in the game servers' own datacentre or next to it. Measured from every relay to
    /// every live landmark on 2026-10-02: the relays next to servers 0.8-5.5 ms (vn-3 to Ho Chi Minh City 4.2, sg-1..4 to
    /// Singapore, hk-2 and hk-3 to Hong Kong), the nearest other 13.6 (sg-4 to Jakarta), then 14.9 (vn-1 to Ho Chi Minh
    /// City). Ten, not six: a plan's second leg is a median of eight echoes less the BEST of a ping burst, which reads
    /// 1-2 ms high - vn-3's came to 7.4 on one pass.
    /// </summary>
    public const double InsideRegionMs = 10;

    /// <summary>
    /// How far a relay's second leg may read and still keep a region rule 4b gave it - three over
    /// <see cref="InsideRegionMs"/>, so one that measures near the line is not taken on one pass and dropped on the next.
    /// Found by PlansDoNotFlapOnNoise: a relay 9 ms on read 11 on a jittered pass and the region went home and back.
    /// Still clear of the nearest relay outside a region, 13.6.
    /// </summary>
    public const double InsideHoldMs = InsideRegionMs + 3;

    /// <summary>Whether <paramref name="candidateMs"/> beats <paramref name="currentMs"/> by <see cref="LeaveMargin"/>.</summary>
    public static bool WorthLeaving(double currentMs, double candidateMs) => currentMs - candidateMs >= LeaveMargin(currentMs);

    public static List<RegionDecision> Plan(
        string homeRelayId,
        IReadOnlyList<RegionMeasurement> regions,
        PlannerOptions options,
        IReadOnlyDictionary<string, RegionPath>? previous = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxTunnels, 1);

        var decisions = regions.Select(r => Decide(homeRelayId, r, options, previous, allowedRelays: null)).ToList();

        var used = decisions.Where(d => d.Path.Kind == PathKind.Relay).Select(d => d.Path.RelayId!).Distinct().ToList();
        var room = options.MaxTunnels - 1;
        if (used.Count <= room) return decisions;
        if (room == 0) return regions.Select(r => Decide(homeRelayId, r, options, previous, [])).ToList();

        // Over the cap: which `room` relays to keep. Every set of that size is scored by what it saves over home,
        // summed over the regions, each region re-decided among the set's relays - at most C(7, 3) = 35 sets.
        //
        //   - The region the game will put this player in counts double (TargetWeight). Trading the player's own
        //     region for a little more elsewhere is the wrong trade - 2026-09-28, an Apex player on Singapore held on
        //     vn-3 (47 ms) with sg-1 at 38. Double, not first: a hard pin let 5 ms there evict 46 ms on another region.
        //   - A set the last plan already used stays unless another clears SetWorthMoving, so jitter never flaps an
        //     open tunnel. It used to stay whatever the other saved, and then a better relay could never enter once
        //     two were open.
        var inPrevious = previous?.Values.Where(p => p.Kind == PathKind.Relay).Select(p => p.RelayId!).ToHashSet() ?? [];

        HashSet<string>? best = null;
        var bestSaving = double.NegativeInfinity;
        foreach (var set in Combinations(used.OrderBy(id => OrderOf(options.RelayOrder, id)).ThenBy(id => id, StringComparer.Ordinal).ToList(), room))
        {
            var saving = SavingOf(homeRelayId, regions, options, previous, set);
            if (saving > bestSaving + 1e-9)
            {
                best = set;
                bestSaving = saving;
            }
        }

        // The set the previous plan holds, topped up the way the old rule did - by what each relay saves alone.
        if (inPrevious.Overlaps(used))
        {
            var saves = used.ToDictionary(
                id => id,
                id => decisions.Where(d => d.Path.Kind == PathKind.Relay && d.Path.RelayId == id)
                    .Sum(d => Weight(options, d.RegionId) * (d.HomeScore!.Value - d.ChosenScore!.Value)));
            var incumbent = used
                .OrderByDescending(id => inPrevious.Contains(id))
                .ThenByDescending(id => saves[id])
                .ThenBy(id => OrderOf(options.RelayOrder, id))
                .ThenBy(id => id, StringComparer.Ordinal)
                .Take(room)
                .ToHashSet(StringComparer.Ordinal);
            if (best is null || !SetWorthMoving(homeRelayId, regions, options, previous, incumbent, best)) best = incumbent;
        }

        return regions.Select(r => Decide(homeRelayId, r, options, previous, best!)).ToList();
    }

    /// <summary>How much more the region the game is expected to use counts when relays compete for the cap.</summary>
    public const double TargetWeight = 2.0;

    private static double Weight(PlannerOptions options, string regionId) =>
        options.TargetRegionId is { } target && target.Equals(regionId, StringComparison.Ordinal) ? TargetWeight : 1.0;

    /// <summary>What a set of relays saves over home, summed over every region re-decided among them and weighted.</summary>
    private static double SavingOf(string homeRelayId, IReadOnlyList<RegionMeasurement> regions, PlannerOptions options,
        IReadOnlyDictionary<string, RegionPath>? previous, HashSet<string> relays) =>
        regions.Select(r => Decide(homeRelayId, r, options, previous, relays))
            .Where(d => d.Path.Kind != PathKind.Home && d.HomeScore is not null && d.ChosenScore is not null)
            .Sum(d => Weight(options, d.RegionId) * (d.HomeScore!.Value - d.ChosenScore!.Value));

    /// <summary>
    /// Whether <paramref name="candidate"/> is worth closing the open <paramref name="incumbent"/> for. What the swap
    /// gains, net of what the regions it pushes onto a worse path lose, must reach the margin each improved region
    /// would pay to move on its own (max(5 ms, 10%) of what its path measures now) - summed, and both weighted. One
    /// margin for the whole set was tried first and flapped: jitter of a quarter margin per region, summed over five
    /// regions, clears a single margin (PathCheck, PlansDoNotFlapOnNoise).
    /// </summary>
    private static bool SetWorthMoving(string homeRelayId, IReadOnlyList<RegionMeasurement> regions, PlannerOptions options,
        IReadOnlyDictionary<string, RegionPath>? previous, HashSet<string> incumbent, HashSet<string> candidate)
    {
        double gain = 0, bar = 0;
        foreach (var r in regions)
        {
            var now = Decide(homeRelayId, r, options, previous, incumbent);
            var then = Decide(homeRelayId, r, options, previous, candidate);
            if (now.Path == then.Path) continue;
            if ((now.ChosenScore ?? now.HomeScore) is not { } a || (then.ChosenScore ?? then.HomeScore) is not { } b) continue;
            var w = Weight(options, r.RegionId);
            gain += w * (a - b);
            if (b < a) bar += w * Margin(a);
        }
        return bar > 0 && gain >= bar;
    }

    /// <summary>Every subset of <paramref name="items"/> of size <paramref name="size"/>, in a fixed order.</summary>
    private static IEnumerable<HashSet<string>> Combinations(List<string> items, int size)
    {
        var index = Enumerable.Range(0, size).ToArray();
        while (true)
        {
            yield return index.Select(i => items[i]).ToHashSet(StringComparer.Ordinal);
            var k = size - 1;
            while (k >= 0 && index[k] == items.Count - size + k) k--;
            if (k < 0) yield break;
            index[k]++;
            for (var j = k + 1; j < size; j++) index[j] = index[j - 1] + 1;
        }
    }

    private static RegionDecision Decide(
        string homeRelayId,
        RegionMeasurement region,
        PlannerOptions options,
        IReadOnlyDictionary<string, RegionPath>? previous,
        HashSet<string>? allowedRelays)
    {
        // Rule 1 (G3).
        if (!region.HasLandmark)
        {
            return new(region.RegionId, RegionPath.HomePath, null, null, "no landmark - follows home");
        }
        if (region.HomeMs is not { } home)
        {
            return new(region.RegionId, RegionPath.HomePath, null, null,
                "the landmark did not answer through home - follows home");
        }

        var homeScore = RelayLoss.Score(home, region.HomeLossy);
        var homeText = Label(home, region.HomeLossy);

        // Rules 2-3: candidates are relays with a number of their own for THIS region, compared on their score.
        string? best = null;
        var bestMs = double.PositiveInfinity;
        var bestScore = double.PositiveInfinity;
        foreach (var (relayId, ms) in region.ViaRelayMs
                     .Where(kv => !kv.Key.Equals(homeRelayId, StringComparison.Ordinal))
                     .Where(kv => allowedRelays is null || allowedRelays.Contains(kv.Key))
                     .OrderBy(kv => OrderOf(options.RelayOrder, kv.Key))
                     .ThenBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var score = RelayLoss.Score(ms, region.IsLossyVia(relayId));
            if (score < bestScore)
            {
                best = relayId;
                bestMs = ms;
                bestScore = score;
            }
        }
        var bestText = best is null ? "" : $"{best} {Label(bestMs, region.IsLossyVia(best))}";

        // Rule 4 (G2): leave home only by the margin.
        var chosen = RegionPath.HomePath;
        var chosenMs = home;
        var chosenScore = homeScore;
        var reason = best is null
            ? $"home {homeText} - no other relay measured"
            : $"home {homeText} - best other {bestText} is not faster by {Margin(homeScore):0.#} ms";
        if (best is not null && WorthLeaving(homeScore, bestScore))
        {
            chosen = RegionPath.Via(best);
            chosenMs = bestMs;
            chosenScore = bestScore;
            reason = $"{bestText} against home {homeText}";
        }

        // Rule 4b: a relay inside the region - next to its game servers (InsideRegionMs) - is taken whenever home would
        // keep the region and the relay scores no worse than home, without the margin. Home is not inside the region, or
        // there is nothing to prefer. The margin is there for paths that only measured faster; this one is shorter by where
        // it is. On 2026-10-02 vn-3, in Ho Chi Minh City next to Naraka's servers, read 1.3 to 4.3 ms faster than a Hanoi
        // home pass after pass - 36 ms in the game against 41 - and no margin held it. Losing packets, it is not inside.
        var inside = false;
        if (chosen.Kind == PathKind.Home && !region.IsHomeInside)
        {
            string? near = null;
            var nearMs = double.PositiveInfinity;
            foreach (var (relayId, ms) in region.ViaRelayMs
                         .Where(kv => !kv.Key.Equals(homeRelayId, StringComparison.Ordinal))
                         .Where(kv => allowedRelays is null || allowedRelays.Contains(kv.Key))
                         .Where(kv => !region.IsLossyVia(kv.Key) && region.IsInsideVia(kv.Key))
                         .OrderBy(kv => OrderOf(options.RelayOrder, kv.Key))
                         .ThenBy(kv => kv.Key, StringComparer.Ordinal))
            {
                if (ms <= homeScore && ms < nearMs)
                {
                    near = relayId;
                    nearMs = ms;
                }
            }
            if (near is not null)
            {
                chosen = RegionPath.Via(near);
                chosenMs = nearMs;
                chosenScore = nearMs;
                inside = true;
                var secondLeg = nearMs - region.ViaRelayLegMs![near];
                reason = $"{near} {nearMs:F0} ms, inside the region ({secondLeg:F0} ms on from the relay), against home {homeText}";
            }
        }

        // Rule 5 (G8). Direct carries no penalty: its loss is the player's own line's, which every path shares.
        if (options.AllowDirect && region.DirectMs is { } direct &&
            RescanScore.WorthMoving(chosenScore, direct) &&
            (best is null || RescanScore.WorthMoving(bestScore, direct)))
        {
            chosen = RegionPath.DirectPath;
            chosenMs = direct;
            chosenScore = direct;
            inside = false;
            reason = $"direct {direct:F0} ms against home {homeText}" + (best is null ? "" : $" and {bestText}");
        }

        double? otherMs = best is null ? null : bestMs;
        var margin = Margin(homeScore);

        // Rule 7: hysteresis. The old path stays unless the new one beats its CURRENT score by the margin -
        // and only while the old path still scores no worse than home (G2 holds through hysteresis too).
        //
        // Two exceptions, both for 4b. Home does not hold a region against a relay inside it: that move never needed the
        // margin, and holding it would undo 4b for the session after one pass that read the relay a millisecond slow. And a
        // region on a relay inside it stays there until home - or anything - beats it by the margin, even when it now reads
        // a little slower than home: a relay next to the servers and a home that measure alike would otherwise trade the
        // region on every pass. The one place a path may score worse than home, and never by the margin.
        if (previous is not null && previous.TryGetValue(region.RegionId, out var was) && was != chosen &&
            !(inside && was.Kind == PathKind.Home) &&
            Current(was, region, homeRelayId, options, allowedRelays, home) is { } current &&
            (current.Score <= homeScore || IsCleanInside(was, region)) && !WorthLeaving(current.Score, chosenScore))
        {
            var wasInside = IsCleanInside(was, region);
            return new(region.RegionId, was, current.Ms, home,
                $"stays on {was} at {Label(current.Ms, current.Score > current.Ms)}{(wasInside ? ", inside the region" : "")} - " +
                $"{chosen} at {Label(chosenMs, chosenScore > chosenMs)} is not faster by {Margin(current.Score):0.#} ms",
                current.Score, homeScore, Inside: wasInside, BestOtherId: best, BestOtherMs: otherMs, MarginMs: margin);
        }

        return new(region.RegionId, chosen, chosenMs, home, reason, chosenScore, homeScore, inside, best, otherMs, margin);
    }

    /// <summary>A relay path still inside the region (<see cref="InsideHoldMs"/>) and not losing packets - what rule 7 holds for 4b.</summary>
    private static bool IsCleanInside(RegionPath path, RegionMeasurement region) =>
        path.Kind == PathKind.Relay && region.IsInsideVia(path.RelayId!, InsideHoldMs) && !region.IsLossyVia(path.RelayId!);

    /// <summary>"38 ms", or "38 ms losing packets" for a path the penalty applies to.</summary>
    private static string Label(double ms, bool lossy) => lossy ? $"{ms:F0} ms losing packets" : $"{ms:F0} ms";

    /// <summary>What a previous path measures and scores now, or null when it is no longer available to this plan.</summary>
    private static (double Ms, double Score)? Current(RegionPath was, RegionMeasurement region, string homeRelayId,
        PlannerOptions options, HashSet<string>? allowedRelays, double home) => was.Kind switch
    {
        PathKind.Home => (home, RelayLoss.Score(home, region.HomeLossy)),
        PathKind.Direct when options.AllowDirect && region.DirectMs is { } direct => (direct, direct),
        PathKind.Relay when was.RelayId is { } id && !id.Equals(homeRelayId, StringComparison.Ordinal) &&
                            (allowedRelays is null || allowedRelays.Contains(id)) &&
                            region.ViaRelayMs.TryGetValue(id, out var ms) => (ms, RelayLoss.Score(ms, region.IsLossyVia(id))),
        _ => null,
    };

    private static double Margin(double ms) => LeaveMargin(ms);

    private static int OrderOf(IReadOnlyList<string> order, string id)
    {
        for (var i = 0; i < order.Count; i++)
        {
            if (order[i].Equals(id, StringComparison.Ordinal)) return i;
        }
        return int.MaxValue;
    }
}
