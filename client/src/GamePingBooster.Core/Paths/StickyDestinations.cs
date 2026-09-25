namespace GamePingBooster.Core.Paths;

/// <summary>
/// Keeps every destination that is in use on the tunnel it started on. See docs/MULTI-TUNNEL.md, 5.3.
///
/// A game server learns the player's address from the first packet of a flow and holds the match to it.
/// The plan - which tunnel each region leaves by - may change at any moment: a pass measured something
/// better, or a tunnel died. If a change reached a server already talking, that server would see a new
/// source mid-match and drop the player. So the plan decides only for destinations nobody is talking to;
/// one that has carried a packet in either direction within <see cref="StickyFor"/> keeps its tunnel.
///
/// This is the rule that makes remapping safe WITHOUT a timing guess about when a region is quiet. Naraka
/// is the case it was written for: the game trickles one packet a second to a server from the lobby and
/// later plays a whole match on it (tools/profile-builder/games.json). Whatever carried the trickle carries
/// the match.
///
/// A destination moves in one way only: <see cref="Release"/>, when its tunnel is gone and the exit it was
/// stuck to no longer exists anyway.
///
/// Thread-safe. The uplink thread resolves every packet, each downlink thread touches its replies, the
/// supervisor releases and sweeps; a game sends a hundred or two packets a second, so one uncontended lock
/// costs nothing that matters. Time is passed in, in milliseconds, so a test can drive it.
/// </summary>
public sealed class StickyDestinations<TTunnel> where TTunnel : class
{
    /// <summary>How long a destination stays with its tunnel after its last packet in either direction.</summary>
    public static readonly TimeSpan StickyFor = TimeSpan.FromSeconds(120);

    private readonly long _stickyForMs;
    private readonly object _gate = new();
    private readonly Dictionary<uint, Entry> _entries = [];

    private sealed class Entry(TTunnel tunnel, long lastMs)
    {
        public TTunnel Tunnel { get; } = tunnel;
        public long LastMs { get; set; } = lastMs;
    }

    public StickyDestinations() : this(StickyFor) { }

    public StickyDestinations(TimeSpan stickyFor) => _stickyForMs = (long)stickyFor.TotalMilliseconds;

    /// <summary>
    /// The tunnel a packet to <paramref name="destination"/> must take: the one it is stuck to, else the
    /// plan's answer, to which it is then stuck. <paramref name="planned"/> runs only when the destination
    /// is not stuck; a null answer means "not through a tunnel", and nothing is stuck.
    /// </summary>
    public TTunnel? Resolve(uint destination, long nowMs, Func<TTunnel?> planned)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(destination, out var entry))
            {
                if (nowMs - entry.LastMs <= _stickyForMs)
                {
                    entry.LastMs = nowMs;
                    return entry.Tunnel;
                }
                _entries.Remove(destination);
            }

            var tunnel = planned();
            if (tunnel is not null) _entries[destination] = new Entry(tunnel, nowMs);
            return tunnel;
        }
    }

    /// <summary>
    /// <see cref="Resolve(uint, long, Func{TTunnel})"/> without a closure: the uplink calls this for every packet,
    /// and a lambda capturing the destination would allocate each time.
    /// </summary>
    public TTunnel? Resolve<TState>(uint destination, long nowMs, TState state, Func<TState, uint, TTunnel?> planned)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(destination, out var entry))
            {
                if (nowMs - entry.LastMs <= _stickyForMs)
                {
                    entry.LastMs = nowMs;
                    return entry.Tunnel;
                }
                _entries.Remove(destination);
            }

            var tunnel = planned(state, destination);
            if (tunnel is not null) _entries[destination] = new Entry(tunnel, nowMs);
            return tunnel;
        }
    }

    /// <summary>
    /// Moves every destination stuck to <paramref name="from"/> onto <paramref name="to"/>, keeping when each was
    /// last used. For the home tunnel being replaced by a reconnect: what was on home stays on home, whichever
    /// tunnel home now is - exactly what a reconnect has always done with every destination. Returns how many.
    /// </summary>
    public int Retarget(TTunnel from, TTunnel to)
    {
        lock (_gate)
        {
            var moved = _entries.Where(e => ReferenceEquals(e.Value.Tunnel, from)).ToList();
            foreach (var (key, entry) in moved) _entries[key] = new Entry(to, entry.LastMs);
            return moved.Count;
        }
    }

    /// <summary>
    /// A packet FROM <paramref name="source"/> came back through <paramref name="tunnel"/>. Keeps a flow
    /// that is quiet one way stuck, and never sticks anything new: only the uplink decides where a
    /// destination goes.
    /// </summary>
    public void Touch(uint source, TTunnel tunnel, long nowMs)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(source, out var entry) && ReferenceEquals(entry.Tunnel, tunnel) &&
                nowMs - entry.LastMs <= _stickyForMs)
            {
                entry.LastMs = nowMs;
            }
        }
    }

    /// <summary>The tunnel <paramref name="destination"/> is stuck to right now, or null.</summary>
    public TTunnel? StuckTo(uint destination, long nowMs)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(destination, out var entry) && nowMs - entry.LastMs <= _stickyForMs
                ? entry.Tunnel
                : null;
        }
    }

    /// <summary>How many destinations are stuck to <paramref name="tunnel"/>. A tunnel with any is not closed.</summary>
    public int CountStuckTo(TTunnel tunnel, long nowMs)
    {
        lock (_gate)
        {
            return _entries.Values.Count(e => ReferenceEquals(e.Tunnel, tunnel) && nowMs - e.LastMs <= _stickyForMs);
        }
    }

    /// <summary>Every destination stuck right now that <paramref name="match"/> selects - a region's, for its pins.</summary>
    public List<uint> Stuck(Func<uint, bool> match, long nowMs)
    {
        lock (_gate)
        {
            return [.. _entries.Where(e => nowMs - e.Value.LastMs <= _stickyForMs && match(e.Key)).Select(e => e.Key)];
        }
    }

    /// <summary>Every destination stuck right now, with its tunnel.</summary>
    public List<(uint Destination, TTunnel Tunnel)> Snapshot(long nowMs)
    {
        lock (_gate)
        {
            return [.. _entries.Where(e => nowMs - e.Value.LastMs <= _stickyForMs).Select(e => (e.Key, e.Value.Tunnel))];
        }
    }

    /// <summary>Forgets every destination stuck to a tunnel that is gone. Returns how many.</summary>
    public int Release(TTunnel tunnel)
    {
        lock (_gate)
        {
            var gone = _entries.Where(e => ReferenceEquals(e.Value.Tunnel, tunnel)).Select(e => e.Key).ToList();
            foreach (var key in gone) _entries.Remove(key);
            return gone.Count;
        }
    }

    /// <summary>Drops what has expired, so the table does not grow with every server a long evening met.</summary>
    public int Sweep(long nowMs)
    {
        lock (_gate)
        {
            var expired = _entries.Where(e => nowMs - e.Value.LastMs > _stickyForMs).Select(e => e.Key).ToList();
            foreach (var key in expired) _entries.Remove(key);
            return expired.Count;
        }
    }
}
