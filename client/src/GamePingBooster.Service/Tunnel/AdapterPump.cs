using GamePingBooster.Core.Protocol;
using GamePingBooster.Service.Native;

namespace GamePingBooster.Service.Tunnel;

/// <summary>
/// The one reader of the virtual adapter's ring: takes every packet Windows routed into the tunnel and
/// hands it to the tunnel that carries it. See docs/MULTI-TUNNEL.md, section 5.1.
///
/// It was <c>TunnelClient.UplinkLoop</c>, one per tunnel. It moved out because a ring has one reader, and
/// with several tunnels something above them has to decide which one each packet takes. Phase A of
/// multi-tunnel ships this with ONE target - the home tunnel - and changes nothing a player can see; the
/// tunnel still does everything it did per packet (<see cref="TunnelClient.SendInner"/>).
///
/// LIVES AS LONG AS THE ADAPTER SESSION, not as long as a tunnel. A reconnect, a failover and a move between
/// matches replace the tunnel under it: the engine sets the target to null first, then disposes the old
/// tunnel, then sets the new one. While there is no target the ring is NOT read, so packets Windows sends
/// in that half second wait in the adapter and leave through the new relay - the behaviour
/// RescanBetweenMatchesAsync depends on, preserved exactly: the old per-tunnel uplink thread stopped
/// reading when its tunnel was disposed and the next tunnel's thread picked the ring up.
///
/// MUST be disposed before the adapter's session ends. It calls into the ring on every packet, and
/// WintunEndSession while it is inside a call is a use-after-free that takes the LocalSystem service down.
/// </summary>
internal sealed class AdapterPump : IDisposable
{
    private readonly IPacketDevice _device;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly ManualResetEventSlim _hasTarget = new(false);
    private Thread? _thread;
    private volatile TunnelClient? _home;

    /// <summary>Packets read while no tunnel was there to take them - a swap racing the read. Diagnostic.</summary>
    private long _droppedNoTarget;

    // Unexpected failures of a send are logged, but not every one of a burst. Pump thread only.
    private long _lastErrorLogTick = long.MinValue;
    private long _errorsSinceLog;

    public AdapterPump(IPacketDevice device, Action<string> log)
    {
        _device = device;
        _log = log;
    }

    /// <summary>Packets read while no tunnel was there to take them.</summary>
    public long DroppedNoTarget => Interlocked.Read(ref _droppedNoTarget);

    /// <summary>The tunnel every packet goes to now, or null to stop reading the ring until there is one.</summary>
    public TunnelClient? Home => _home;

    /// <summary>
    /// Points the pump at <paramref name="tunnel"/>, or pauses it with null. Pausing does not wait for a packet
    /// already read: the caller disposes the old tunnel next, and a packet caught in that race is lost exactly
    /// as it was when the old uplink thread was stopped under it.
    /// </summary>
    public void SetHome(TunnelClient? tunnel)
    {
        _home = tunnel;
        if (tunnel is null) _hasTarget.Reset();
        else _hasTarget.Set();
    }

    public void Start()
    {
        if (_thread is not null) throw new InvalidOperationException("The pump is already running.");

        // A dedicated thread, not the thread pool, at the priority the old uplink thread had: game packets
        // are latency sensitive, and the pool adds milliseconds exactly when a game loads the machine.
        _thread = new Thread(() => Run(_cts.Token))
        {
            Name = "gpb-uplink",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
    }

    private void Run(CancellationToken ct)
    {
        var packet = new byte[GpbProtocol.MaxPacketLen];

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // No target: leave the ring alone, so what Windows sends waits for the next tunnel.
                if (_home is null)
                {
                    _hasTarget.Wait(250, ct);
                    continue;
                }

                var len = _device.ReceivePacket(packet);
                if (len < 0)
                {
                    // Ring is empty - sleep until the driver signals a packet, do not spin.
                    _device.WaitForPacket(250);
                    continue;
                }

                // Read again: a swap may have cleared it while the packet was being read.
                var target = _home;
                if (target is null)
                {
                    Interlocked.Increment(ref _droppedNoTarget);
                    continue;
                }

                if (len == 0)
                {
                    // Did not fit the buffer, which can only happen if the adapter MTU has been raised past
                    // MaxPacketLen. Counted on the tunnel, where it always was.
                    target.CountUplinkOversize();
                    continue;
                }

                SendOn(target, packet.AsSpan(0, len));
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // The adapter itself failing - its session gone from under us. As before, that ends the uplink:
                // carrying on would spin on a ring that is not there.
                if (ct.IsCancellationRequested) return;
                _log($"Uplink thread error: {ex.Message}");
                return;
            }
        }
    }

    /// <summary>
    /// One packet to one tunnel. The tunnel handles every socket failure it knows (see SendInner); anything
    /// else is a fault of this code, and it costs the packet, never the uplink.
    ///
    /// That last part is a deliberate difference from the old per-tunnel loop, which ended on any unexpected
    /// exception - leaving a tunnel that answered keepalives and carried nothing up, which the supervisor
    /// cannot see. One lost packet and a log line is the better failure.
    /// </summary>
    private void SendOn(TunnelClient target, ReadOnlySpan<byte> packet)
    {
        try
        {
            target.SendInner(packet);
        }
        catch (Exception ex)
        {
            target.CountUplinkSendFailed();
            _errorsSinceLog++;
            var now = Environment.TickCount64;
            if (_lastErrorLogTick != long.MinValue && now - _lastErrorLogTick < 30_000) return;
            _log($"Uplink: a packet could not be sent ({ex.GetType().Name}: {ex.Message}) - {_errorsSinceLog} since " +
                 "the last line like this. The uplink carries on.");
            _lastErrorLogTick = now;
            _errorsSinceLog = 0;
        }
    }

    public void Dispose()
    {
        _home = null;
        _cts.Cancel();
        _hasTarget.Set();

        // Must be gone before the caller ends the adapter session - see the class summary. Neither wait can
        // block for long: the ring wait is 250 ms and the target wait wakes on the cancel above.
        var stopped = _thread?.Join(TimeSpan.FromSeconds(2)) ?? true;
        if (!stopped)
        {
            _log("WARNING: the uplink thread did not stop within 2s. The adapter is about to be released while " +
                 "it may still be in use - if the service dies right after this line, that is why.");
        }

        var lost = DroppedNoTarget;
        if (lost > 0) _log($"Uplink: {lost} packet(s) were read during a tunnel swap and had nowhere to go.");

        _cts.Dispose();
        _hasTarget.Dispose();
    }
}
