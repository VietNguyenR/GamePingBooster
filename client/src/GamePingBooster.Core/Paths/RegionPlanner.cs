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
public sealed record RegionMeasurement(
    string RegionId,
    bool HasLandmark,
    double? HomeMs,
    IReadOnlyDictionary<string, double> ViaRelayMs,
    double? DirectMs);

/// <param name="AllowDirect">The game may leave a region unrouted (Game.regionDirect).</param>
/// <param name="MaxTunnels">Tunnels open at once, home included. At least 1.</param>
/// <param name="RelayOrder">Relay ids in profile order - the last tie-break, so a plan is deterministic.</param>
public sealed record PlannerOptions(bool AllowDirect, int MaxTunnels, IReadOnlyList<string> RelayOrder);

/// <param name="ChosenMs">What the chosen path measured; null only when the region could not be measured.</param>
/// <param name="Reason">One line for the log and the quality record.</param>
public sealed record RegionDecision(string RegionId, RegionPath Path, double? ChosenMs, double? HomeMs, string Reason);

/// <summary>
/// Chooses a path for every region of a game from what a measurement pass found. Pure: same inputs, same
/// plan. The rules are docs/MULTI-TUNNEL.md 5.5, and three of them are the guarantees the whole design
/// stands on:
///
///   G2  a region is never given a path that measured slower than home - it leaves home only by
///       <see cref="RescanScore.WorthMoving"/> (max(5 ms, 10%)), and hysteresis may keep an old path only
///       while that path is still no slower than home;
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

        // Over the cap: keep the relays the last plan already used first - they are open, and dropping one
        // because another edged ahead by a millisecond would flap - then the ones saving the most.
        var saving = used.ToDictionary(
            id => id,
            id => decisions.Where(d => d.Path.Kind == PathKind.Relay && d.Path.RelayId == id)
                .Sum(d => d.HomeMs!.Value - d.ChosenMs!.Value));
        var inPrevious = previous?.Values.Where(p => p.Kind == PathKind.Relay).Select(p => p.RelayId!).ToHashSet() ?? [];
        var kept = used
            .OrderByDescending(id => inPrevious.Contains(id))
            .ThenByDescending(id => saving[id])
            .ThenBy(id => OrderOf(options.RelayOrder, id))
            .ThenBy(id => id, StringComparer.Ordinal)
            .Take(room)
            .ToHashSet(StringComparer.Ordinal);

        return regions.Select(r => Decide(homeRelayId, r, options, previous, kept)).ToList();
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

        // Rules 2-3: candidates are relays with a number of their own for THIS region.
        string? best = null;
        var bestMs = double.PositiveInfinity;
        foreach (var (relayId, ms) in region.ViaRelayMs
                     .Where(kv => !kv.Key.Equals(homeRelayId, StringComparison.Ordinal))
                     .Where(kv => allowedRelays is null || allowedRelays.Contains(kv.Key))
                     .OrderBy(kv => OrderOf(options.RelayOrder, kv.Key))
                     .ThenBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (ms < bestMs)
            {
                best = relayId;
                bestMs = ms;
            }
        }

        // Rule 4 (G2): leave home only by the margin.
        var chosen = RegionPath.HomePath;
        var chosenMs = home;
        var reason = best is null
            ? $"home {home:F0} ms - no other relay measured"
            : $"home {home:F0} ms - best other {best} {bestMs:F0} ms is not faster by {Margin(home):F0} ms";
        if (best is not null && RescanScore.WorthMoving(home, bestMs))
        {
            chosen = RegionPath.Via(best);
            chosenMs = bestMs;
            reason = $"{best} {bestMs:F0} ms against home {home:F0} ms";
        }

        // Rule 5 (G8).
        if (options.AllowDirect && region.DirectMs is { } direct &&
            RescanScore.WorthMoving(chosenMs, direct) &&
            (best is null || RescanScore.WorthMoving(bestMs, direct)))
        {
            chosen = RegionPath.DirectPath;
            chosenMs = direct;
            reason = $"direct {direct:F0} ms against home {home:F0} ms" + (best is null ? "" : $" and {best} {bestMs:F0} ms");
        }

        // Rule 7: hysteresis. The old path stays unless the new one beats its CURRENT number by the margin -
        // and only while the old path is still no slower than home (G2 holds through hysteresis too).
        if (previous is not null && previous.TryGetValue(region.RegionId, out var was) && was != chosen &&
            CurrentMs(was, region, homeRelayId, options, allowedRelays, home) is { } wasMs &&
            wasMs <= home && !RescanScore.WorthMoving(wasMs, chosenMs))
        {
            return new(region.RegionId, was, wasMs, home,
                $"stays on {was} at {wasMs:F0} ms - {chosen} at {chosenMs:F0} ms is not faster by {Margin(wasMs):F0} ms");
        }

        return new(region.RegionId, chosen, chosenMs, home, reason);
    }

    /// <summary>What a previous path measures now, or null when it is no longer available to this plan.</summary>
    private static double? CurrentMs(RegionPath was, RegionMeasurement region, string homeRelayId, PlannerOptions options,
        HashSet<string>? allowedRelays, double home) => was.Kind switch
    {
        PathKind.Home => home,
        PathKind.Direct => options.AllowDirect ? region.DirectMs : null,
        PathKind.Relay when was.RelayId is { } id && !id.Equals(homeRelayId, StringComparison.Ordinal) &&
                            (allowedRelays is null || allowedRelays.Contains(id)) &&
                            region.ViaRelayMs.TryGetValue(id, out var ms) => ms,
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
