using System.Net;
using System.Runtime.InteropServices;

namespace GamePingBooster.Service.Native;

/// <summary>
/// Per-connection TCP counters - the extended statistics Resource Monitor reads (GetPerTcpConnectionEStats, data
/// table). Discovery uses them to tell a TCP game's match connection from its quiet gateway ones by rate, the way it
/// tells UDP flows apart with ETW.
///
/// Why not ETW for TCP: Microsoft-Windows-Kernel-Network reports TCP sends and receives for the whole machine, so a
/// Steam download while the game runs is thousands of events a second the session pays for and throws away. These
/// counters are per connection: the service asks only about the game's own connections, a few of them, once per poll,
/// and nothing is paid for anything else. Collection is off by default and is switched on per connection (needs
/// Administrator, which the service has); it counts from that moment, so the first read is a baseline.
///
/// Nothing here touches the game: the counters belong to Windows' TCP stack, as the connection table does.
/// </summary>
internal static unsafe partial class TcpEStats
{
    private const string Dll = "iphlpapi.dll";
    private const int TcpConnectionEstatsData = 1;

    // TCP_ESTATS_DATA_ROD_v0: six ULONG64 (DataBytesOut, DataSegsOut, DataBytesIn, DataSegsIn, SegsOut, SegsIn), then
    // ULONGs and ULONG64s to 96 bytes with natural alignment. Only the first six are read.
    private const int RodSize = 96;

    [LibraryImport(Dll)]
    private static partial uint SetPerTcpConnectionEStats(in TcpConnections.Row row, int type, byte* rw, uint rwVersion,
        uint rwSize, uint offset);

    [LibraryImport(Dll)]
    private static partial uint GetPerTcpConnectionEStats(in TcpConnections.Row row, int type, byte* rw, uint rwVersion,
        uint rwSize, byte* ros, uint rosVersion, uint rosSize, byte* rod, uint rodVersion, uint rodSize);

    /// <summary>What one connection has carried since collection was switched on.</summary>
    internal readonly record struct Counters(ulong DataSegsOut, ulong DataSegsIn, ulong DataBytesOut, ulong DataBytesIn);

    /// <summary>Switches collection on for one connection; returns the Win32 error, 0 when it is on.</summary>
    public static uint Enable(IPEndPoint local, IPEndPoint remote)
    {
        var row = RowFor(local, remote);
        byte enable = 1;   // TCP_ESTATS_DATA_RW_v0 { BOOLEAN EnableCollection }
        try
        {
            return SetPerTcpConnectionEStats(in row, TcpConnectionEstatsData, &enable, 0, 1, 0);
        }
        catch (Exception)
        {
            return uint.MaxValue;
        }
    }

    /// <summary>The connection's counters, or null when Windows has none for it (closed, or collection not on).</summary>
    public static Counters? Read(IPEndPoint local, IPEndPoint remote)
    {
        var row = RowFor(local, remote);
        var rod = stackalloc byte[RodSize];
        try
        {
            var error = GetPerTcpConnectionEStats(in row, TcpConnectionEstatsData, null, 0, 0, null, 0, 0, rod, 0, RodSize);
            if (error != 0) return null;
        }
        catch (Exception)
        {
            return null;
        }
        var u = (ulong*)rod;
        return new Counters(DataSegsOut: u[1], DataSegsIn: u[3], DataBytesOut: u[0], DataBytesIn: u[2]);
    }

    /// <summary>MIB_TCPROW for a connection: addresses as the table holds them, ports in network order. The state is
    /// not part of the lookup.</summary>
    private static TcpConnections.Row RowFor(IPEndPoint local, IPEndPoint remote) => new()
    {
        State = 5,
        LocalAddress = BitConverter.ToUInt32(local.Address.GetAddressBytes(), 0),
        LocalPort = NetworkOrder(local.Port),
        RemoteAddress = BitConverter.ToUInt32(remote.Address.GetAddressBytes(), 0),
        RemotePort = NetworkOrder(remote.Port),
    };

    private static uint NetworkOrder(int port) => (uint)(((port & 0xFF) << 8) | ((port >> 8) & 0xFF));
}
