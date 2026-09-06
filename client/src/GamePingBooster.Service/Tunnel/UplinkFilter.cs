using System.Buffers.Binary;
using System.Net;

namespace GamePingBooster.Service.Tunnel;

/// <summary>
/// Decides which packets Windows pushes into the virtual adapter are worth putting on the wire.
///
/// Routes only cover game server prefixes, so it is tempting to assume everything arriving at the
/// adapter is game traffic. It is not. Windows sends link-local discovery out of <b>every</b>
/// interface regardless of the routing table, so a freshly connected tunnel carries mDNS, SSDP,
/// LLMNR, IGMP, NetBIOS name broadcasts and IPv6 router solicitations before the game has even
/// started. A real session log showed 83 packets up and 0 down, every one of them this.
///
/// Three reasons to stop them here:
///
///   - They cannot work. The tunnel is IPv4 and point-to-point; a broadcast has nowhere to go at
///     the far end, and an IPv6 packet has no path at all. They were being encapsulated, sent to
///     the VPS, and thrown away there.
///   - They are the player's local network on somebody else's machine. A NetBIOS or mDNS
///     broadcast carries the hostname; forwarding it to a VPS abroad is not something anybody
///     asked for, and it sits badly with a page that says the relays carry game traffic.
///   - They cost uplink and a NAT entry per destination, for nothing.
///
/// Dropping them changes nothing about discovery on the player's real network: Windows still
/// sends all of this on the physical adapter, which is where it belongs.
/// </summary>
internal static class UplinkFilter
{
    /// <summary>Everything from 224.0.0.0 to 239.255.255.255 - all IPv4 multicast.</summary>
    private const uint MulticastNetwork = 0xE0000000;
    private const uint MulticastMask = 0xF0000000;

    /// <summary>169.254.0.0/16, the address a machine gives itself when DHCP fails.</summary>
    private const uint LinkLocalNetwork = 0xA9FE0000;
    private const uint LinkLocalMask = 0xFFFF0000;

    private const uint LimitedBroadcast = 0xFFFFFFFF;

    /// <summary>
    /// The tunnel's own subnet broadcast, or 0 before a session exists.
    ///
    /// The adapter is always configured as a /24 (see TunnelEngine's ConfigureAdapter calls), so
    /// for a client at 10.77.0.252 this is 10.77.0.255 - the address Windows aims NetBIOS at.
    /// </summary>
    public static uint SubnetBroadcastFor(IPAddress clientIp)
    {
        var bytes = clientIp.GetAddressBytes();
        if (bytes.Length != 4) return 0;
        return BinaryPrimitives.ReadUInt32BigEndian(bytes) | 0x000000FF;
    }

    /// <summary>
    /// True when the packet is local noise and must not be sent.
    ///
    /// Reads at most the first 20 bytes and never allocates: this runs on the uplink thread for
    /// every packet, in front of the game's traffic.
    /// </summary>
    public static bool IsLocalNoise(ReadOnlySpan<byte> packet, uint subnetBroadcast)
    {
        // Too short to be an IPv4 header. Nothing downstream could make sense of it either.
        if (packet.Length < 20) return true;

        // IPv6 (and anything else that is not 4). The tunnel carries IPv4 only, so these were
        // never going to arrive - they were being paid for and then discarded at the relay.
        if ((packet[0] >> 4) != 4) return true;

        var destination = BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(16, 4));

        if ((destination & MulticastMask) == MulticastNetwork) return true;
        if (destination == LimitedBroadcast) return true;
        if ((destination & LinkLocalMask) == LinkLocalNetwork) return true;
        if (subnetBroadcast != 0 && destination == subnetBroadcast) return true;

        return false;
    }
}
