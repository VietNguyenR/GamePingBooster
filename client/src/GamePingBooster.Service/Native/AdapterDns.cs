using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace GamePingBooster.Service.Native;

/// <summary>
/// Reads the resolvers Windows is currently configured to use, through GetAdaptersAddresses.
///
/// Not <c>NetworkInterface.GetAllNetworkInterfaces()</c>, which would be four lines instead of this
/// file. That call cost seconds as LocalSystem while an adapter was coming up and was a large part
/// of the 30-second connect fixed on 2026-09-17; this runs on the connect path for the same reason
/// routing does, so it goes to the same API underneath by hand. See <see cref="IpHelper"/>.
///
/// Read BEFORE the resolver is put in front of them, and kept for as long as it is running: these
/// are the addresses every name that is not Steam's gets forwarded to. Reading them afterwards
/// would return this service's own loopback address and build a resolver that asks itself.
/// </summary>
internal static partial class AdapterDns
{
    private const string Dll = "iphlpapi.dll";

    private const uint AfUnspec = 0;
    private const uint ErrorBufferOverflow = 111;

    // Everything that is not a DNS server address, skipped: this is a hot call and the unicast
    // lists on a machine with Hyper-V are long.
    private const uint SkipUnicast = 0x0001;
    private const uint SkipAnycast = 0x0002;
    private const uint SkipMulticast = 0x0004;
    private const uint SkipFriendlyName = 0x0020;

    private const int IfOperStatusUp = 1;

    // Offsets into IP_ADAPTER_ADDRESSES_LH (x64), from iptypes.h. Named rather than mapped to a
    // struct because three fields out of thirty are needed and the rest are unions of pointers.
    private const int OffsetNext = 8;
    private const int OffsetFirstDnsServer = 48;
    private const int OffsetOperStatus = 104;

    // IP_ADAPTER_DNS_SERVER_ADDRESS_XP (x64).
    private const int DnsOffsetNext = 8;
    private const int DnsOffsetSockaddr = 16;

    [LibraryImport(Dll)]
    private static partial uint GetAdaptersAddresses(
        uint family, uint flags, nint reserved, nint adapterAddresses, ref uint sizePointer);

    /// <summary>
    /// Every resolver declared by an adapter that is up, in the order Windows lists them, without
    /// duplicates and without loopback.
    ///
    /// Loopback is dropped because it is either this service from a previous run that did not clean
    /// up, or another DNS proxy - and forwarding to either would build a loop. A machine whose only
    /// resolver is a loopback address therefore comes back empty, and the caller refuses to enable
    /// rather than taking the machine's DNS down.
    /// </summary>
    public static List<IPAddress> Read()
    {
        var servers = new List<IPAddress>();

        uint size = 16 * 1024;
        var buffer = Marshal.AllocHGlobal((int)size);

        try
        {
            var flags = SkipUnicast | SkipAnycast | SkipMulticast | SkipFriendlyName;
            var result = GetAdaptersAddresses(AfUnspec, flags, 0, buffer, ref size);

            if (result == ErrorBufferOverflow)
            {
                Marshal.FreeHGlobal(buffer);
                buffer = Marshal.AllocHGlobal((int)size);
                result = GetAdaptersAddresses(AfUnspec, flags, 0, buffer, ref size);
            }

            if (result != IpHelper.NoError) return servers;

            for (var adapter = buffer; adapter != 0; adapter = Marshal.ReadIntPtr(adapter + OffsetNext))
            {
                if (Marshal.ReadInt32(adapter + OffsetOperStatus) != IfOperStatusUp) continue;

                for (var entry = Marshal.ReadIntPtr(adapter + OffsetFirstDnsServer);
                     entry != 0;
                     entry = Marshal.ReadIntPtr(entry + DnsOffsetNext))
                {
                    var sockaddr = Marshal.ReadIntPtr(entry + DnsOffsetSockaddr);
                    if (sockaddr == 0) continue;

                    var address = ReadSockaddr(sockaddr);
                    if (address is null) continue;
                    if (IPAddress.IsLoopback(address)) continue;
                    if (IsWindowsPlaceholder(address)) continue;
                    if (servers.Contains(address)) continue;

                    servers.Add(address);
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return servers;
    }

    /// <summary>
    /// The three site-local addresses Windows lists on every machine whether or not anything is
    /// there - fec0:0:0:ffff::1 through ::3.
    ///
    /// They are a leftover default from an IPv6 addressing scheme deprecated in 2004, and nothing
    /// answers on them on a normal home line. Kept out because the forwarder tries its upstreams in
    /// order and would otherwise spend two seconds each on three dead addresses before admitting a
    /// query failed - turning a resolver outage into a six-second one.
    /// </summary>
    private static bool IsWindowsPlaceholder(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return false;

        var bytes = address.GetAddressBytes();
        return bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0xC0;   // fec0::/10, site-local
    }

    private static IPAddress? ReadSockaddr(nint sockaddr)
    {
        var family = (ushort)Marshal.ReadInt16(sockaddr);

        switch ((AddressFamily)family)
        {
            case AddressFamily.InterNetwork:
            {
                var bytes = new byte[4];
                Marshal.Copy(sockaddr + 4, bytes, 0, 4);      // sin_family(2) + sin_port(2)
                return new IPAddress(bytes);
            }

            case AddressFamily.InterNetworkV6:
            {
                var bytes = new byte[16];
                Marshal.Copy(sockaddr + 8, bytes, 0, 16);     // family(2) + port(2) + flowinfo(4)
                var scope = (uint)Marshal.ReadInt32(sockaddr + 24);
                return new IPAddress(bytes, scope);
            }

            default:
                return null;
        }
    }
}
