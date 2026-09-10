# Security Policy

## Reporting a vulnerability

**Do not open a public issue.** This project handles authentication, payment and relay
infrastructure, and a public issue is a disclosure before there is a fix.

Two private channels, in order of preference:

1. **[Report a vulnerability](https://github.com/VietNguyenR/GamePingBooster/security/advisories/new)**
   — GitHub's private advisory form. Preferred: it keeps the report, the discussion and the fix in
   one place, and nothing is visible until it is published.
2. **vietnguyen010@gmail.com** — if you cannot use GitHub, or prefer email.

**Tiếng Việt hoàn toàn ổn.**

### What to expect

This is a one-person project, not a company with a security team. Being honest about that is more
useful than promising a turnaround nobody can keep:

| | |
|---|---|
| First reply | Within 5 days |
| Assessment | Within 14 days |
| Fix for something critical | As fast as I can, and I will tell you what "as fast" looks like |
| Credit | Yes, unless you would rather not be named |

If you do not hear back in a week, assume the mail was lost rather than ignored, and send it again.

### What helps

- What you did, what happened, and what should have happened instead.
- A proof of concept if you have one. It does not have to be weaponised — enough to show the
  behaviour is real.
- Which version. `.\gpb.ps1 version`, or the About screen in the app.

## Please do not test against the production relays

This is a network product, and its relays carry live game traffic for people paying to use them.
Load testing, fuzzing or denial-of-service against them takes the service away from real players,
and it will not tell you anything you could not learn from your own relay.

**Run your own instead.** The whole server is in this repository and comes up in a few minutes:

```bash
cd relay && go build ./...
sudo ./deploy/install.sh --psk        # a local relay with a throwaway key
```

There is also a load generator built for exactly this, which never sends a byte outside the
machine it runs on — it bounces ICMP off the relay's own inner address:

```bash
go run ./cmd/gpb-soak -relay 127.0.0.1:51820 -psk-file ./psk -clients 20 -pps 200 -duration 10m
```

## Scope

**In scope** — anything in this repository:

- `relayd` — handshake, authentication, licence token verification, the data plane, rate limiting
- The Windows client — the service, the tunnel, routing, how credentials are stored
- The wire protocol itself (`docs/PROTOCOL-v3.md`)
- The build and release pipeline in `.github/workflows`

**Out of scope**, because it is not this repository's to fix:

- The licence server (`gamepingbooster.com`) — closed source, but reports about it are welcome
  through the same channels
- Third-party components: Wintun, .NET, Go, and the VPS providers the relays run on
- Anything that needs physical access to a machine, or an account that is already compromised
- Social engineering

## Things that look like bugs and are not

Written down so you do not spend an evening on something already decided. Each of these is a
deliberate trade-off with the reasoning next to the code:

- **The tunnel payload is not encrypted.** PUBG already encrypts its own gameplay packets, and a
  second layer would cost latency, which is the one thing this project exists to protect. Packet
  type `0x7` is reserved in the spec for the day that changes.
- **The relay verifies licences offline** against a public key, and calls no API. That is what
  lets a relay be rented, rebuilt or lost without leaking anything. The known cost is a revocation
  window of at most one token lifetime — see below.
- **A cancelled subscription keeps working until its token expires** (`LICENCE_TOKEN_HOURS`,
  24 hours by default). Token expiry is clamped to the end of the subscription period, so a
  subscription that simply runs out has no window at all. A mid-period cancellation does. This is
  known, bounded and documented.
- **The client cannot enforce anything and does not try.** Patching the desktop app is expected to
  be easy; it gets you to a Connect button that cannot connect, because the relay is what checks
  the licence. If you find a way to make the *relay* accept something it should not, that is a
  real finding and I want to hear about it.

## Supported versions

| Version | Supported |
|---|---|
| 0.1.x | ✅ current |
| older | ❌ |

There is one line of development and one supported version: the latest release. Please reproduce
against that before reporting.

## Safe harbour

If you make a good-faith effort to follow this policy — report privately, do not touch the
production relays, do not access or destroy anyone else's data — I will not pursue action against
you, and I will work with you on the fix.
