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
public sealed record RegionMeasurement(
    string RegionId,
    bool HasLandmark,
    double? HomeMs,
    IReadOnlyDictionary<string, double> ViaRelayMs,
    double? DirectMs,
    bool HomeLossy = false,
    IReadOnlySet<string>? LossyVia = null)
{
    public bool IsLossyVia(string relayId) => LossyVia?.Contains(relayId) ?? false;
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
public sealed record RegionDecision(string RegionId, RegionPath Path, double? ChosenMs, double? HomeMs, string Reason,
    double? ChosenScore = null, double? HomeScore = null);

/// <summary>
/// Chooses a path for every region of a game from what a measurement pass found. Pure: same inputs, same
/// plan. The rules are docs/MULTI-TUNNEL.md 5.5, and three of them are the guarantees the whole design
/// stands on:
///
///   G2  a region is never given a path that scores worse than home - it leaves home only by
///       <see cref="RescanScore.WorthMoving"/> (max(5 ms, 10%)), and hysteresis may keep an old path only
///       while that path still scores no worse than home. A path's score is its round trip, plus
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
        // summed over the regions, each region re-decided among the set's relays - at most C(7, 2) = 21 sets.
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
            : $"home {homeText} - best other {bestText} is not faster by {Margin(homeScore):F0} ms";
        if (best is not null && RescanScore.WorthMoving(homeScore, bestScore))
        {
            chosen = RegionPath.Via(best);
            chosenMs = bestMs;
            chosenScore = bestScore;
            reason = $"{bestText} against home {homeText}";
        }

        // Rule 5 (G8). Direct carries no penalty: its loss is the player's own line's, which every path shares.
        if (options.AllowDirect && region.DirectMs is { } direct &&
            RescanScore.WorthMoving(chosenScore, direct) &&
            (best is null || RescanScore.WorthMoving(bestScore, direct)))
        {
            chosen = RegionPath.DirectPath;
            chosenMs = direct;
            chosenScore = direct;
            reason = $"direct {direct:F0} ms against home {homeText}" + (best is null ? "" : $" and {bestText}");
        }

        // Rule 7: hysteresis. The old path stays unless the new one beats its CURRENT score by the margin -
        // and only while the old path still scores no worse than home (G2 holds through hysteresis too).
        if (previous is not null && previous.TryGetValue(region.RegionId, out var was) && was != chosen &&
            Current(was, region, homeRelayId, options, allowedRelays, home) is { } current &&
            current.Score <= homeScore && !RescanScore.WorthMoving(current.Score, chosenScore))
        {
            return new(region.RegionId, was, current.Ms, home,
                $"stays on {was} at {Label(current.Ms, current.Score > current.Ms)} - {chosen} at " +
                $"{Label(chosenMs, chosenScore > chosenMs)} is not faster by {Margin(current.Score):F0} ms",
                current.Score, homeScore);
        }

        return new(region.RegionId, chosen, chosenMs, home, reason, chosenScore, homeScore);
    }

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

    private static double Margin(double ms) => Profiles.RelayPaths.HelpMargin(ms);

    private static int OrderOf(IReadOnlyList<string> order, string id)
    {
        for (var i = 0; i < order.Count; i++)
        {
            if (order[i].Equals(id, StringComparison.Ordinal)) return i;
        }
        return int.MaxValue;
    }
}
