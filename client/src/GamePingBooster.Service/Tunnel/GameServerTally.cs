using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using GamePingBooster.Core.Profiles;

namespace GamePingBooster.Service.Tunnel;

/// <summary>
/// Records which game server addresses the tunnel is actually carrying traffic to.
///
/// This exists to answer one question that no amount of relay-side latency measurement can:
/// <b>does the game put you on a different server depending on which relay you exit from?</b>
/// A tester reported 23ms to the Hong Kong relay, 45ms to Singapore, the app correctly picked
/// Hong Kong on those numbers, and then the in-game ping was 70-80ms. Either the Hong Kong relay
/// has a bad onward path, or matchmaking saw a Hong Kong source address and placed the player on
/// a different datacentre entirely. Those two have completely different fixes, and the
/// destination addresses tell them apart: if the prefix changes when the relay changes, it is
/// matchmaking, and measuring relay-to-server latency would have been wasted work.
///
/// It only sees traffic while the tunnel is up, which is fine - the comparison that matters is
/// Hong Kong versus Singapore, and both go through here. Getting the same reading with the
/// booster off needs a capture outside this process; `pktmon` ships with Windows and can do it.
///
/// Deliberately a summary, not a packet log. One line every 30 seconds naming a handful of
/// addresses costs nothing and is something a tester can paste into a message; a per-packet log
/// would be unreadable, enormous, and would sit on the latency path.
///
/// Only addresses and ports are kept. Nothing here looks at payload, and there is nothing in a
/// destination address that is not already in the player's own routing table.
/// </summary>
internal sealed class GameServerTally
{
    /// <summary>
    /// Distinct destinations tracked before new ones are counted but not named.
    ///
    /// A game talks to a handful of addresses; anything past this is a sign the routes are
    /// pulling in traffic that is not the game, which is worth seeing as a number rather than
    /// letting it grow without limit.
    /// </summary>
    private const int MaxTracked = 64;

    private readonly record struct Key(uint Address, ushort Port, byte Protocol);

    private sealed class Flow
    {
        public long Packets;
        public long Bytes;
        public long FirstSeenTicks;
        public long LastSeenTicks;
    }

    private readonly Dictionary<Key, Flow> _flows = new();
    private readonly object _gate = new();
    private long _untrackedPackets;
    private long _unparsedPackets;
    private long _gamePackets;

    /// <summary>
    /// Packets the game has sent to its servers through this tunnel, since the tunnel was created. Never
    /// cleared, unlike everything else here, so a reader can take differences: destination discovery,
    /// the move between matches and the region planner all read it to tell a match on the tunnel from
    /// one that is not.
    ///
    /// UDP always counts: only game routes carry UDP into the adapter. TCP counts only to the ranges in
    /// <see cref="TcpGameplay"/> - a game that plays over TCP (<see cref="GameEntry.TcpGameplay"/>,
    /// Aniimo). Every other TCP in the adapter is a lobby, a login or an unblocked store, which must
    /// never read as a match: PUBG's lobby would then hold off every rescan for as long as it is open.
    /// </summary>
    public long GamePackets => Interlocked.Read(ref _gamePackets);

    /// <summary>
    /// The ranges whose TCP is a match, as (network, mask) pairs - those of the running game when it
    /// declares <see cref="GameEntry.TcpGameplay"/>, otherwise empty. Shared by every tunnel, because
    /// every tunnel carries the same game; set by the engine whenever it chooses or switches the game.
    /// Swapped whole, never edited, so the uplink thread reads it without a lock.
    /// </summary>
    private static (uint Network, uint Mask)[] s_tcpGameplay = [];

    /// <summary>Makes TCP to <paramref name="game"/>'s regions count as its match, or stops it when the game does not play over TCP.</summary>
    public static void TcpGameplay(GameEntry? game)
    {
        var ranges = new List<(uint, uint)>();
        if (game is { TcpGameplay: true })
        {
            foreach (var cidr in game.Regions.SelectMany(r => r.Cidrs))
            {
                if (!IPNetwork.TryParse(cidr, out var net) || net.BaseAddress.AddressFamily != AddressFamily.InterNetwork) continue;
                var mask = net.PrefixLength == 0 ? 0u : uint.MaxValue << (32 - net.PrefixLength);
                ranges.Add((BinaryPrimitives.ReadUInt32BigEndian(net.BaseAddress.GetAddressBytes()) & mask, mask));
            }
        }
        Volatile.Write(ref s_tcpGameplay, [.. ranges]);
    }

    private static bool IsTcpGameplay(uint destination)
    {
        foreach (var (network, mask) in Volatile.Read(ref s_tcpGameplay))
        {
            if ((destination & mask) == network) return true;
        }
        return false;
    }

    private long _lastGameAt;
    private long _firstGameAt;

    /// <summary>When the game first sent a packet counted in <see cref="GamePackets"/>, as a Stopwatch timestamp, or 0 before it has.</summary>
    public long FirstGameAt => Interlocked.Read(ref _firstGameAt);

    /// <summary>
    /// When the game last sent a packet counted in <see cref="GamePackets"/>, as a Stopwatch timestamp, or 0 before it ever has. The
    /// move between matches reads it for how long the game has been silent to the second, which a counter
    /// read every five seconds cannot say.
    /// </summary>
    public long LastGameAt => Interlocked.Read(ref _lastGameAt);

    /// <summary>
    /// Notes one outbound inner packet. Called from the uplink thread for every packet, so it
    /// stays cheap: a fixed-size header read and a dictionary lookup.
    ///
    /// The lock is taken per packet on purpose. Only game-server prefixes are routed into the
    /// adapter, so this runs at a game's packet rate - tens to low hundreds per second - where an
    /// uncontended lock is far below the noise floor, and the alternative is a lock-free
    /// structure that has to be got exactly right for no measurable gain.
    /// </summary>
    public void Note(ReadOnlySpan<byte> packet)
    {
        // IPv4 only. Anything else - IPv6, or a packet too short to hold a header - is counted
        // rather than parsed. The game profile carries v4 prefixes, so a non-zero count here is
        // itself the finding, whichever of the two it turns out to be.
        if (packet.Length < 20 || (packet[0] >> 4) != 4)
        {
            lock (_gate) _unparsedPackets++;
            return;
        }

        var headerLen = (packet[0] & 0x0F) * 4;
        if (headerLen < 20 || packet.Length < headerLen) return;

        var protocol = packet[9];
        var destination = BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(16, 4));
        if (protocol == 17 || (protocol == 6 && IsTcpGameplay(destination)))
        {
            Interlocked.Increment(ref _gamePackets);
            var sentAt = System.Diagnostics.Stopwatch.GetTimestamp();
            Interlocked.Exchange(ref _lastGameAt, sentAt);
            if (Interlocked.Read(ref _firstGameAt) == 0) Interlocked.CompareExchange(ref _firstGameAt, sentAt, 0);
        }

        // Port lives in the transport header, which is only there for UDP and TCP, and only if
        // this is the first fragment. Everything else is recorded on address alone.
        ushort port = 0;
        var fragmented = (BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(6, 2)) & 0x1FFF) != 0;
        if (!fragmented && (protocol == 17 || protocol == 6) && packet.Length >= headerLen + 4)
        {
            port = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(headerLen + 2, 2));
        }

        var key = new Key(destination, port, protocol);
        var now = Environment.TickCount64;

        lock (_gate)
        {
            if (!_flows.TryGetValue(key, out var flow))
            {
                if (_flows.Count >= MaxTracked)
                {
                    _untrackedPackets++;
                    return;
                }
                flow = new Flow { FirstSeenTicks = now };
                _flows[key] = flow;
            }

            flow.Packets++;
            flow.Bytes += packet.Length;
            flow.LastSeenTicks = now;
        }
    }

    /// <summary>Whether anything has been seen since the last <see cref="Format"/>.</summary>
    public bool HasTraffic
    {
        get { lock (_gate) return _flows.Count > 0; }
    }

    /// <summary>
    /// The address the game is sending most of its packets to, or null when nothing has been
    /// seen since the last <see cref="Format"/>.
    ///
    /// This is the address the in-game ping is measured against. It is known here and nowhere
    /// else: the game picks its match server at runtime, tells nobody, and this tally is already
    /// reading the destination out of every outbound packet for the log line. Measuring to a
    /// landmark instead was an estimate standing in for exactly this.
    ///
    /// Packets are summed per ADDRESS, not per flow. A server is talked to on more than one port
    /// over a session - 22361 then 26524 on the same host, in the 2026-09-10 capture - and three
    /// flows to one server must not lose to one flow somewhere else.
    ///
    /// UDP, and TCP only to <see cref="TcpGameplay"/>'s ranges. The lobby's TCP rides the same
    /// tunnel since lobby routes: on 2026-10-03 PUBG's lobby out-sent a quiet match, and the
    /// in-game ping read the lobby front (80-90 ms) and the launcher API in the US (about 290 ms)
    /// as "the game server". Null while only lobby TCP is seen - in a lobby there is no match to
    /// measure. A game that plays over TCP (Aniimo) declares it, and only its region ranges count.
    ///
    /// Unlike <see cref="Format"/> this does not clear. It is read once a second, and a reader
    /// that emptied the tally would blind the 30-second log line that shares it.
    /// </summary>
    public IPAddress? PrimaryDestination
    {
        get
        {
            lock (_gate)
            {
                if (_flows.Count == 0) return null;

                var byAddress = new Dictionary<uint, long>();
                foreach (var (key, flow) in _flows)
                {
                    if (key.Protocol != 17 && !(key.Protocol == 6 && IsTcpGameplay(key.Address))) continue;
                    byAddress.TryGetValue(key.Address, out var running);
                    byAddress[key.Address] = running + flow.Packets;
                }

                uint best = 0;
                long bestPackets = -1;
                foreach (var (address, packets) in byAddress)
                {
                    if (packets <= bestPackets) continue;
                    bestPackets = packets;
                    best = address;
                }

                return bestPackets < 0 ? null : new IPAddress(BinaryPrimitives.ReverseEndianness(best));
            }
        }
    }

    /// <summary>
    /// Renders the summary and clears it, so each line covers one interval rather than repeating
    /// a running total that gets harder to read the longer a session lasts.
    ///
    /// <paramref name="context"/> names the relay in use. It is the whole point of the line: two
    /// of these from the same tester, one per relay, is the experiment.
    /// </summary>
    public string Format(string context, int top = 8)
    {
        lock (_gate)
        {
            if (_flows.Count == 0 && _unparsedPackets == 0)
            {
                return $"Game destinations via {context}: none seen.";
            }

            var ordered = _flows
                .OrderByDescending(entry => entry.Value.Packets)
                .Take(top)
                .ToList();

            var builder = new StringBuilder();
            builder.Append("Game destinations via ").Append(context)
                   .Append(" - ").Append(_flows.Count).Append(" distinct:");

            foreach (var (key, flow) in ordered)
            {
                var address = new IPAddress(BinaryPrimitives.ReverseEndianness(key.Address));
                builder.Append("\n    ").Append("***");
                if (key.Port != 0) builder.Append(':').Append(key.Port);
                builder.Append('/').Append(ProtocolName(key.Protocol))
                       .Append("  ").Append(flow.Packets).Append(" pkt, ")
                       .Append((flow.Bytes / 1024.0).ToString("F1")).Append(" KiB, ")
                       .Append((flow.LastSeenTicks - flow.FirstSeenTicks) / 1000).Append("s active");
            }

            if (_flows.Count > ordered.Count)
            {
                builder.Append("\n    (").Append(_flows.Count - ordered.Count).Append(" more)");
            }
            if (_untrackedPackets > 0)
            {
                builder.Append("\n    ").Append(_untrackedPackets)
                       .Append(" packets to addresses past the ").Append(MaxTracked)
                       .Append(" tracked - the routes may be pulling in more than the game");
            }
            if (_unparsedPackets > 0)
            {
                builder.Append("\n    ").Append(_unparsedPackets)
                       .Append(" packets not readable as IPv4 (v6, or too short)");
            }

            _flows.Clear();
            _untrackedPackets = 0;
            _unparsedPackets = 0;
            return builder.ToString();
        }
    }

    private static string ProtocolName(byte protocol) => protocol switch
    {
        1 => "icmp",
        6 => "tcp",
        17 => "udp",
        _ => protocol.ToString(),
    };
}
