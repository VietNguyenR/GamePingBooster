# GPB Tunnel Protocol v2

The protocol between the **Windows client** and the **Linux relay**. This document is the single
source of truth - any change has to be made here, in `relay/internal/protocol/protocol.go`, and
in `client/src/GamePingBooster.Core/Protocol/` together.

## Principles

- Runs over **UDP**: one socket on the client, one listening port on the relay.
- The client sends **whole IPv4 packets** as read from the Wintun adapter; the relay writes them
  into its own TUN device and lets the Linux kernel handle NAT and forwarding. Layer 4 is never
  parsed in userspace.
- The **handshake is authenticated with HMAC-SHA256** over a pre-shared key, so the relay cannot
  be used as an open proxy. Data packets are **not encrypted** - see the security section
  of `docs/ARCHITECTURE.md` for the reasoning and the upgrade path.
- Fixed headers, no TLV. The goal is the smallest possible per-packet overhead.

## The first byte

```
bits 7..4 : version  (currently 2)
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

So the first byte is `0x21` for HandshakeReq, `0x23` for Data, and so on.

## HandshakeReq - 57 bytes

```
off  len  field
0    1    header (0x21)
1    8    client nonce (random)
9    8    unix timestamp in seconds, big-endian
17   8    client id
25   32   HMAC-SHA256(psk, bytes[0..25))
```

The relay rejects the packet if `|now - timestamp| > 120s` (a coarse replay guard) or if the
HMAC does not verify. Rejection is **silent** - no reply is sent, so the relay cannot be used as
a scanning oracle.

The **client id** is a random 64-bit value generated once per installation. The relay remembers
which inner address each client id last held and hands the same one back on reconnect, so a
client that loses its network for a few seconds does not have to re-address its virtual adapter
and reinstall every route - during the exact moment the network is least reliable. It sits inside
the signed range, so it cannot be swapped in transit; it is not an account or a licence key, and
means nothing outside one relay's session table.

Reservations are a convenience, not a promise. A relay whose pool runs dry releases unused
reservations rather than refusing a live client, and an explicit Disconnect gives the address up
immediately. Only an idle timeout - the case that usually means "came back later" - keeps it.

## HandshakeResp - 52 bytes

```
off  len  field
0    1    header (0x22)
1    1    status (0 = OK, 1 = address pool full, 2 = server shutting down,
                  3 = protocol version mismatch)
2    8    session id (random, assigned by the relay)
10   4    inner IPv4 assigned to the client (e.g. 10.77.0.5)
14   4    inner IPv4 of the relay, i.e. the gateway (e.g. 10.77.0.1)
18   2    recommended MTU for the virtual adapter, big-endian (e.g. 1400)
20   32   HMAC-SHA256(psk, bytes[0..20))
```

The client must verify the HMAC before trusting any field.

**Version mismatch is answered, not ignored.** When the relay receives a handshake whose version
is not its own, it replies with status 3 and puts the **client's** version in the header, not its
own. That is deliberate: a reply the other side cannot parse teaches it nothing, and silence is
indistinguishable from a dead relay or a blocked port. The HandshakeResp layout has not changed
between v1 and v2, so a v1 client parses this and reports a refusal instead of timing out after
four attempts. Clients must therefore read the status byte before rejecting on version.

## Data - 9-byte header plus payload

```
off  len  field
0    1    header (0x23)
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
0    1    header (0x24 or 0x25)
1    8    session id
9    8    client timestamp (uint64, client's own tick unit; the relay only echoes it)
```

Three jobs: measure client-to-relay RTT, keep the NAT mapping alive while the player sits in a
lobby with no game traffic flowing, and detect dead sessions. The relay considers a session dead
after 90 seconds without a packet.

## Disconnect - 9 bytes

```
off  len  field
0    1    header (0x26)
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

## Keeping the two implementations in step

This format has two implementations that must agree byte for byte:

- `relay/internal/protocol/protocol.go`
- `client/src/GamePingBooster.Core/Protocol/GpbProtocol.cs`

Nothing in either build fails when they drift apart. The symptom is not a compile error: it is a
tunnel that handshakes and then carries nothing, or one that misreads a field and hands a
player's traffic to the wrong session - discovered on a player's PC rather than on the machine
where the change was made.

Both sides therefore check themselves against the same committed file of golden packets,
`testdata/protocol-vectors.json`.

Run both after touching either implementation:

```
cd relay && go test ./internal/protocol/
dotnet run --project client/src/GamePingBooster.ProtocolCheck
```

The C# side is a plain console program returning 0 or 1, with no test framework behind it: this
repository carries no NuGet test dependency and a protocol check is a list of assertions.

Regenerate the vectors **only** when a format change is deliberate and the Go side is finished.
A regenerated file makes any drift look correct, which is exactly the failure this guards
against:

```
cd relay && GPB_UPDATE_VECTORS=1 go test ./internal/protocol/ -run TestProtocolVectors
```

So a protocol change now touches **five** places: the two implementations, this document, the
vector file, and whatever new assertions the change deserves on both sides.
