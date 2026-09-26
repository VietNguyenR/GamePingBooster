namespace GamePingBooster.Core.Paths;

/// <summary>
/// Which tunnel is carrying the match - the "active tunnel" of docs/MULTI-TUNNEL.md 5.8, which the in-game
/// ping, the spike recorder, the match summary and the app's status follow.
///
/// Fed about once a second with every open tunnel's count of game UDP packets (never cleared, so a reader takes
/// differences). A tunnel carrying at least <see cref="MatchPacketsPerSecond"/> is carrying a match; the lobby is
/// TCP, and the one-a-second trickles a game keeps to a server it may play on later stay under it.
///
/// Three rules, each against a way it would read the wrong tunnel:
///
///   - <b>Silence never moves it.</b> A stall on the tunnel carrying the match is exactly what the spike recorder
///     exists to see; if the answer went back to home the moment the game's packets stopped, the recorder would
///     be measuring home through the one stall worth recording. The carrier leaves only for another tunnel
///     that is carrying more, or for home once it has carried nothing for <see cref="HomeAfter"/> - past the
///     recorder's 30 s end of a match, so its summary is written against the tunnel that carried it.
///   - <b>A challenger has to hold.</b> Another tunnel takes over only after carrying a match's rate for
///     <see cref="TakeOverAfter"/> without a break, and only while it is carrying more than the carrier. A burst
///     from the lobby, or a second game server touched once, is not a match.
///   - <b>A tunnel that is gone is not the carrier.</b> One no longer in the list - closed, died, replaced by a
///     reconnect - hands over to home at once.
///
/// Time is passed in, in milliseconds, so a test can drive it. Update from one thread; <see cref="Current"/> may be
/// read from any.
/// </summary>
public sealed class MatchCarrier<TTunnel> where TTunnel : class
{
    /// <summary>Game packets a second, into one tunnel, that make it the one carrying a match. Matches run 9-150.</summary>
    public const double MatchPacketsPerSecond = 5;

    /// <summary>How long another tunnel must carry a match's rate before it takes over.</summary>
    public static readonly TimeSpan TakeOverAfter = TimeSpan.FromSeconds(3);

    /// <summary>How long the carrier, when it is not home, may carry nothing before home takes it back.</summary>
    public static readonly TimeSpan HomeAfter = TimeSpan.FromSeconds(60);

    private readonly object _gate = new();
    private readonly Dictionary<TTunnel, Reading> _readings = new(ReferenceEqualityComparer.Instance);
    private volatile TTunnel? _current;
    private long _currentBusyMs;

    private sealed class Reading
    {
        public long Packets;
        public long AtMs;
        public double Rate;

        /// <summary>When this tunnel started carrying a match's rate without a break, or -1 while it is not.</summary>
        public long BusySinceMs = -1;
    }

    /// <summary>The tunnel carrying the match, or null before the first <see cref="Update"/>.</summary>
    public TTunnel? Current => _current;

    /// <summary>
    /// One reading of every open tunnel: <paramref name="home"/>, and <paramref name="others"/> with each one's
    /// game UDP count. Returns the carrier - home unless another tunnel carries the match by the rules above.
    /// </summary>
    public TTunnel Update(long nowMs, TTunnel home, long homePackets, IReadOnlyList<(TTunnel Tunnel, long Packets)> others)
    {
        lock (_gate)
        {
            var seen = new HashSet<TTunnel>(ReferenceEqualityComparer.Instance) { home };
            Read(home, homePackets, nowMs);
            foreach (var (tunnel, packets) in others)
            {
                if (!seen.Add(tunnel)) continue;
                Read(tunnel, packets, nowMs);
            }
            foreach (var gone in _readings.Keys.Where(t => !seen.Contains(t)).ToList()) _readings.Remove(gone);

            var current = _current;
            if (current is null || !seen.Contains(current))
            {
                current = home;
                _currentBusyMs = nowMs;
            }

            var carrying = _readings[current];
            if (carrying.Rate >= MatchPacketsPerSecond) _currentBusyMs = nowMs;

            TTunnel? challenger = null;
            double challengerRate = 0;
            foreach (var (tunnel, reading) in _readings)
            {
                if (ReferenceEquals(tunnel, current) || reading.BusySinceMs < 0) continue;
                if (nowMs - reading.BusySinceMs < TakeOverAfter.TotalMilliseconds) continue;
                if (reading.Rate <= carrying.Rate || reading.Rate <= challengerRate) continue;
                challenger = tunnel;
                challengerRate = reading.Rate;
            }

            if (challenger is not null)
            {
                current = challenger;
                _currentBusyMs = nowMs;
            }
            else if (!ReferenceEquals(current, home) && nowMs - _currentBusyMs >= HomeAfter.TotalMilliseconds)
            {
                current = home;
                _currentBusyMs = nowMs;
            }

            _current = current;
            return current;
        }
    }

    private void Read(TTunnel tunnel, long packets, long nowMs)
    {
        if (!_readings.TryGetValue(tunnel, out var reading))
        {
            // First sight: no rate yet. A tunnel already carrying a match is known one reading later.
            _readings[tunnel] = new Reading { Packets = packets, AtMs = nowMs };
            return;
        }

        var elapsed = nowMs - reading.AtMs;
        if (elapsed <= 0) return;
        reading.Rate = Math.Max(0, packets - reading.Packets) * 1000.0 / elapsed;
        reading.Packets = packets;
        reading.AtMs = nowMs;
        if (reading.Rate < MatchPacketsPerSecond) reading.BusySinceMs = -1;
        else if (reading.BusySinceMs < 0) reading.BusySinceMs = nowMs - elapsed;
    }
}
