using System.Buffers.Binary;
using System.Diagnostics;
using GamePingBooster.Core.Net;
using GamePingBooster.Core.Paths;

namespace GamePingBooster.Service.Tunnel;

/// <summary>
/// Which tunnel each packet leaves by, once a game's regions can leave by different ones - docs/MULTI-TUNNEL.md
/// 5.1-5.3 and 5.9. The <see cref="AdapterPump"/> asks it for every packet it reads; every tunnel's downlink
/// hands it every packet before Windows sees it.
///
/// Uplink, per packet: the destination is looked up in the sticky table first - a destination in use keeps its
/// tunnel whatever the plan says now (G1) - then in the region table, then the plan; anything the plan does not
/// place goes home. A packet leaving by a tunnel whose relay handed out a different inner address from the
/// adapter's has its source rewritten to that tunnel's (<see cref="InnerNat"/>); the relay drops anything else.
///
/// Downlink, per packet: the source refreshes the sticky entry, so a flow quiet one way stays stuck, and the
/// destination is rewritten back to the adapter's address.
///
/// THE ADAPTER'S ADDRESS IS FIXED for the dispatcher's life (5.2). A home reconnect no longer re-addresses the
/// adapter while one exists: the new home rewrites instead, so a game socket bound to the address keeps working
/// on the tunnels that did not change.
///
/// Changing the plan is swapping one array reference; nothing on the packet path waits for it. A fault inside
/// <see cref="Route"/> collapses the dispatcher (5.9): nothing new is sent off home from then on, destinations
/// already stuck keep their tunnel, and the engine closes the rest - a bug in this code costs its benefit, not
/// the player's match.
/// </summary>
internal sealed class PathDispatcher
{
    private readonly RegionTable _table;
    private readonly StickyDestinations<TunnelClient> _sticky = new();
    private volatile TunnelClient?[] _byRegion;
    private volatile TunnelClient? _home;
    private string? _faultReason;

    private long _rewrittenUp;
    private long _rewrittenDown;
    private long _malformedUp;
    private long _malformedDown;

    /// <summary>For TunnelCheck only: runs inside <see cref="Route"/>, so a fault can be injected where one would happen.</summary>
    internal Action? TestFault { get; set; }

    public PathDispatcher(uint adapterIp, RegionTable table, TunnelClient home)
    {
        if (!table.Usable) throw new ArgumentException("The region table overlaps; the game must stay on one tunnel.", nameof(table));
        AdapterIp = adapterIp;
        _table = table;
        _byRegion = new TunnelClient?[table.RegionCount];
        _home = home;
    }

    /// <summary>The virtual adapter's address, host order. Fixed - see the class summary.</summary>
    public uint AdapterIp { get; }

    public RegionTable Table => _table;

    /// <summary>Set once a fault collapsed the dispatcher; the reason, for the log and the quality record.</summary>
    public string? FaultReason => Volatile.Read(ref _faultReason);

    public long RewrittenUp => Interlocked.Read(ref _rewrittenUp);
    public long RewrittenDown => Interlocked.Read(ref _rewrittenDown);
    public long Malformed => Interlocked.Read(ref _malformedUp) + Interlocked.Read(ref _malformedDown);

    /// <summary>The home tunnel, or null while a reconnect has none.</summary>
    public TunnelClient? Home => _home;

    /// <summary>
    /// A reconnect put <paramref name="home"/> in place of the old home. What was stuck to the old one moves to it:
    /// home is home, whichever relay it is - what a reconnect always did with every destination.
    /// </summary>
    public void ReplaceHome(TunnelClient home)
    {
        var old = _home;
        _home = home;
        if (old is not null && !ReferenceEquals(old, home)) _sticky.Retarget(old, home);
    }

    /// <summary>
    /// The home tunnel is gone and there is no new one yet. What was stuck to it is released - its routes are out,
    /// so those destinations go over the player's own line until the reconnect puts them back - and packets the
    /// pump still reads for home are dropped rather than sent on a disposed tunnel.
    /// </summary>
    public void LoseHome()
    {
        var old = _home;
        _home = null;
        if (old is not null) _sticky.Release(old);
    }

    /// <summary>
    /// The plan: for each region of <see cref="Table"/>, by index, the tunnel it leaves by, or null for home.
    /// Applies only to destinations not already in use. Ignored once collapsed.
    /// </summary>
    public void SetPlan(TunnelClient?[] byRegion)
    {
        if (byRegion.Length != _table.RegionCount) throw new ArgumentException("One entry per region.", nameof(byRegion));
        if (FaultReason is not null) return;
        _byRegion = (TunnelClient?[])byRegion.Clone();
    }

    /// <summary>The plan in force, region by region; null means home.</summary>
    public TunnelClient?[] Plan => (TunnelClient?[])_byRegion.Clone();

    /// <summary>
    /// A tunnel that died or is being closed: its regions go home and its stuck destinations are released - the
    /// only way a stuck destination moves, and only when the exit it was stuck to is gone. Returns how many.
    /// </summary>
    public int Remove(TunnelClient tunnel)
    {
        var plan = _byRegion;
        if (plan.Any(t => ReferenceEquals(t, tunnel)))
        {
            _byRegion = [.. plan.Select(t => ReferenceEquals(t, tunnel) ? null : t)];
        }
        return _sticky.Release(tunnel);
    }

    public int CountStuckTo(TunnelClient tunnel) => _sticky.CountStuckTo(tunnel, NowMs());

    /// <summary>Every destination stuck to a tunnel other than home right now, host order.</summary>
    public List<uint> StuckOffHome()
    {
        var home = _home;
        return [.. _sticky.Snapshot(NowMs()).Where(e => !ReferenceEquals(e.Tunnel, home)).Select(e => e.Destination)];
    }

    public int Sweep() => _sticky.Sweep(NowMs());

    /// <summary>
    /// Uplink: the tunnel <paramref name="packet"/> leaves by, its source rewritten when that tunnel needs it, or
    /// null to drop it - malformed where a rewrite was due, or bound for home while there is no home.
    /// </summary>
    public TunnelClient? Route(Span<byte> packet)
    {
        var home = _home;

        // Not IPv4, or too short to name a destination: home's filter has always dealt with these.
        if (packet.Length < 20 || packet[0] >> 4 != 4) return home;

        TestFault?.Invoke();

        var destination = BinaryPrimitives.ReadUInt32BigEndian(packet[16..]);
        var now = NowMs();
        var target = FaultReason is null
            ? _sticky.Resolve(destination, now, this, static (self, d) => self.Planned(d))
            : _sticky.StuckTo(destination, now) ?? home;
        if (target is null) return null;

        var inner = target.InnerIp;
        if (inner == AdapterIp || inner == 0) return target;
        switch (InnerNat.RewriteSource(packet, AdapterIp, inner))
        {
            case InnerNat.Outcome.Rewritten:
                Interlocked.Increment(ref _rewrittenUp);
                return target;
            case InnerNat.Outcome.NotOurs:
                // Not from the adapter's address: nothing Windows sent through this adapter. Unchanged, as ever.
                return target;
            default:
                Interlocked.Increment(ref _malformedUp);
                return null;
        }
    }

    private TunnelClient? Planned(uint destination)
    {
        var region = _table.Find(destination);
        return region < 0 ? _home : _byRegion[region] ?? _home;
    }

    /// <summary>
    /// Downlink, from <paramref name="from"/>'s own thread: keeps the flow stuck and gives the packet the adapter's
    /// address. False to drop it - malformed where a rewrite was due.
    /// </summary>
    public bool OnDownlink(TunnelClient from, Span<byte> packet)
    {
        if (packet.Length < 20 || packet[0] >> 4 != 4) return true;
        _sticky.Touch(BinaryPrimitives.ReadUInt32BigEndian(packet[12..]), from, NowMs());

        var inner = from.InnerIp;
        if (inner == AdapterIp || inner == 0) return true;
        switch (InnerNat.RewriteDestination(packet, inner, AdapterIp))
        {
            case InnerNat.Outcome.Rewritten:
                Interlocked.Increment(ref _rewrittenDown);
                return true;
            case InnerNat.Outcome.NotOurs:
                return true;
            default:
                Interlocked.Increment(ref _malformedDown);
                return false;
        }
    }

    /// <summary>
    /// Called by the pump when <see cref="Route"/> threw: collapses the dispatcher. The first reason is kept.
    /// </summary>
    public void Fault(Exception ex)
    {
        Interlocked.CompareExchange(ref _faultReason, $"{ex.GetType().Name}: {ex.Message}", null);
        _byRegion = new TunnelClient?[_table.RegionCount];
    }

    private static long NowMs() => Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency;
}
