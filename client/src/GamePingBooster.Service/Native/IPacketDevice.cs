namespace GamePingBooster.Service.Native;

/// <summary>
/// The virtual adapter as the packet path sees it: a ring Windows pushes packets into, and one it takes
/// packets out of.
///
/// <see cref="WintunAdapter"/> is the only production implementation. The interface exists so the whole
/// packet path - <see cref="Tunnel.AdapterPump"/>, <see cref="Tunnel.TunnelClient"/>'s downlink - can be
/// driven in-process against a fake relay, without a driver or Administrator rights (GamePingBooster.
/// TunnelCheck). See docs/MULTI-TUNNEL.md, section 10.2.
///
/// The contract is Wintun's, including its threading: one reader of the ring, and any number of threads
/// injecting - api/wintun.h documents WintunAllocateSendPacket and WintunSendPacket as thread-safe.
/// </summary>
internal interface IPacketDevice
{
    /// <summary>
    /// Copies the next packet Windows sent into <paramref name="destination"/> and returns its length; -1 when
    /// there is none (call <see cref="WaitForPacket"/>); 0 when it did not fit. One reader only.
    /// </summary>
    int ReceivePacket(Span<byte> destination);

    /// <summary>Waits for a packet to read. False on timeout.</summary>
    bool WaitForPacket(int timeoutMs);

    /// <summary>Hands a packet to Windows. False when the ring was full and it was dropped. Thread-safe.</summary>
    bool SendPacket(ReadOnlySpan<byte> packet);
}
