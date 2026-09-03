# GPB Tunnel Protocol v3 - DESIGN, not yet implemented

Status: **proposal awaiting implementation.** `docs/PROTOCOL.md` describes v2, which is what the
code does today and what a running relay speaks. Nothing here exists in `protocol.go` or
`GpbProtocol.cs` yet. When v3 ships, this file is merged into `PROTOCOL.md` and deleted.

## What v3 changes, and what it deliberately does not

v3 exists for one reason: **the relay must be able to tell one paying customer from another**,
and refuse everybody else. v2 has a single pre-shared key that every installation carries, which
cannot express "this user, until this date, on this device".

| | v2 | v3 |
|---|---|---|
| Who may connect | anyone holding the one shared key | a signed token naming a user, a device and an expiry |
| Revoking one user | change the key, break everyone | let their token expire, or deny-list them |
| What the relay stores | the shared secret | a **public** key only |
| Self-hosting | the only mode | still supported, unchanged in spirit |
| Data packets | plaintext | **plaintext, unchanged** |

**Data stays plaintext in v3.** This is a decision, not an omission - see
`docs/ARCHITECTURE.md` and HANDOFF section 7. In-game latency measured 80 ms -> 43 ms and held
over a three-hour session; that result is the project's only proven asset and v3 is not allowed
to put it at risk for a benefit no current user needs. Type `0x7` is reserved below so that
encryption can be added later without redesigning the handshake a second time.

## Two authentication modes

A relay is configured for exactly one mode and answers only that one.

| Mode | Flag on relayd | For | Relay holds |
|------|----------------|-----|-------------|
| 0 | `-psk-file` | self-hosted relays | its own shared key |
| 1 | `-licence-key` | the commercial fleet | the licence server's **public** key |

Mode 1 is the point of v3. Mode 0 is kept because self-hosting is a stated product, and because
deleting a working path to replace it with an unproven one is how stable software stops being
stable.

**A mode mismatch is answered with silence**, like any other failed authentication. A relay that
replied "wrong mode" would be telling an unauthenticated scanner that a licensed relay lives
here. The client reports a timeout and says to check that its credentials match the relay, which
is the only case where this happens to a legitimate user.

## The first byte

```
bits 7..4 : version  (3)
bits 3..0 : message type
```

| Type | Name             | Direction       | Change from v2 |
|------|------------------|-----------------|----------------|
| 0x1  | HandshakeReq     | client -> relay | **new layout** |
| 0x2  | HandshakeResp    | relay -> client | **new layout** |
| 0x3  | Data             | both ways       | unchanged |
| 0x4  | Ping             | client -> relay | unchanged |
| 0x5  | Pong             | relay -> client | unchanged |
| 0x6  | Disconnect       | client -> relay | unchanged |
| 0x7  | **reserved**     | -               | **DataEncrypted. Reserved, never sent, never accepted.** |

Reserving `0x7` now costs nothing and means a future encrypted Data path can be introduced
alongside the plaintext one without touching the handshake again.

## The licence token - 150 bytes

Minted by the licence server, opaque to the client, verified by the relay offline.

```
off  len  field
0    1    token format version (0x01)
1    8    user id, big-endian
9    65   device public key, P-256 uncompressed: 0x04 || X(32) || Y(32)
74   8    expiry, unix seconds, big-endian
82   1    tier
83   1    max concurrent sessions (0 = relay default)
84   2    reserved, must be zero
86   64   ECDSA P-256 (r || s) over SHA-256(bytes[0..86)), by the LICENCE key
```

The relay needs no database and makes no network call: it verifies one signature with a public
key it already has, then reads the fields. Two P-256 verifications per handshake is on the order
of 100 microseconds, against a handshake that already costs a round trip.

Token lifetime is short - **24 hours** - so an expired subscription stops working within a day
without the relay knowing anything about subscriptions. The client refreshes at 50% of the
remaining life, so a handshake never carries a nearly-expired token.

## HandshakeReq

### Mode 0, PSK - 58 bytes

```
off  len  field
0    1    header (0x31)
1    1    auth mode = 0
2    8    client nonce (random)
10   8    unix timestamp, seconds, big-endian
18   8    client id
26   32   HMAC-SHA256(psk, bytes[0..26))
```

This is v2 with one byte inserted for the mode.

### Mode 1, licence token - 240 bytes

```
off  len  field
0    1    header (0x31)
1    1    auth mode = 1
2    8    client nonce (random)
10   8    unix timestamp, seconds, big-endian
18   8    client id
26   150  licence token (above)
176  64   ECDSA P-256 (r || s) over SHA-256(bytes[0..176)), by the DEVICE key
```

The device public key is not a separate field: it is inside the token, where the licence server
put it. So the relay checks, in order:

1. the token's signature, against the licence public key
2. `expiry` against its own clock
3. the request's signature, against the device public key the token carries
4. `|now - timestamp| <= 120s`, the same coarse replay guard as v2

Step 3 is what stops a stolen token being useful on its own: the holder must also have the
device private key, which never leaves the machine it was generated on.

Every failure is **silent**. No reply, so the relay is not a scanning oracle and not an
amplifier.

240 bytes is one UDP datagram with room to spare, and it is sent once per connection.

## HandshakeResp

Both layouts carry an **echo of the client's nonce**, which v2 did not. Without it a captured
HandshakeResp can be replayed at a client that is mid-handshake, handing it a session id the
relay has already forgotten - a silent blackhole until the idle timeout. The nonce binds one
answer to one question.

### Mode 0, PSK - 60 bytes

```
off  len  field
0    1    header (0x32)
1    1    status
2    8    session id (random, assigned by the relay)
10   4    inner IPv4 assigned to the client
14   4    inner IPv4 of the relay (the gateway)
18   2    recommended MTU, big-endian
20   8    echo of the client nonce
28   32   HMAC-SHA256(psk, bytes[0..28))
```

### Mode 1, licence token - 92 bytes

```
off  len  field
0    1    header (0x32)
1    1    status
2    8    session id
10   4    inner IPv4 assigned to the client
14   4    inner IPv4 of the relay
18   2    recommended MTU, big-endian
20   8    echo of the client nonce
28   64   ECDSA P-256 (r || s) over SHA-256(bytes[0..28)), by the RELAY key
```

In mode 1 there is no shared secret, so the relay signs with **its own** P-256 key. Its public
key travels in the profile, next to its endpoint - and the profile is fetched over HTTPS from an
authenticated endpoint, so it is a trustworthy carrier. The client must verify this signature
before trusting any field. This is new: in v2 the client could tell a real relay from a forged
answer only because both sides shared a key.

The client knows which mode it used, so it knows which length and which check to expect. There
is no length field and no TLV, in keeping with the rest of the format.

### Status codes

```
0  OK
1  address pool full
2  server shutting down
3  protocol version mismatch
4  credential expired      (mode 1 only)
5  credential revoked      (mode 1 only)
```

4 and 5 are sent **only after the signature verified**, so they tell a legitimate customer why
they were refused without telling a stranger anything. A bad signature stays silent.

## Version mismatch stays parseable by old clients

v2's rule holds and gets stricter: when the relay answers a handshake whose version is not its
own, it replies with status 3, puts the **client's** version in the header, and **emits the
52-byte v2 layout** - not a v3 layout. A v1 or v2 client can only parse what it already knows.
Getting this wrong turns "please update" into a four-attempt timeout that looks like a dead
relay.

## Address reservation is keyed differently in mode 1

v2 keys the inner-address reservation on the client id in the packet. With one shared key, any
client can claim any other client's id and take over its reservation. In self-hosted mode 0 that
stays as it is: everyone there already shares a key and is on the same side.

In mode 1 the relay keys the reservation on **SHA-256(device public key)[0..8)** and ignores the
client id field for that purpose. A device cannot claim another device's address, because it
cannot produce that device's signature.

## Session lifetime

A session is authenticated **once, at handshake**. The relay does not re-check the token while a
session runs, and does not hold a timer against its expiry. This is deliberate: cutting a
customer off in the middle of a match is the worst possible moment, and someone whose
subscription lapsed is refused at their next connect anyway - within 24 hours at most.

The cost of that choice is a session that could in principle be held open forever. So the relay
enforces a **maximum session age of 24 hours**, after which the session is dropped and the
client must handshake again. No real game session is that long.

## Data, Ping/Pong, Disconnect

Unchanged from v2 in every respect except the version nibble, which becomes 3. Data is still a
9-byte header plus a whole IPv4 packet, still plaintext, still checked against the inner source
address the relay assigned, and the client's outer address is still updated on every valid Data
packet so roaming works.

## MTU arithmetic

Unchanged. The handshake grew; Data did not, so the per-packet overhead is the same 37 bytes and
the virtual-adapter MTU stays **1400**.

Worth writing down for whenever `0x7` is implemented: an AES-GCM tag is 16 bytes, and 1400 + 37
+ 16 = 1453 is still under 1500. Encryption would not force an MTU change.

## Cryptographic choices, and why not the obvious ones

**P-256 (ECDSA and, later, ECDH), not Ed25519 and not X25519.** The project rule is standard
library only on both sides. Checked on the .NET 9 reference assemblies actually installed here:
`ECDsa`, `ECDiffieHellman` with `DeriveRawSecretAgreement`, `HKDF` and `AesGcm` are all present;
**`Ed25519` and `X25519` are not**. Choosing them would mean a NuGet dependency in the client,
which is a bigger cost than the ergonomic difference is worth. Go's standard library covers
P-256 on its side.

Signature encoding is the raw 64-byte `r || s` pair, not ASN.1 DER. DER is variable length,
which a fixed-layout format cannot use, and both standard libraries can produce and consume the
raw form.

## Implementation order for stage 2

1. **Prove P-256 agreement between Go and .NET, and commit the proof.** This was done once
   before and the code was never committed, so it had to be redone - do not repeat that. It
   belongs in `testdata/protocol-vectors.json` and the existing checkers, not in a scratch file.
2. `relay/internal/protocol/protocol.go`
3. `client/src/GamePingBooster.Core/Protocol/GpbProtocol.cs`
4. `testdata/protocol-vectors.json` plus new assertions on both sides
5. `relayd`: add `-licence-key`, keep `-psk-file` working unchanged
6. Maximum session age

The acceptance test for stage 2 is not a unit test: **deploy with `-psk-file`, play a match, and
confirm the tunnel still behaves as it does today.** A v3 that passes every test and costs a
millisecond in game has failed.
