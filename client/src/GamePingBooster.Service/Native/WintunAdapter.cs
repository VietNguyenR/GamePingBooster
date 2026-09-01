using System.ComponentModel;
using System.Runtime.InteropServices;

namespace GamePingBooster.Service.Native;

/// <summary>
/// Wraps a Wintun adapter and its session behind a disposable API.
/// Lifecycle: Create -> StartSession -> (ReceivePacket / SendPacket) -> Dispose.
/// </summary>
internal sealed class WintunAdapter : IDisposable
{
    private nint _adapter;
    private nint _session;
    private nint _readEvent;
    private bool _disposed;

    /// <summary>Adapter name as it appears in Windows Network Connections.</summary>
    public string Name { get; }

    /// <summary>LUID - the stable identifier Windows uses for the interface.</summary>
    public ulong Luid { get; private set; }

    /// <summary>Interface index - the parameter route and netsh take.</summary>
    public uint InterfaceIndex { get; private set; }

    private WintunAdapter(string name, nint adapter)
    {
        Name = name;
        _adapter = adapter;
    }

    /// <summary>
    /// Creates a new adapter (or reuses one of the same name left over from a previous run).
    /// Throws <see cref="Win32Exception"/> if the process is not running as LocalSystem.
    /// </summary>
    public static WintunAdapter Create(string name, string tunnelType = "GamePingBooster")
    {
        var handle = WintunInterop.WintunCreateAdapter(name, tunnelType, nint.Zero);
        if (handle == nint.Zero)
        {
            var err = Marshal.GetLastPInvokeError();
            throw new Win32Exception(err,
                $"Could not create the Wintun virtual adapter '{name}' (error {err}). " +
                "Check that the process runs as LocalSystem and that wintun.dll sits next to the executable.");
        }

        var adapter = new WintunAdapter(name, handle);
        WintunInterop.WintunGetAdapterLuid(handle, out var luid);
        adapter.Luid = luid;

        if (WinApi.ConvertInterfaceLuidToIndex(in luid, out var index) != 0)
        {
            adapter.Dispose();
            throw new InvalidOperationException("Could not resolve the interface index from the adapter LUID.");
        }
        adapter.InterfaceIndex = index;
        return adapter;
    }

    /// <summary>
    /// Opens the ring buffer for reading and writing packets. Capacity must be a power of two.
    /// 4 MB is what WireGuard uses by default: wide enough for bursts, negligible memory cost.
    /// </summary>
    public void StartSession(uint capacity = 4 * 1024 * 1024)
    {
        if (capacity < WintunInterop.MinRingCapacity || capacity > WintunInterop.MaxRingCapacity ||
            (capacity & (capacity - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity),
                "Capacity must be a power of two between 128KB and 64MB.");
        }

        _session = WintunInterop.WintunStartSession(_adapter, capacity);
        if (_session == nint.Zero)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "WintunStartSession failed.");
        }
        _readEvent = WintunInterop.WintunGetReadWaitEvent(_session);
    }

    /// <summary>
    /// Reads one IP packet that Windows pushed into the adapter and copies it into
    /// <paramref name="destination"/>. Returns -1 when the ring is empty; call
    /// <see cref="WaitForPacket"/> then.
    /// </summary>
    public int ReceivePacket(Span<byte> destination)
    {
        var ptr = WintunInterop.WintunReceivePacket(_session, out var size);
        if (ptr == nint.Zero)
        {
            var err = Marshal.GetLastPInvokeError();
            if (err == WintunInterop.ErrorNoMoreItems) return -1;
            throw new Win32Exception(err, "WintunReceivePacket failed.");
        }

        try
        {
            if (size > destination.Length) return 0; // larger than our buffer; cannot happen with a correct MTU
            unsafe
            {
                new ReadOnlySpan<byte>((void*)ptr, (int)size).CopyTo(destination);
            }
            return (int)size;
        }
        finally
        {
            WintunInterop.WintunReleaseReceivePacket(_session, ptr);
        }
    }

    /// <summary>Waits for a new packet in the ring. Returns false on timeout.</summary>
    public bool WaitForPacket(int timeoutMs)
        => WinApi.WaitForSingleObject(_readEvent, (uint)timeoutMs) == WinApi.WaitObject0;

    /// <summary>
    /// Injects an IP packet (received from the relay) into the Windows network stack.
    /// Returns false when the ring was full and the packet had to be dropped.
    ///
    /// The caller must count those drops. Dropping is the right behaviour - blocking this thread
    /// would stall every packet behind it, and UDP tolerates loss - but a drop nobody counts is
    /// indistinguishable from a problem out on the internet, and telling those two apart is
    /// precisely what this project exists to do.
    /// </summary>
    public bool SendPacket(ReadOnlySpan<byte> packet)
    {
        var ptr = WintunInterop.WintunAllocateSendPacket(_session, (uint)packet.Length);
        if (ptr == nint.Zero)
        {
            var err = Marshal.GetLastPInvokeError();
            if (err == WintunInterop.ErrorBufferOverflow) return false;
            throw new Win32Exception(err, "WintunAllocateSendPacket failed.");
        }

        unsafe
        {
            packet.CopyTo(new Span<byte>((void*)ptr, packet.Length));
        }
        WintunInterop.WintunSendPacket(_session, ptr);
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_session != nint.Zero)
        {
            WintunInterop.WintunEndSession(_session);
            _session = nint.Zero;
        }
        if (_adapter != nint.Zero)
        {
            // Closing the adapter also deletes every route pointing at it, so the user's
            // network recovers on its own.
            WintunInterop.WintunCloseAdapter(_adapter);
            _adapter = nint.Zero;
        }
    }
}
