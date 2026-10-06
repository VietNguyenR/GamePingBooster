using System.Buffers.Binary;
using System.Net.Sockets;

namespace GamePingBooster.Service.Native;

/// <summary>
/// What Windows' own TCP knows about one connection: SIO_TCP_INFO, version 0 (Windows 10 1703 and later).
///
/// Why SplitProxy needs it: a connection over the line whose path stops carrying packets gives the program nothing - no
/// reset, no error, just a wait that lasts until Windows gives up retransmitting, a minute or more. Waiting alone tells a
/// dead path from a server that is thinking (a long poll) only by guessing at a timeout; TCP's own counters tell them
/// apart: a thinking server has ACKed what was sent (nothing in flight), a dead path leaves bytes in flight and runs
/// retransmission timeouts.
///
/// TCP_INFO_v0 layout (88 bytes): State 0, Mss 4, ConnectionTimeMs 8 (u64), TimestampsEnabled 16, RttUs 20,
/// MinRttUs 24, BytesInFlight 28, Cwnd 32, SndWnd 36, RcvWnd 40, RcvBuf 44, BytesOut 48 (u64), BytesIn 56 (u64),
/// BytesReordered 64, BytesRetrans 68, FastRetrans 72, DupAcksIn 76, TimeoutEpisodes 80, SynRetrans 84.
/// TunnelCheck checks BytesOut/BytesIn against a loopback connection, so a wrong offset fails there.
/// </summary>
internal static class TcpInfo
{
    // _WSAIORW(IOC_VENDOR, 39)
    private const int SioTcpInfo = unchecked((int)0xD8000027);
    private const int Size = 88;

    internal readonly record struct Sample(uint BytesInFlight, ulong BytesOut, ulong BytesIn, uint TimeoutEpisodes);

    /// <summary>Null where Windows does not have it, or the socket is gone.</summary>
    public static Sample? Read(Socket socket)
    {
        try
        {
            var output = new byte[Size];
            var read = socket.IOControl(SioTcpInfo, BitConverter.GetBytes(0u), output);
            if (read < 84) return null;
            return new Sample(
                BinaryPrimitives.ReadUInt32LittleEndian(output.AsSpan(28)),
                BinaryPrimitives.ReadUInt64LittleEndian(output.AsSpan(48)),
                BinaryPrimitives.ReadUInt64LittleEndian(output.AsSpan(56)),
                BinaryPrimitives.ReadUInt32LittleEndian(output.AsSpan(80)));
        }
        catch (Exception)
        {
            return null;
        }
    }
}
