# GPB Tunnel Protocol v1

The protocol between the **Windows client** and the **Linux relay**. This document is the single
source of truth - any change has to be made here, in `relay/internal/protocol/protocol.go`, and
in `client/src/GamePingBooster.Core/Protocol/` together.

## Principles

- Runs over **UDP**: one socket on the client, one listening port on the relay.
- The client sends **whole IPv4 packets** as read from the Wintun adapter; the relay writes them
  into its own TUN device and lets the Linux kernel handle NAT and forwarding. Layer 4 is never
  parsed in userspace.
- The **handshake is authenticated with HMAC-SHA256** over a pre-shared key, so the relay cannot
  be used as an open proxy. Data packets are **not encrypted** in v1 - see the security section
  of `docs/ARCHITECTURE.md` for the reasoning and the upgrade path.
- Fixed headers, no TLV. The goal is the smallest possible per-packet overhead.

## The first byte

```
bits 7..4 : version  (currently 1)
bits 3..0 : message type
```

| Type | Name           | Direction       |
|------|----------------|-----------------|
| 0x1  | HandshakeReq   | client -> relay |
| 0x2  | HandshakeResp  | relay -> client |
| 0x3  | Data           | both ways       |
| 0x4  | Ping           | client -> relay |
| 0x5  | Pong           | relay -> client |
| 0x6  | Disconnect     | client -> relay |

So the first byte is `0x11` for HandshakeReq, `0x13` for Data, and so on.

## HandshakeReq - 49 bytes

```
off  len  field
0    1    header (0x11)
1    8    client nonce (random)
9    8    unix timestamp in seconds, big-endian
17   32   HMAC-SHA256(psk, bytes[0..17))
```

The relay rejects the packet if `|now - timestamp| > 120s` (a coarse replay guard) or if the
HMAC does not verify. Rejection is **silent** - no reply is sent, so the relay cannot be used as
a scanning oracle.

## HandshakeResp - 52 bytes

```
off  len  field
0    1    header (0x12)
1    1    status (0 = OK, 1 = address pool full, 2 = server shutting down)
2    8    session id (random, assigned by the relay)
10   4    inner IPv4 assigned to the client (e.g. 10.77.0.5)
14   4    inner IPv4 of the relay, i.e. the gateway (e.g. 10.77.0.1)
18   2    recommended MTU for the virtual adapter, big-endian (e.g. 1400)
20   32   HMAC-SHA256(psk, bytes[0..20))
```

The client must verify the HMAC before trusting any field.

## Data - 9-byte header plus payload

```
off  len  field
0    1    header (0x13)
1    8    session id
9    N    a whole IPv4 packet (starts with nibble 0x4)
```

The relay checks that the **inner source address matches the address it assigned** to that
session, and drops the packet otherwise. This stops one client spoofing another's address in
order to receive their return traffic.

The client's UDP source address is **updated on every valid Data packet**, so the tunnel
survives the ISP's NAT changing the port, or the user moving between Wi-Fi and Ethernet. This is
the same roaming behaviour WireGuard has.

## Ping / Pong - 17 bytes

```
off  len  field
0    1    header (0x14 or 0x15)
1    8    session id
9    8    client timestamp (uint64, client's own tick unit; the relay only echoes it)
```

Three jobs: measure client-to-relay RTT, keep the NAT mapping alive while the player sits in a
lobby with no game traffic flowing, and detect dead sessions. The relay considers a session dead
after 90 seconds without a packet.

## Disconnect - 9 bytes

```
off  len  field
0    1    header (0x16)
1    8    session id
```

Best-effort. The relay releases the session immediately; if the packet is lost, the idle timeout
takes care of it.

## MTU arithmetic

```
outer IPv4 (20) + UDP (8) + GPB Data header (9) = 37 bytes of overhead
```

With a path MTU of 1500 the safe virtual-adapter MTU is **1400** - deliberately below the
1500 - 37 = 1463 the arithmetic allows, to leave room for PPPoE (1492) and ISPs that shave off a
little more. TCP inside the tunnel is handled by MSS clamping on the relay
(`iptables --clamp-mss-to-pmtu`); see `relay/deploy/setup-nat.sh`.
