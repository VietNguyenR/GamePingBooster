using System.Net;
using System.Runtime.InteropServices;

namespace GamePingBooster.Service.Native;

/// <summary>
/// Bytes per process over TCP, IPv4 and IPv6, for a few seconds at a time - what Resource Monitor's Network tab shows,
/// read the way it reads it: the extended statistics of each connection (GetPerTcpConnectionEStats /
/// GetPerTcp6ConnectionEStats, data table), with the owning process from the connection table.
///
/// Why not the Kernel-Network ETW provider: TCP there is every segment on the machine, thousands of events a second
/// during a Steam download (see <see cref="TcpEStats"/>), and the PC tune-up panel asks exactly when something is
/// downloading. These counters cost one call per connection to switch on and one to read.
///
/// Collection is off by default and counts from the moment it is switched on, so a count is: switch on, read (a
/// baseline), wait, read again, switch off. Connections opened during the wait are not counted - downloads keep
/// theirs open, and what is missed shows as the part of the line's traffic no app is named for.
///
/// Rows are kept as Windows wrote them (ports in network order) and handed back unchanged.
/// </summary>
internal static unsafe partial class TcpTraffic
{
    private const string Dll = "iphlpapi.dll";
    private const uint AfInet = 2, AfInet6 = 23;
    private const int TcpTableOwnerPidAll = 5;
    private const uint ErrorInsufficientBuffer = 122;
    private const uint StateEstablished = 5;
    private const int TcpConnectionEstatsData = 1;
    private const int RodSize = 96; // TCP_ESTATS_DATA_ROD_v0, see TcpEStats

    [LibraryImport(Dll)]
    private static partial uint GetExtendedTcpTable(nint table, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool order,
        uint addressFamily, int tableClass, uint reserved);

    [LibraryImport(Dll)]
    private static partial uint SetPerTcpConnectionEStats(byte* row, int type, byte* rw, uint rwVersion, uint rwSize, uint offset);

    [LibraryImport(Dll)]
    private static partial uint GetPerTcpConnectionEStats(byte* row, int type, byte* rw, uint rwVersion, uint rwSize,
        byte* ros, uint rosVersion, uint rosSize, byte* rod, uint rodVersion, uint rodSize);

    [LibraryImport(Dll)]
    private static partial uint SetPerTcp6ConnectionEStats(byte* row, int type, byte* rw, uint rwVersion, uint rwSize, uint offset);

    [LibraryImport(Dll)]
    private static partial uint GetPerTcp6ConnectionEStats(byte* row, int type, byte* rw, uint rwVersion, uint rwSize,
        byte* ros, uint rosVersion, uint rosSize, byte* rod, uint rodVersion, uint rodSize);

    /// <summary>
    /// One established connection: the MIB_TCPROW (20 bytes) or MIB_TCP6ROW (52 bytes) that names it to the stats
    /// calls, the process that owns it and the address it was opened from.
    /// </summary>
    internal sealed class Conn(byte[] row, bool v6, uint pid, IPAddress local, IPAddress remote)
    {
        public byte[] Row { get; } = row;
        public bool V6 { get; } = v6;
        public uint Pid { get; } = pid;
        public IPAddress Local { get; } = local;
        public IPAddress Remote { get; } = remote;
    }

    /// <summary>Bytes in and out since collection was switched on.</summary>
    internal readonly record struct Counters(ulong BytesIn, ulong BytesOut);

    /// <summary>Every established connection on the machine, IPv4 then IPv6. Empty for a table that cannot be read.</summary>
    public static List<Conn> Established()
    {
        var result = new List<Conn>();
        ReadTable(AfInet, 24, at =>
        {
            // MIB_TCPROW_OWNER_PID: state, local addr, local port, remote addr, remote port, pid.
            var p = (byte*)at;
            var u = (uint*)p;
            if (u[0] != StateEstablished) return;
            var row = new byte[20];
            new ReadOnlySpan<byte>(p, 20).CopyTo(row);
            result.Add(new Conn(row, false, u[5], new IPAddress(u[1]), new IPAddress(u[3])));
        });
        ReadTable(AfInet6, 56, at =>
        {
            // MIB_TCP6ROW_OWNER_PID: local addr[16], scope, port, remote addr[16], scope, port, state, pid.
            var p = (byte*)at;
            var u = (uint*)p;
            if (u[12] != StateEstablished) return;
            // MIB_TCP6ROW: state, then the owner row's first 48 bytes in the same order.
            var row = new byte[52];
            BitConverter.TryWriteBytes(row.AsSpan(0, 4), StateEstablished);
            new ReadOnlySpan<byte>(p, 48).CopyTo(row.AsSpan(4));
            result.Add(new Conn(row, true, u[13], new IPAddress(new ReadOnlySpan<byte>(p, 16)),
                new IPAddress(new ReadOnlySpan<byte>(p + 24, 16))));
        });
        return result;
    }

    /// <summary>Switches byte counting on (true) or off for one connection; returns the Win32 error, 0 when done.</summary>
    public static uint SetCounting(Conn conn, bool on)
    {
        byte enable = on ? (byte)1 : (byte)0; // TCP_ESTATS_DATA_RW_v0 { BOOLEAN EnableCollection }
        try
        {
            fixed (byte* row = conn.Row)
            {
                return conn.V6
                    ? SetPerTcp6ConnectionEStats(row, TcpConnectionEstatsData, &enable, 0, 1, 0)
                    : SetPerTcpConnectionEStats(row, TcpConnectionEstatsData, &enable, 0, 1, 0);
            }
        }
        catch (Exception)
        {
            return uint.MaxValue;
        }
    }

    /// <summary>The connection's byte counts, or null when Windows has none (closed, or counting not on).</summary>
    public static Counters? Read(Conn conn)
    {
        var rod = stackalloc byte[RodSize];
        try
        {
            fixed (byte* row = conn.Row)
            {
                var error = conn.V6
                    ? GetPerTcp6ConnectionEStats(row, TcpConnectionEstatsData, null, 0, 0, null, 0, 0, rod, 0, RodSize)
                    : GetPerTcpConnectionEStats(row, TcpConnectionEstatsData, null, 0, 0, null, 0, 0, rod, 0, RodSize);
                if (error != 0) return null;
            }
        }
        catch (Exception)
        {
            return null;
        }
        var u = (ulong*)rod;
        return new Counters(BytesIn: u[2], BytesOut: u[0]); // DataBytesOut, DataSegsOut, DataBytesIn, ...
    }

    private static void ReadTable(uint family, int rowSize, Action<nint> each)
    {
        uint size = 0;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var buffer = size == 0 ? 0 : Marshal.AllocHGlobal((int)size);
            try
            {
                var error = GetExtendedTcpTable(buffer, ref size, order: false, family, TcpTableOwnerPidAll, 0);
                if (error == ErrorInsufficientBuffer) continue;
                if (error != 0 || buffer == 0) return;
                var count = Marshal.ReadInt32(buffer);
                for (var i = 0; i < count; i++) each(buffer + 4 + i * rowSize);
                return;
            }
            catch (Exception) when (attempt < 3)
            {
                // The table grew between the two calls, or the DLL would not load: once more, then give up.
            }
            catch (Exception)
            {
                return;
            }
            finally
            {
                if (buffer != 0) Marshal.FreeHGlobal(buffer);
            }
        }
    }
}
