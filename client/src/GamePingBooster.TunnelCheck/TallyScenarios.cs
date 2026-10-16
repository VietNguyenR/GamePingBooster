using System.Buffers.Binary;
using System.Net;
using GamePingBooster.Core.Profiles;
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

    /// <summary>
    /// PUBG's UDP 8081 datacentre probes to Seoul ride routed ranges, and in a lobby they are the only UDP in the tunnel.
    /// On 2026-10-16 they read as the game server and the app said "~109 ms to Korea" in a lobby on a Singapore relay.
    /// </summary>
    private static Task ProbesAreNoGameServer()
    {
        try
        {
            GameServerTally.ProbePort(new GameEntry { Id = "pubg" });
            var tally = new GameServerTally();
            for (var i = 0; i < 12; i++) tally.Note(Packet(17, "20.41.114.247", 8081));
            for (var i = 0; i < 9; i++) tally.Note(Packet(17, "57.155.2.179", 8081));
            Check("The game's own datacentre probes are no game server", tally.PrimaryDestination is null,
                $"{tally.PrimaryDestination}");
            // 2026-10-16: the burst ~60 s after launch, with no matchmaking, became a 30-45 s "match" in Korea.
            Check("  nor a match: they are not game packets", tally.GamePackets == 0 && tally.LastGameAt == 0,
                $"{tally.GamePackets}");
            Check("  and their answers are probes too, coming back from the port",
                GameServerTally.IsProbe(Reply(17, "20.41.114.247", 8081), inbound: true) &&
                !GameServerTally.IsProbe(Reply(17, "20.6.10.20", 7777), inbound: true), "");

            for (var i = 0; i < 5; i++) tally.Note(Packet(17, "20.6.10.20", 7777));
            Check("  and a quiet match beside them counts", tally.GamePackets == 5, $"{tally.GamePackets}");
            Check("  and is the game server",
                IPAddress.Parse("20.6.10.20").Equals(tally.PrimaryDestination), $"{tally.PrimaryDestination}");

            GameServerTally.ProbePort(new GameEntry { Id = "fortnite" });
            var other = new GameServerTally();
            other.Note(Packet(17, "20.41.114.247", 8081));
            Check("A game that declares no probes keeps port 8081",
                IPAddress.Parse("20.41.114.247").Equals(other.PrimaryDestination) && other.GamePackets == 1,
                $"{other.PrimaryDestination}, {other.GamePackets}");
        }
        finally
        {
            GameServerTally.ProbePort(null);
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// A game that plays over TCP (Aniimo: TCP 20000 to Alibaba Singapore, no UDP at all). Its TCP to its own region
    /// ranges is the match - counted in GamePackets and chosen as the game server - and every other TCP stays a lobby.
    /// A game that does not declare it keeps the old rule, so PUBG's lobby can never hold off a rescan.
    /// </summary>
    private static Task TcpGameplayCountsOnlyItsOwnRanges()
    {
        var aniimo = new GameEntry
        {
            Id = "aniimo",
            TcpGameplay = true,
            Regions = [new RegionEntry { Id = "asia-sg", Cidrs = ["47.237.208.0/20", "43.106.7.69/32"] }],
        };
        try
        {
            GameServerTally.TcpGameplay(aniimo);
            var tally = new GameServerTally();
            for (var i = 0; i < 50; i++) tally.Note(Packet(6, "47.253.108.56", 443));
            Check("TCP outside the game's ranges is still a lobby: not counted, no game server",
                tally.GamePackets == 0 && tally.PrimaryDestination is null, $"{tally.GamePackets}, {tally.PrimaryDestination}");

            for (var i = 0; i < 20; i++) tally.Note(Packet(6, "47.237.210.48", 20000));
            for (var i = 0; i < 5; i++) tally.Note(Packet(6, "43.106.7.69", 20000));
            Check("TCP to the game's ranges is the match, /20 and /32 alike", tally.GamePackets == 25 && tally.LastGameAt != 0,
                $"{tally.GamePackets}");
            Check("  and the busiest of them is the game server, though the lobby sent more",
                IPAddress.Parse("47.237.210.48").Equals(tally.PrimaryDestination), $"{tally.PrimaryDestination}");

            GameServerTally.TcpGameplay(new GameEntry { Id = "pubg", Regions = aniimo.Regions });
            var pubg = new GameServerTally();
            for (var i = 0; i < 20; i++) pubg.Note(Packet(6, "47.237.210.48", 20000));
            pubg.Note(Packet(17, "47.237.210.48", 7777));
            Check("A game that does not declare tcpGameplay counts UDP only, even inside its ranges",
                pubg.GamePackets == 1, $"{pubg.GamePackets}");
        }
        finally
        {
            GameServerTally.TcpGameplay(null);
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// The same probes through a live tunnel, both ways: neither the game-packet count nor the cadence the spike recorder
    /// reads may move. On 2026-10-16 PUBG's 8081 burst ~60 s after launch, with no matchmaking, was logged as "a match
    /// started" and uploaded as a 30-45 s match in Korea - two thirds of the "KR" matches after the 10-14 patch.
    /// </summary>
    private static async Task AProbeBurstIsNoMatchStart()
    {
        GameServerTally.ProbePort(new GameEntry { Id = "pubg" });
        try
        {
            using var relay = new FakeRelay(Psk);
            using var rig = new Rig();
            var tunnel = await rig.StartAsync(relay, clientId: 7);
            var inner = U32(tunnel.Session.ClientIp);
            var session = relay.SessionOf(tunnel.SessionId)!;
            tunnel.TakeCadence();

            static byte[] OnPort(byte[] packet, int offset)
            {
                BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(offset), 8081);
                return packet;
            }
            for (var i = 0; i < 20; i++) rig.Device.FromWindows(OnPort(Game(inner, Servers[i % 3], i), 22));
            await WaitUntil(() => relay.Arrivals.Count >= 20);
            for (var i = 0; i < 20; i++) relay.SendToClient(session, OnPort(Game(Servers[i % 3], inner, i), 20));
            await WaitUntil(() => rig.Device.ToWindows.Count >= 20);

            var probes = tunnel.TakeCadence();
            Check("A probe burst still goes through the tunnel, both ways",
                relay.Arrivals.Count == 20 && rig.Device.ToWindows.Count == 20,
                $"{relay.Arrivals.Count} up, {rig.Device.ToWindows.Count} down");
            Check("  but is no match: no game packets, no cadence for the spike recorder",
                tunnel.Destinations.GamePackets == 0 && probes.UpPackets == 0 && probes.DownPackets == 0,
                $"game packets {tunnel.Destinations.GamePackets}, cadence up {probes.UpPackets} down {probes.DownPackets}");

            for (var i = 0; i < 5; i++) rig.Device.FromWindows(Game(inner, Servers[0], 100 + i));
            for (var i = 0; i < 5; i++) relay.SendToClient(session, Game(Servers[0], inner, 100 + i));
            await WaitUntil(() => relay.Arrivals.Count >= 25 && rig.Device.ToWindows.Count >= 25);
            var match = tunnel.TakeCadence();
            Check("  while the match's own packets count",
                tunnel.Destinations.GamePackets == 5 && match.UpPackets == 5 && match.DownPackets == 5,
                $"game packets {tunnel.Destinations.GamePackets}, cadence up {match.UpPackets} down {match.DownPackets}");
        }
        finally
        {
            GameServerTally.ProbePort(null);
        }
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

    /// <summary><see cref="Packet"/> coming back: from <paramref name="source"/>:<paramref name="port"/> to the adapter.</summary>
    private static byte[] Reply(byte protocol, string source, ushort port)
    {
        var packet = Packet(protocol, "10.77.0.2", 50000);
        IPAddress.Parse(source).GetAddressBytes().CopyTo(packet, 12);
        packet[20] = (byte)(port >> 8); packet[21] = (byte)port;
        return packet;
    }
}
