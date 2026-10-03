using System.Net;
using GamePingBooster.Service.Tunnel;

namespace GamePingBooster.TunnelCheck;

/// <summary>
/// The address the in-game ping is measured against (GameServerTally.PrimaryDestination). Since lobby routes put the
/// lobby's TCP in the same tunnel, a quiet match could be out-sent by the lobby; on 2026-10-03 PUBG's in-game ping read
/// the lobby front and a US launcher API as "the game server". Only UDP may be chosen.
/// </summary>
internal static partial class Program
{
    private static Task TheGameServerIsTheUdpOne()
    {
        var tally = new GameServerTally();
        for (var i = 0; i < 50; i++) tally.Note(Packet(6, "23.66.150.216", 443));
        Check("Lobby TCP alone is no game server", tally.PrimaryDestination is null, $"{tally.PrimaryDestination}");

        for (var i = 0; i < 5; i++) tally.Note(Packet(17, "34.87.10.20", 7777));
        Check("A quiet match is the game server, though the lobby sent ten times as much",
            IPAddress.Parse("34.87.10.20").Equals(tally.PrimaryDestination), $"{tally.PrimaryDestination}");

        for (var i = 0; i < 20; i++) tally.Note(Packet(17, "34.87.10.21", 7778));
        Check("  and the busiest UDP address wins among several",
            IPAddress.Parse("34.87.10.21").Equals(tally.PrimaryDestination), $"{tally.PrimaryDestination}");
        return Task.CompletedTask;
    }

    /// <summary>A minimal IPv4 packet with a transport header's ports, enough for the tally to read.</summary>
    private static byte[] Packet(byte protocol, string destination, ushort port)
    {
        var packet = new byte[28];
        packet[0] = 0x45;
        packet[9] = protocol;
        IPAddress.Parse("10.77.0.2").GetAddressBytes().CopyTo(packet, 12);
        IPAddress.Parse(destination).GetAddressBytes().CopyTo(packet, 16);
        packet[20] = 0xC3; packet[21] = 0x50;
        packet[22] = (byte)(port >> 8); packet[23] = (byte)port;
        return packet;
    }
}
