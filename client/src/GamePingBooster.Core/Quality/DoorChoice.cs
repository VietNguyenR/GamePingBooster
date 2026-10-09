namespace GamePingBooster.Core.Quality;

/// <summary>
/// One way into a relay as the relay list measured it: relayd's round trip down that way, the median of the last
/// few seconds, and how many of its probes went unanswered. <see cref="Direct"/> is the relay's own address, not an entry.
/// </summary>
public sealed record DoorReading(string DoorId, bool Direct, double? MedianMs, PingLoss Loss)
{
    /// <summary>What the rule ranks on: the median, plus the loss penalty. Null when nothing answered.</summary>
    public double? Score => MedianMs is { } ms ? RelayLoss.Score(ms, Loss) : null;

    public override string ToString() => MedianMs is { } ms ? $"{DoorId} {ms:F0} ms{RelayLoss.Note(Loss)}" : $"{DoorId} no answer";
}

/// <summary>What <see cref="DoorChoice.Pick"/> decided: the way to use, or null when none answered, and the line for the log.</summary>
public sealed record DoorPick(DoorReading? Door, string Reason);

/// <summary>
/// Which way into ONE relay to use - its own address or an entry in front of it - by the round trips the relay list
/// measured with measurement tickets. One rule for both places that show or take a way: the list shows the number of
/// the way this picks, and a connect to a relay chosen there starts on that same way. Until 2026-10-09 the two were
/// separate: the list was an ICMP echo to the relay's own address, a connect compared entries only sometimes, by
/// another margin, and a player saw 60 ms in the list and got 49 - or 43 through an entry nothing had shown him.
///
/// Comparing the first leg alone is fair between ways into one relay: they all end at the same relayd, and the leg on
/// to the game is the same whichever way the packets came in (see TunnelEngine.ChooseDoorAsync).
///
/// The margin is the steady rule's, <see cref="DoorSwitchPolicy.SteadyMargin"/> - max(5 ms, 10%) on medians - because
/// what is compared is the same kind of number the steady rule compares in a match: a median over seconds, not a
/// best-of-three. The 10 ms "worse" bar is for a lag in progress and kept players 6-9 ms slower than an entry beside
/// them (pubg-relay-selection-gaps, 2026-10-07). Ties go to the relay's own address: an entry is a detour, and one
/// more box that can fail.
///
/// Pure: numbers in, a decision out.
/// </summary>
public static class DoorChoice
{
    public static DoorPick Pick(IReadOnlyList<DoorReading> doors)
    {
        var answered = doors.Where(d => d.Score is not null).ToList();
        if (answered.Count == 0) return new DoorPick(null, "no way in answered");

        var fastest = answered.MinBy(d => d.Score!.Value)!;
        var direct = answered.FirstOrDefault(d => d.Direct);
        if (direct is null)
        {
            return new DoorPick(fastest, fastest.Direct ? $"{fastest}" : $"{fastest} - the relay's own address did not answer");
        }
        if (ReferenceEquals(fastest, direct)) return new DoorPick(direct, $"{direct} - no entry is faster");

        var margin = DoorSwitchPolicy.SteadyMargin(fastest.Score!.Value);
        if (direct.Score!.Value - fastest.Score.Value < margin)
        {
            return new DoorPick(direct, $"{direct} - {fastest} is not faster by {margin:F0} ms or more");
        }
        return new DoorPick(fastest, $"{fastest} rather than {direct}");
    }
}
