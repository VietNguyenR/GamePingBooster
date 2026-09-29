namespace GamePingBooster.Core.Quality;

/// <summary>One way into a relay, measured by a round of Probes: its median round trip to relayd, and what went unanswered.</summary>
public sealed record WaySample(string Id, double? MedianMs, PingLoss Loss)
{
    /// <summary>What the check compares: the median, plus the loss penalty; no answer at all is worst of all.</summary>
    public double Score => MedianMs is { } ms ? RelayLoss.Score(ms, Loss) : double.PositiveInfinity;

    public override string ToString() => MedianMs is { } ms
        ? $"{Id} {ms:F0} ms{(Loss.IsLossy ? $", losing packets ({Loss})" : RelayLoss.Note(Loss))}"
        : $"{Id} no answer";
}

/// <summary>What <see cref="WayCheck.Choose"/> decided, with the line for the log.</summary>
public sealed record WayChoice(string? MoveTo, string Reason, bool Return = false);

/// <summary>
/// Between matches: whether a tunnel to another relay should change the way into it before the next match - the same
/// question <see cref="DoorSwitchPolicy"/> answers for the tunnel carrying a match, asked of a tunnel that is not.
///
/// The policy judges thirty seconds of quarter seconds and only ever sees the tunnel the match is on, so a tunnel that
/// sits idle between the matches of its region is never measured by it. Before 2026-09-30 such a tunnel stayed on the
/// way it was opened on for the whole connection - a customer's Singapore tunnel held at a high ping all evening -
/// because the region plan measured it through itself, and every number it produced was that way's.
///
/// Every way, the one in use included, is measured the same way - a round of Probes from a socket of its own, which
/// relayd answers without moving the session - so nothing here compares a pong with a Probe. The bars are the
/// policy's: another way must be better by <see cref="DoorSwitchPolicy.Margin"/>, on the score (a way losing packets
/// counts <see cref="RelayLoss.PenaltyMs"/> slower); and the way the tunnel left - the relay's direct road, when the
/// tunnel is on an entry - is gone back to once it is faster by <see cref="DoorSwitchPolicy.ReturnMargin"/> and clean.
/// Pure: numbers in, a decision out.
/// </summary>
public static class WayCheck
{
    /// <summary>Probes per way. The burst's count, so 26% loss shows as lossy 95 times in 100.</summary>
    public const int Rounds = RelayLoss.BurstPings;

    /// <summary>Between rounds: sixteen spread over a second and a half, not bunched into one moment of one queue.</summary>
    public const int SpacingMs = 100;

    /// <summary>
    /// Probes a second, every way together, that a round may send. relayd answers 20 a second per session and drops the
    /// rest silently (maxProbesPerSecond), which read as loss: on the rig, two ways at ten rounds a second lost one and
    /// two Probes in sixteen and a clean road was scored as losing packets. Well under the cap, for a relay with four ways.
    /// </summary>
    public const int MaxProbesPerSecond = 12;

    /// <summary>The spacing between rounds for <paramref name="ways"/> ways: <see cref="SpacingMs"/>, or more to stay under <see cref="MaxProbesPerSecond"/>.</summary>
    public static int SpacingFor(int ways) => Math.Max(SpacingMs, (int)Math.Ceiling(1000.0 * ways / MaxProbesPerSecond));

    /// <param name="current">The way the tunnel is on.</param>
    /// <param name="left">The way the tunnel was put off - the direct road it did not start on - or null.</param>
    public static WayChoice Choose(string current, IReadOnlyList<WaySample> ways, string? left = null)
    {
        var here = ways.FirstOrDefault(w => string.Equals(w.Id, current, StringComparison.OrdinalIgnoreCase));
        if (here is null) return new WayChoice(null, $"{current} was not measured - staying");
        var others = ways.Where(w => !ReferenceEquals(w, here) && w.MedianMs is not null).ToList();
        if (others.Count == 0) return new WayChoice(null, $"{here} - no other way in answered, staying");

        var best = others.MinBy(w => w.Score)!;
        if (here.Score - best.Score >= DoorSwitchPolicy.Margin(best.MedianMs!.Value))
        {
            return new WayChoice(best.Id, $"{best} against {here} - better by {DoorSwitchPolicy.Margin(best.MedianMs.Value):F0} ms or more");
        }

        // Going back to the road the tunnel was put off: the lower bar, as the policy's return rule, and only clean.
        if (left is not null && !string.Equals(left, current, StringComparison.OrdinalIgnoreCase) &&
            others.FirstOrDefault(w => string.Equals(w.Id, left, StringComparison.OrdinalIgnoreCase)) is { } road &&
            !road.Loss.IsLossy && here.MedianMs is { } hereMs && road.MedianMs!.Value <= hereMs - DoorSwitchPolicy.ReturnMargin &&
            road.Score <= here.Score)
        {
            return new WayChoice(road.Id, $"{road}, the way left earlier, has recovered - {here} now", Return: true);
        }

        return new WayChoice(null, $"{here} - the best other, {best}, is not better by {DoorSwitchPolicy.Margin(best.MedianMs.Value):F0} ms");
    }
}
