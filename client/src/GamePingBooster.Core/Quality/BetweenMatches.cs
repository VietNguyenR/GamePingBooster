using GamePingBooster.Core.Profiles;

namespace GamePingBooster.Core.Quality;

/// <summary>
/// Tells the gap between two matches from the tunnel's count of game UDP packets, read on every pass of
/// the supervisor.
///
/// A match on the tunnel is a rate - a routed PUBG match sends 15 to 60 packets a second and its longest
/// silence on record is 858 ms. The lobby is TCP and sends no game UDP at all. So the gap is: a match was
/// seen, and then NOT ONE packet for <see cref="QuietFor"/>. Not "under the match rate": a result screen
/// still trickles to the match's server, and switching then would move a connection that is still open.
///
/// Ten seconds, not the thirty the match summary waits: on 2026-09-18 the next match's UDP began 36 to 80 s
/// after the last one stopped, and a rescan takes up to ten more. What decides the gap here is certainty
/// that no connection to a game server is open, and ten seconds of nothing at all is that.
///
/// Fires once per gap and not again until another match has been seen, so a player who sits in the lobby
/// for an hour is measured once. Pure logic: fed a clock and a counter, holds nothing else.
/// </summary>
public sealed class MatchGap
{
    /// <summary>Game UDP a second that means a match is on the tunnel, as in destination discovery.</summary>
    public const double MatchPacketsPerSecond = 10;

    public static readonly TimeSpan QuietFor = TimeSpan.FromSeconds(10);

    private long? _lastPackets;
    private long _lastAtMs;
    private bool _matchSeen;
    private long _quietSinceMs = -1;

    /// <summary>
    /// One reading: <paramref name="nowMs"/> from a monotonic clock and the live tunnel's game UDP count, or
    /// null with no tunnel. <paramref name="lastSentAtMs"/> is when the game last sent, on the same clock, when
    /// it is known: the gap is then timed from that packet rather than from the reading before the quiet, which
    /// can be five seconds late. True when this reading completes a gap.
    /// </summary>
    public bool Feed(long nowMs, long? packets, long? lastSentAtMs = null)
    {
        if (packets is not { } count)
        {
            _lastPackets = null;
            _quietSinceMs = -1;
            return false;
        }

        // The first reading, or a new tunnel whose count started again from zero: nothing to compare yet.
        if (_lastPackets is not { } last || count < last)
        {
            _lastPackets = count;
            _lastAtMs = nowMs;
            _quietSinceMs = -1;
            return false;
        }

        var previousAt = _lastAtMs;
        var seconds = (nowMs - previousAt) / 1000.0;
        _lastPackets = count;
        _lastAtMs = nowMs;
        if (seconds <= 0) return false;

        var sent = count - last;
        if (sent / seconds >= MatchPacketsPerSecond)
        {
            _matchSeen = true;
            _quietSinceMs = -1;
            return false;
        }
        if (sent > 0)
        {
            _quietSinceMs = -1;
            return false;
        }
        if (!_matchSeen) return false;

        // Nothing since the previous reading, so nothing since then at the latest - or, known exactly, since the
        // last packet, which cannot be later than that reading.
        if (_quietSinceMs < 0)
        {
            _quietSinceMs = lastSentAtMs is { } sentAt && sentAt <= previousAt ? sentAt : previousAt;
        }
        if (nowMs - _quietSinceMs < QuietFor.TotalMilliseconds) return false;

        _matchSeen = false;
        _quietSinceMs = -1;
        return true;
    }

    /// <summary>
    /// How long until the quiet under way completes a gap, when one is under way; null otherwise. Lets the
    /// caller read again at that moment instead of at its next regular pass.
    /// </summary>
    public long? DueInMs(long nowMs) =>
        _matchSeen && _quietSinceMs >= 0
            ? Math.Max(0, (long)QuietFor.TotalMilliseconds - (nowMs - _quietSinceMs))
            : null;
}

/// <summary>
/// How the paths measured between matches are compared: the median of <see cref="Samples"/> echoes to the
/// region's landmark, the same count for the path in use and for every candidate, taken minutes apart at most.
///
/// Median, not the best of three that connect uses: a best-of rewards the path that got lucky once, and a
/// move made on luck is a move back a match later. A path must answer <see cref="MinAnswered"/> of them to be
/// scored at all.
///
/// A candidate must beat the path in use by <see cref="RelayPaths.HelpMargin"/> - max(5 ms, 10%), the margin
/// connect uses to call a relay worth it. Anything less is inside what an evening varies by, and the move
/// costs the lobby a reconnect.
/// </summary>
public static class RescanScore
{
    public const int Samples = 8;
    public const int MinAnswered = 6;

    /// <summary>The median of the answered samples, or null when fewer than <see cref="MinAnswered"/> answered.</summary>
    public static double? Median(IReadOnlyList<double?> samples)
    {
        var answered = samples.OfType<double>().ToList();
        if (answered.Count < MinAnswered) return null;
        return SpikeDetector.Percentile(answered, 50);
    }

    public static bool WorthMoving(double currentMs, double candidateMs) =>
        currentMs - candidateMs >= RelayPaths.HelpMargin(currentMs);
}
