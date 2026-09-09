using System.Buffers.Binary;
using System.Net;
using System.Text;

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
