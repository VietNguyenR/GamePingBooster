using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace GamePingBooster.Service.Native;

/// <summary>
/// The IP Helper calls RouteManager makes instead of starting netsh.exe: routes, the adapter's
/// address, its MTU. Each is a function call into iphlpapi.dll that returns in microseconds and
/// hands back a Win32 error code, where netsh was a process start per command - seconds each as
/// LocalSystem while an adapter was coming up, and about 27 of the 30 seconds a connect took on
/// 2026-09-17.
///
/// NOTHING HERE IS PERSISTENT, which was netsh's reason for being (store=active). Routes made by
/// CreateIpForwardEntry2 live in the active store only and are gone after a reboot, exactly like
/// store=active; the address and interface settings belong to a Wintun adapter that is deleted on
/// disconnect and with the service's process. WireGuard for Windows configures its tunnels through
/// these same calls for the same reason.
///
/// The row structs are laid out by explicit offset from the Windows SDK headers (netioapi.h, x64 and
/// ARM64 - the only targets this service is built for). The sizes are asserted in <see cref="SelfCheck"/>
/// and the offsets were checked by reading live rows on a real machine against netsh and
/// NetworkInformation. Every write starts from Initialize* or a Get*, so fields not named here keep
/// the values Windows chose.
/// </summary>
internal static partial class IpHelper
{
    private const string Dll = "iphlpapi.dll";

    internal const uint NoError = 0;
    internal const uint ErrorNotFound = 1168;
    internal const uint ErrorObjectAlreadyExists = 5010;

    private const ushort AfInet = 2;

    /// <summary>MIB_IPPROTO_NETMGMT: a static route added by management, which is what netsh wrote.</summary>
    private const int ProtocolNetMgmt = 3;

    /// <summary>NldsPreferred: the address is usable at once, no duplicate address detection to wait out.</summary>
    private const int DadStatePreferred = 4;

    /// <summary>IpPrefixOriginManual / IpSuffixOriginManual.</summary>
    private const int OriginManual = 1;

    // ------------------------------------------------------------------ rows

    /// <summary>MIB_IPFORWARD_ROW2 - 104 bytes. Only the IPv4 halves of the SOCKADDR_INET unions are named.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 104)]
    internal struct ForwardRow
    {
        [FieldOffset(0)] public ulong InterfaceLuid;
        [FieldOffset(8)] public uint InterfaceIndex;
        [FieldOffset(12)] public ushort DestinationFamily;
        [FieldOffset(16)] public uint DestinationAddress;
        [FieldOffset(40)] public byte DestinationPrefixLength;
        [FieldOffset(44)] public ushort NextHopFamily;
        [FieldOffset(48)] public uint NextHopAddress;
        [FieldOffset(72)] public byte SitePrefixLength;
        [FieldOffset(76)] public uint ValidLifetime;
        [FieldOffset(80)] public uint PreferredLifetime;
        [FieldOffset(84)] public uint Metric;
        [FieldOffset(88)] public int Protocol;
        [FieldOffset(92)] public byte Loopback;
        [FieldOffset(93)] public byte AutoconfigureAddress;
        [FieldOffset(94)] public byte Publish;
        [FieldOffset(95)] public byte Immortal;
        [FieldOffset(96)] public uint Age;
        [FieldOffset(100)] public int Origin;
    }

    /// <summary>MIB_UNICASTIPADDRESS_ROW - 80 bytes.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 80)]
    internal struct UnicastRow
    {
        [FieldOffset(0)] public ushort Family;
        [FieldOffset(4)] public uint Address;
        [FieldOffset(32)] public ulong InterfaceLuid;
        [FieldOffset(40)] public uint InterfaceIndex;
        [FieldOffset(44)] public int PrefixOrigin;
        [FieldOffset(48)] public int SuffixOrigin;
        [FieldOffset(52)] public uint ValidLifetime;
        [FieldOffset(56)] public uint PreferredLifetime;
        [FieldOffset(60)] public byte OnLinkPrefixLength;
        [FieldOffset(61)] public byte SkipAsSource;
        [FieldOffset(64)] public int DadState;
        [FieldOffset(68)] public uint ScopeId;
        [FieldOffset(72)] public long CreationTimeStamp;
    }

    /// <summary>MIB_IPINTERFACE_ROW - 168 bytes. Only what is read or written here is named.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 168)]
    internal struct InterfaceRow
    {
        [FieldOffset(0)] public ushort Family;
        [FieldOffset(8)] public ulong InterfaceLuid;
        [FieldOffset(16)] public uint InterfaceIndex;
        [FieldOffset(44)] public byte UseAutomaticMetric;
        [FieldOffset(56)] public uint DadTransmits;
        [FieldOffset(144)] public uint SitePrefixLength;
        [FieldOffset(148)] public uint Metric;
        [FieldOffset(152)] public uint NlMtu;
        [FieldOffset(156)] public byte Connected;
    }

    // ------------------------------------------------------------------ imports

    [LibraryImport(Dll)] private static partial void InitializeIpForwardEntry(ref ForwardRow row);
    [LibraryImport(Dll)] private static partial uint CreateIpForwardEntry2(in ForwardRow row);
    [LibraryImport(Dll)] private static partial uint DeleteIpForwardEntry2(in ForwardRow row);
    [LibraryImport(Dll)] private static partial uint GetIpForwardTable2(ushort family, out nint table);

    [LibraryImport(Dll)] private static partial void InitializeUnicastIpAddressEntry(ref UnicastRow row);
    [LibraryImport(Dll)] private static partial uint CreateUnicastIpAddressEntry(in UnicastRow row);
    [LibraryImport(Dll)] private static partial uint DeleteUnicastIpAddressEntry(in UnicastRow row);
    [LibraryImport(Dll)] private static partial uint GetUnicastIpAddressTable(ushort family, out nint table);

    [LibraryImport(Dll)] private static partial void InitializeIpInterfaceEntry(ref InterfaceRow row);
    [LibraryImport(Dll)] private static partial uint GetIpInterfaceEntry(ref InterfaceRow row);
    [LibraryImport(Dll)] private static partial uint SetIpInterfaceEntry(ref InterfaceRow row);

    [LibraryImport(Dll)] private static partial void FreeMibTable(nint table);

    /// <summary>
    /// Refuses to run on a build where the rows are not the sizes Windows expects. A wrong size would
    /// not fail loudly: Windows would read past the end of the row, or write settings into the wrong
    /// field of somebody's network adapter.
    /// </summary>
    internal static void SelfCheck()
    {
        if (Unsafe.SizeOf<ForwardRow>() != 104 || Unsafe.SizeOf<UnicastRow>() != 80 ||
            Unsafe.SizeOf<InterfaceRow>() != 168 || IntPtr.Size != 8)
        {
            throw new PlatformNotSupportedException(
                "The IP Helper row layouts in this build do not match a 64-bit Windows. Routing is refused.");
        }
    }

    // ------------------------------------------------------------------ prefixes

    /// <summary>An IPv4 prefix as Windows stores it: network address (host bits cleared) and length.</summary>
    internal readonly record struct Prefix(uint Address, byte Length)
    {
        public override string ToString() => $"{new IPAddress(Address)}/{Length}";
    }

    /// <summary>"a.b.c.d/n" as a prefix, host bits cleared. Null when it is not an IPv4 CIDR.</summary>
    internal static Prefix? ParsePrefix(string cidr)
    {
        var parts = cidr.Split('/');
        if (parts.Length != 2 ||
            !IPAddress.TryParse(parts[0], out var ip) || ip.AddressFamily != AddressFamily.InterNetwork ||
            !byte.TryParse(parts[1], out var bits) || bits > 32)
        {
            return null;
        }

        return new Prefix(Mask(ToInAddr(ip), bits), bits);
    }

    /// <summary>
    /// The four address bytes reinterpreted as IN_ADDR, the way SOCKADDR_IN carries them - no byte
    /// swapping, the same convention as GamePingBooster.Core.Native.IpHelperInterop.
    /// </summary>
    internal static uint ToInAddr(IPAddress ip) => BitConverter.ToUInt32(ip.GetAddressBytes(), 0);

    private static uint Mask(uint inAddr, byte bits)
    {
        if (bits == 0) return 0;
        // Host order for the arithmetic, back to the wire reinterpretation after.
        var host = (uint)IPAddress.NetworkToHostOrder((int)inAddr);
        host &= bits == 32 ? uint.MaxValue : uint.MaxValue << (32 - bits);
        return (uint)IPAddress.HostToNetworkOrder((int)host);
    }

    // ------------------------------------------------------------------ routes

    /// <summary>
    /// Adds an IPv4 route: on-link when <paramref name="nextHop"/> is null - what the tunnel's routes
    /// must be, see RouteManager.InstallGameRoutes - or through a gateway. Returns the Win32 error.
    /// </summary>
    internal static uint AddRoute(Prefix prefix, uint interfaceIndex, IPAddress? nextHop, uint metric)
    {
        var row = default(ForwardRow);
        InitializeIpForwardEntry(ref row);
        row.InterfaceIndex = interfaceIndex;
        row.DestinationFamily = AfInet;
        row.DestinationAddress = prefix.Address;
        row.DestinationPrefixLength = prefix.Length;
        row.NextHopFamily = AfInet;
        row.NextHopAddress = nextHop is null ? 0 : ToInAddr(nextHop);
        row.Metric = metric;
        row.Protocol = ProtocolNetMgmt;
        return CreateIpForwardEntry2(in row);
    }

    /// <summary>
    /// Deletes every IPv4 route to any of <paramref name="targets"/> on the named interface, whatever
    /// its next hop - what `netsh delete route prefix= interface=` did. One read of the routing table
    /// however many there are. Returns how many rows were deleted; a route that is not there is not an
    /// error.
    /// </summary>
    internal static int DeleteRoutes(IReadOnlyCollection<(Prefix Prefix, uint InterfaceIndex)> targets)
    {
        if (targets.Count == 0) return 0;
        var wanted = targets.ToHashSet();

        var matches = new List<ForwardRow>();
        ReadTable<ForwardRow>(GetIpForwardTable2, row =>
        {
            if (row.DestinationFamily == AfInet &&
                wanted.Contains((new Prefix(row.DestinationAddress, row.DestinationPrefixLength), row.InterfaceIndex)))
            {
                matches.Add(row);
            }
        });

        var deleted = 0;
        foreach (var row in matches)
        {
            var copy = row;
            var result = DeleteIpForwardEntry2(in copy);
            if (result is NoError) deleted++;
        }
        return deleted;
    }

    // ------------------------------------------------------------------ the adapter

    /// <summary>
    /// Makes <paramref name="address"/>/<paramref name="prefixLength"/> the adapter's only IPv4 address.
    /// Any other IPv4 address on it - left by an earlier session on a reused adapter - is removed first,
    /// which is what `netsh set address source=static` did. Returns the Win32 error of the create.
    /// </summary>
    internal static uint SetOnlyAddress(uint interfaceIndex, IPAddress address, byte prefixLength)
    {
        var wanted = ToInAddr(address);

        var stale = new List<UnicastRow>();
        ReadTable<UnicastRow>(GetUnicastIpAddressTable, row =>
        {
            if (row.Family == AfInet && row.InterfaceIndex == interfaceIndex &&
                (row.Address != wanted || row.OnLinkPrefixLength != prefixLength))
            {
                stale.Add(row);
            }
        });
        foreach (var row in stale)
        {
            var copy = row;
            DeleteUnicastIpAddressEntry(in copy);
        }

        var entry = default(UnicastRow);
        InitializeUnicastIpAddressEntry(ref entry);
        entry.Family = AfInet;
        entry.Address = wanted;
        entry.InterfaceIndex = interfaceIndex;
        entry.OnLinkPrefixLength = prefixLength;
        entry.PrefixOrigin = OriginManual;
        entry.SuffixOrigin = OriginManual;
        entry.DadState = DadStatePreferred;

        var result = CreateUnicastIpAddressEntry(in entry);
        return result == ErrorObjectAlreadyExists ? NoError : result;
    }

    /// <summary>
    /// Sets the adapter's IPv4 MTU and turns duplicate address detection off (dadtransmits=0) - read,
    /// change those two fields, write back. Returns the Win32 error, and the MTU Windows reports after.
    /// </summary>
    internal static (uint Error, uint MtuAfter) SetMtu(uint interfaceIndex, uint mtu)
    {
        var row = default(InterfaceRow);
        InitializeIpInterfaceEntry(ref row);
        row.Family = AfInet;
        row.InterfaceIndex = interfaceIndex;

        var result = GetIpInterfaceEntry(ref row);
        if (result != NoError) return (result, 0);

        row.NlMtu = mtu;
        row.DadTransmits = 0;
        // Documented requirement for IPv4: SetIpInterfaceEntry refuses the row otherwise.
        row.SitePrefixLength = 0;

        result = SetIpInterfaceEntry(ref row);
        if (result != NoError) return (result, 0);

        var check = default(InterfaceRow);
        InitializeIpInterfaceEntry(ref check);
        check.Family = AfInet;
        check.InterfaceIndex = interfaceIndex;
        return GetIpInterfaceEntry(ref check) == NoError ? (NoError, check.NlMtu) : (NoError, 0);
    }

    /// <summary>The interface's IPv4 row as Windows reports it - metric, MTU, whether it is connected. Null when it has none.</summary>
    internal static InterfaceRow? ReadInterface(uint interfaceIndex)
    {
        var row = default(InterfaceRow);
        InitializeIpInterfaceEntry(ref row);
        row.Family = AfInet;
        row.InterfaceIndex = interfaceIndex;
        return GetIpInterfaceEntry(ref row) == NoError ? row : null;
    }

    /// <summary>Every IPv4 route in the table.</summary>
    internal static List<ForwardRow> ReadRoutes()
    {
        var rows = new List<ForwardRow>();
        ReadTable<ForwardRow>(GetIpForwardTable2, rows.Add);
        return rows;
    }

    /// <summary>Every IPv4 unicast address on the machine.</summary>
    internal static List<UnicastRow> ReadAddresses()
    {
        var rows = new List<UnicastRow>();
        ReadTable<UnicastRow>(GetUnicastIpAddressTable, rows.Add);
        return rows;
    }

    // ------------------------------------------------------------------ tables

    private delegate uint GetTable(ushort family, out nint table);

    /// <summary>
    /// Walks a MIB_*_TABLE2: a ULONG count, then the rows, which start at offset 8 because every row
    /// type here is 8-byte aligned.
    /// </summary>
    private static unsafe void ReadTable<T>(GetTable get, Action<T> each) where T : unmanaged
    {
        var result = get(AfInet, out var table);
        if (result != NoError)
        {
            throw new InvalidOperationException($"Could not read the IP table (error {result}).");
        }

        try
        {
            var count = *(uint*)table;
            var rows = (T*)(table + 8);
            for (var i = 0; i < count; i++) each(rows[i]);
        }
        finally
        {
            FreeMibTable(table);
        }
    }

    /// <summary>A Win32 error code with its system message, for an exception or a log line.</summary>
    internal static string Describe(uint error) =>
        $"error {error}: {new System.ComponentModel.Win32Exception((int)error).Message}";
}
