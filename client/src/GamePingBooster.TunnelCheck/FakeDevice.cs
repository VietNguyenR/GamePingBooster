using System.Collections.Concurrent;
using System.Diagnostics;
using GamePingBooster.Service.Native;

namespace GamePingBooster.TunnelCheck;

/// <summary>
/// The virtual adapter, in memory: what "Windows" sends waits in a queue for the one reader, and what the
/// tunnels hand to Windows is kept with the time it arrived.
///
/// It also keeps Wintun's one hard rule: nothing may be inside the ring once the session has ended. A call
/// that STARTS after <see cref="EndSession"/>, one still IN PROGRESS when it runs, and one that RETURNS after
/// it are all counted in <see cref="CallsAfterEnd"/> - on real hardware each is a use-after-free in a
/// LocalSystem service (WaitForPacket waits on the session's own event handle, which EndSession closes).
/// </summary>
internal sealed class FakeDevice : IPacketDevice
{
    private readonly ConcurrentQueue<(byte[] Packet, long At)> _fromWindows = new();
    private readonly SemaphoreSlim _signal = new(0);
    private volatile bool _ended;
    private long _callsAfterEnd;
    private long _inside;

    /// <summary>Packets the tunnels handed to Windows, with when.</summary>
    public ConcurrentQueue<(byte[] Packet, long At)> ToWindows { get; } = new();

    /// <summary>When set, SendPacket reports the ring full and drops, as Wintun does when Windows falls behind.</summary>
    public volatile bool RingFull;

    public long CallsAfterEnd => Interlocked.Read(ref _callsAfterEnd);
    public int Waiting => _fromWindows.Count;

    /// <summary>"Windows" routes a packet into the adapter.</summary>
    public void FromWindows(byte[] packet)
    {
        _fromWindows.Enqueue((packet, Stopwatch.GetTimestamp()));
        _signal.Release();
    }

    public int ReceivePacket(Span<byte> destination)
    {
        Enter();
        try
        {
            if (!_fromWindows.TryDequeue(out var item)) return -1;
            if (item.Packet.Length > destination.Length) return 0;
            item.Packet.CopyTo(destination);
            return item.Packet.Length;
        }
        finally
        {
            Leave();
        }
    }

    public bool WaitForPacket(int timeoutMs)
    {
        Enter();
        try
        {
            return _signal.Wait(timeoutMs);
        }
        finally
        {
            Leave();
        }
    }

    public bool SendPacket(ReadOnlySpan<byte> packet)
    {
        Enter();
        try
        {
            if (RingFull) return false;
            ToWindows.Enqueue((packet.ToArray(), Stopwatch.GetTimestamp()));
            return true;
        }
        finally
        {
            Leave();
        }
    }

    /// <summary>WintunEndSession: from here on, anything inside the ring is a fault - including a call already in it.</summary>
    public void EndSession()
    {
        _ended = true;
        if (Interlocked.Read(ref _inside) > 0) Interlocked.Increment(ref _callsAfterEnd);
    }

    private void Enter()
    {
        Interlocked.Increment(ref _inside);
        if (_ended) Interlocked.Increment(ref _callsAfterEnd);
    }

    private void Leave()
    {
        if (_ended) Interlocked.Increment(ref _callsAfterEnd);
        Interlocked.Decrement(ref _inside);
    }
}
