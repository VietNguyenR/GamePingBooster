namespace GamePingBooster.Core.Quality;

/// <summary>
/// When a tunnel kept open after a move between matches is let go - TunnelEngine.HeldTunnel.cs.
///
/// The move used to close every TCP connection that rode the old relay, and on 2026-10-19 14:35 (owner, FPT) that cut
/// the game's Steam CM connection while the lobby was still loading: the lobby never came, the player saw smoke. So the
/// old tunnel now stays open for those connections and only what opens afterwards goes to the new relay. It cannot stay
/// for ever - a relay slot and a keepalive a second, and Steam's heartbeats keep its connection in use for hours - so it
/// is let go at the first of:
///
///   - the next match has run on the new relay for <see cref="MatchFor"/>: the lobby loaded long ago, and what is cut
///     now is opened again in the background while the player is playing;
///   - <see cref="AtMost"/> after the move with no such match: a player standing in the lobby, which loaded long ago too.
///
/// (The engine also lets it go the moment nothing on it is in use, and when its relay goes silent - those are the other
/// tunnels' rules.) A match is the rate MatchGap uses, <see cref="MatchGap.MatchPacketsPerSecond"/>, read between two
/// passes of the supervisor. Pure logic: fed a clock and the new home's count of game UDP packets.
/// </summary>
public sealed class HeldTunnelClock
{
    public static readonly TimeSpan MatchFor = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan AtMost = TimeSpan.FromMinutes(10);

    private readonly long _heldAtMs;
    private long? _lastPackets;
    private long _lastAtMs;
    private long _matchSinceMs = -1;

    public HeldTunnelClock(long heldAtMs) => _heldAtMs = heldAtMs;

    public long HeldAtMs => _heldAtMs;

    /// <summary>
    /// One reading: <paramref name="nowMs"/> on the clock the tunnel was held on, and the home tunnel's game UDP count,
    /// or null while there is no home. Returns why the held tunnel goes now - "match" or "time" - or null to keep it.
    /// </summary>
    public string? Feed(long nowMs, long? homePackets)
    {
        if (homePackets is { } count)
        {
            // A new home after a reconnect counts from zero again: nothing to compare yet.
            if (_lastPackets is not { } last || count < last)
            {
                _matchSinceMs = -1;
            }
            else if (nowMs > _lastAtMs)
            {
                var rate = (count - last) / ((nowMs - _lastAtMs) / 1000.0);
                if (rate >= MatchGap.MatchPacketsPerSecond)
                {
                    if (_matchSinceMs < 0) _matchSinceMs = _lastAtMs;
                    if (nowMs - _matchSinceMs >= MatchFor.TotalMilliseconds) return "match";
                }
                else
                {
                    _matchSinceMs = -1;
                }
            }
            _lastPackets = count;
            _lastAtMs = nowMs;
        }
        else
        {
            _lastPackets = null;
            _matchSinceMs = -1;
        }

        return nowMs - _heldAtMs >= AtMost.TotalMilliseconds ? "time" : null;
    }
}
