using System.Net;
using System.Runtime.InteropServices;

namespace GamePingBooster.Service.Native;

/// <summary>
/// P/Invoke into iphlpapi.dll, for the one question the managed API cannot answer: which
/// interface would Windows use to reach a particular address.
///
/// NetworkInterface can enumerate adapters and their gateways, but it cannot rank them - it has
/// no view of the routing table, so "which of these adapters is the way out" has to be guessed
/// from the order the list happens to arrive in. On a machine with one adapter that guess is
/// always right, which is why it survived so long; on a machine with a second one that is Up and
/// carries a gateway - a VM host adapter, WSL, a corporate VPN, Wi-Fi and Ethernet both live -
/// it is a coin toss, and losing it pins the relay to an adapter that cannot reach it. That
/// failure was diagnosed on 2026-09-12: the chosen relay went silent while the other five
/// answered normally, which is the signature of a pin through the wrong door.
/// </summary>
internal static partial class IpHelperInterop
{
    private const string Dll = "iphlpapi.dll";

    private const int NoError = 0;
    private const short AfInet = 2;

    /// <summary>
    /// sockaddr_in, 16 bytes. Blittable on purpose so the source-generated marshalling can pass
    /// it straight through.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct SockAddrIn
    {
        public short Family;
        public ushort Port;

        /// <summary>
        /// The four address bytes in the order they appear on the wire. Held as a uint because
        /// that is what IN_ADDR is; it is a reinterpretation of those bytes, never a number to
        /// do arithmetic on, so no byte swapping belongs here.
        /// </summary>
        public uint Address;

        public long Zero;
    }

    [LibraryImport(Dll, EntryPoint = "GetBestInterfaceEx")]
    private static partial int GetBestInterfaceEx(ref SockAddrIn destination, out uint bestIfIndex);

    /// <summary>
    /// The interface index Windows would send a packet to <paramref name="destination"/> through,
    /// or null when it will not say.
    ///
    /// This reads the live routing table, so it is the same answer <c>Find-NetRoute</c> gives and
    /// the same one the socket layer acts on - not a guess that agrees with it most of the time.
    ///
    /// Null rather than an exception on failure: the caller has a workable fallback, and a
    /// routing question that cannot be answered is not a reason to refuse to connect.
    /// </summary>
    internal static uint? BestInterfaceFor(IPAddress destination)
    {
        if (destination.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return null;

        var bytes = destination.GetAddressBytes();
        var address = new SockAddrIn
        {
            Family = AfInet,
            Port = 0,
            Address = BitConverter.ToUInt32(bytes, 0),
            Zero = 0,
        };

        try
        {
            return GetBestInterfaceEx(ref address, out var index) == NoError ? index : null;
        }
        catch (DllNotFoundException)
        {
            return null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }
}
