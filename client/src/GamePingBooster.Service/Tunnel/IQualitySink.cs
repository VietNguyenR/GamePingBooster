namespace GamePingBooster.Service.Tunnel;

/// <summary>
/// Where <see cref="TunnelClient"/> hands the answers to the spike recorder's probes.
///
/// Both methods are called on the DOWNLINK THREAD, the one carrying game packets into the adapter,
/// so an implementation must be quick, must never block on anything slower than an uncontended
/// lock, and must never throw.
/// </summary>
internal interface IQualitySink
{
    /// <summary>A pong from relayd: <paramref name="rttMs"/> round trip, arrived at <paramref name="receivedAt"/> (Stopwatch timestamp).</summary>
    void OnPong(double rttMs, long receivedAt);

    /// <summary>An answer to one of the recorder's echoes: a reply, or a time-exceeded from a router on the way.</summary>
    void OnEcho(ushort sequence, bool timeExceeded, long receivedAt);
}

/// <summary>
/// The timing of the game's own packets since the last read, in both directions.
///
/// Packets are counted and the longest silence between two of them is kept - nothing about their
/// content, and no per-packet record. The last-seen timestamps are Stopwatch timestamps, 0 when
/// nothing has been seen on this tunnel yet.
/// </summary>
internal readonly record struct Cadence(
    long UpPackets, double UpGapMs, long LastUpAt,
    long DownPackets, double DownGapMs, long LastDownAt);
