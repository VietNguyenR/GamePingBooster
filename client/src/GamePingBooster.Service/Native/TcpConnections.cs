using System.Net;
using System.Runtime.InteropServices;

namespace GamePingBooster.Service.Native;

/// <summary>
/// The machine's IPv4 TCP connections, and closing some of them - what TCPView's "Close Connection" does.
///
/// Why the tunnel needs it: a TCP connection that rode one relay cannot continue on another. The server knows it by
/// the first relay's exit address, and the adapter may even be given another address. Nothing tells the program: no
/// reset ever arrives, its packets go nowhere, and it waits out its own timeouts. On 2026-10-06 (FPT, owner's PC) the
/// tunnel moved between matches 5 s after a training match ended, while PUBG was taking its lobby back over those
/// connections - "Initializing..." for more than two minutes, until the game was closed. Closed here, each such
/// connection fails at once in the program, which opens a new one - through the new relay.
///
/// MIB_TCPROW is five DWORDs; ports are in network order in the low 16 bits. Closing needs Administrator, which the
/// service has.
/// </summary>
internal static partial class TcpConnections
{
    private const string Dll = "iphlpapi.dll";
    private const uint NoError = 0;
    private const uint ErrorInsufficientBuffer = 122;

    // MIB_TCP_STATE_*: 2 is LISTEN, 3 SYN_SENT ... 10 LAST_ACK, 11 TIME_WAIT, 12 DELETE_TCB.
    private const uint StateListen = 2;
    private const uint StateTimeWait = 11;
    private const uint StateDeleteTcb = 12;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Row
    {
        public uint State;
        public uint LocalAddress;
        public uint LocalPort;
        public uint RemoteAddress;
        public uint RemotePort;

        public readonly override string ToString() =>
            $"{new IPAddress(LocalAddress)}:{Port(LocalPort)} -> {new IPAddress(RemoteAddress)}:{Port(RemotePort)}";

        private static int Port(uint raw) => (int)(((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF));
    }

    [LibraryImport(Dll)]
    private static partial uint GetTcpTable(nint table, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool order);

    [LibraryImport(Dll)]
    private static partial uint SetTcpEntry(in Row row);

    /// <summary>
    /// Every connection from <paramref name="local"/> that is open or opening - not listening, not already in
    /// TIME_WAIT. Empty when the table cannot be read: this is a courtesy to the program, never a reason to fail.
    /// </summary>
    public static List<Row> From(IPAddress local)
    {
        if (local.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return [];
        var want = BitConverter.ToUInt32(local.GetAddressBytes(), 0);
        return Open(row => row.LocalAddress == want);
    }

    /// <summary>
    /// The remote address of every connection that is open or opening, from any local address. Empty when the table
    /// cannot be read.
    /// </summary>
    public static HashSet<IPAddress> OpenRemotes() => [.. Open(_ => true).Select(row => new IPAddress(row.RemoteAddress))];

    private static List<Row> Open(Func<Row, bool> wanted)
    {
        var rows = new List<Row>();
        uint size = 0;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var buffer = size == 0 ? 0 : Marshal.AllocHGlobal((int)size);
            try
            {
                var error = GetTcpTable(buffer, ref size, order: false);
                if (error == ErrorInsufficientBuffer) continue;
                if (error != NoError || buffer == 0) return rows;

                // Read field by field: five DWORDs, no marshaller needed under Native AOT.
                var count = Marshal.ReadInt32(buffer);
                for (var i = 0; i < count; i++)
                {
                    var at = 4 + i * 20;
                    var row = new Row
                    {
                        State = (uint)Marshal.ReadInt32(buffer, at),
                        LocalAddress = (uint)Marshal.ReadInt32(buffer, at + 4),
                        LocalPort = (uint)Marshal.ReadInt32(buffer, at + 8),
                        RemoteAddress = (uint)Marshal.ReadInt32(buffer, at + 12),
                        RemotePort = (uint)Marshal.ReadInt32(buffer, at + 16),
                    };
                    if (row.State is > StateListen and < StateTimeWait && wanted(row)) rows.Add(row);
                }
                return rows;
            }
            catch (Exception) when (attempt < 3)
            {
                // A DLL that will not load, a table that grew between the two calls: try once more, then give up.
            }
            catch (Exception)
            {
                return rows;
            }
            finally
            {
                if (buffer != 0) Marshal.FreeHGlobal(buffer);
            }
        }
        return rows;
    }

    /// <summary>
    /// Closes each connection; returns how many Windows closed, and the first Win32 error for one it did not (0 when
    /// none failed) - 5 is no Administrator, 1168 (not found) a connection already gone.
    /// </summary>
    public static (int Closed, uint FirstError) Close(IEnumerable<Row> rows)
    {
        var closed = 0;
        uint firstError = 0;
        foreach (var row in rows)
        {
            var target = row with { State = StateDeleteTcb };
            try
            {
                var error = SetTcpEntry(in target);
                if (error == NoError) closed++;
                else if (firstError == 0) firstError = error;
            }
            catch (Exception)
            {
                return (closed, firstError == 0 ? uint.MaxValue : firstError);
            }
        }
        return (closed, firstError);
    }
}
