# Multi-tunnel: one path per game region

An architecture overview of region routing: why a game's regions may leave by different relays, what keeps
that safe, and how the pieces fit. It describes the design, not every line of it - the code under
`client/src/GamePingBooster.Core/Paths`, `Core/Net/InnerNat.cs` and `Service/Tunnel` is the reference.

Section numbers are stable: the code cites them.

## 1. The problem

The client measures relays once, at connect, against **one** region - the one whose landmark answers fastest
over the player's own line, on the assumption that the game picks its region the same way - and opens **one**
tunnel that carries every region of the game.

That holds for PUBG. It does not hold for games whose matchmaker places the same player in different regions
from one match to the next:

- **Delta Force** plays on Ho Chi Minh City, Hong Kong, Singapore, Jakarta and Bangkok servers.
- **Naraka: Bladepoint** plays in Tokyo and Ho Chi Minh City.
- **Domestic servers need a relay too.** A Vietnamese line can reach a Ho Chi Minh City server through Hong
  Kong - 20 ms becomes 60-80 - and a Hong Kong relay chosen for the Hong Kong landmark then carries the HCM
  matches at 57-96 ms where a Vietnamese relay would give about 22.

So the best relay for one region is routinely the wrong one for the next match.

## 2. Why it can be solved at all

A game server learns the player's address from the **first packet** of a flow. Until then the path that packet
takes can be chosen freely; what must never happen is the exit address changing **after** it - which is why a
relay switch drops a match and entry switching (same relay, same exit) does not.

A match lives on one server, and a server lives in one region. So:

> Route each region through its own best path, decided **before** the region's first packet, and never move a
> destination that is in use.

The client does not react to "the match was found in Hong Kong": the plan is in place before matchmaking, and
the first packet to a Hong Kong address takes the Hong Kong path by its destination alone.

Two observations make it safe:

- **Servers do not bind a match to the lobby's address.** Games whose lobby ran over the player's own line and
  whose match ran through a relay have always worked; a match on a different relay from the lobby is the same
  situation.
- **A game can talk to a server before playing on it** (Naraka trickles a packet a second from the lobby to the
  server it later plays on). Whatever carried the trickle must carry the match - hence sticky destinations (5.3).

## 3. The guarantees

| # | Guarantee | Enforced by |
|---|---|---|
| G1 | The exit address of a destination in use never changes. | Sticky destinations (5.3); a tunnel stays open while anything is stuck to it. |
| G2 | No region gets a path measured slower than home, and a region leaves home only when another path beats it by max(5 ms, 10%). | Planner rules 3-4 and 7 (5.5). |
| G3 | A region that cannot be measured fairly stays on home - exactly the single-tunnel behaviour. | Planner rule 1. |
| G4 | With region routing off, the client behaves as a single-tunnel client. | No dispatcher exists until a plan leaves home. |
| G5 | Never two paths into one relayd: a second handshake to a relay in use would steal its session. | The planner chooses relays, not ways into them; a relay with a tunnel open is measured through that tunnel. |
| G6 | A tunnel that dies takes only its own regions home; the others carry on. | Per-tunnel supervision (5.7). |
| G7 | Three ways back to one tunnel without a release: config.json, the game's served mode, and an automatic collapse on any fault in the dispatcher. | 8.1, 7.1, 5.9. |
| G8 | "Direct" is chosen only when the game allows it and it beats home and the best usable relay by the same margin. | Planner rule 5. |
| G9 | Connect is not slower: home is chosen exactly as before, everything else is measured afterwards. | 5.5. |

Put together: **for every region, the path with the lowest measured end-to-end latency to its landmark, chosen
by a margin noise cannot cross, and never at the price of a match in progress.** Each guarantee has checks in
PathCheck or TunnelCheck (section 10).

## 4. Terms

- **Region** - a profile region: address ranges and landmarks. The unit a path is chosen for.
- **Path** - how a region's packets leave: `home`, another relay, or `direct` (not routed).
- **Home tunnel** - the tunnel connect opens. Carries the lobby, every region not moved, and anything routed that
  matches no region.
- **Secondary** - a tunnel to another relay, opened for the regions a plan sends there. At most `MaxTunnels` (3)
  tunnels in all, one per relay.
- **Carrier** (active tunnel) - the tunnel carrying the match right now (5.8).
- **Stuck destination** - an address that has carried a packet in either direction within 120 s, and the tunnel
  it is stuck to.

## 5. Design

### 5.1 The data plane

```
   Windows routes: every region's ranges + lobby /32s  ->  one Wintun adapter
                                   |
                AdapterPump - the ONE reader of the adapter's ring
                                   |
          stuck destination?  -> that tunnel
          else region -> plan -> tunnel (home when the plan says nothing)
                                   |
          rewrite the source to that tunnel's inner address, if it differs
                                   |
   each tunnel: its socket to its relay, its own downlink thread writing back to the adapter
```

One uplink reader, one downlink thread per tunnel - Wintun's ring calls are thread-safe. The per-packet cost is
a dictionary lookup and, for a new destination, a binary search over the region table; a game sends 50-150
packets a second. The one real coupling is that the ring delivers in allocation order, so a downlink thread
descheduled between allocating and sending holds the others back for that moment; it is measured in-process
and not visible at three tunnels.

### 5.2 The inner address, and why the adapter keeps one

Every relay hands out its own inner address and drops packets from any other source. The adapter has one
address: the home tunnel's at connect, **fixed for the life of the connection**. A tunnel whose relay gave a
different address rewrites the source going up and the destination coming down - a one-address NAT with
incremental checksums, including the original header quoted inside ICMP errors (path-MTU discovery and "port
unreachable" depend on it). Anything malformed is dropped, never half-rewritten.

Because the address is fixed, a reconnect of home no longer re-addresses the adapter while secondaries exist:
the new home rewrites instead, so sockets on the other tunnels keep working. A secondary whose MTU is below the
adapter's is not used.

### 5.3 Sticky destinations

Per destination, the dispatcher remembers the tunnel its last packet used and when a packet last passed either
way. While a destination is stuck, **every packet to it takes the same tunnel, whatever the plan now says.**

That one rule makes every plan change safe at any moment - servers already talking keep their tunnel, new ones
follow the plan, and no timing heuristic stands between a remap and a dropped match. A destination moves only
when its tunnel is gone. After 120 s of silence both ways it is released.

### 5.4 Routes

The routing table does not change when the plan does: every region's ranges and the lobby addresses point at
the one adapter, and moving a region is a reference swap in the dispatcher. Each secondary's relay is pinned to
the physical adapter, like home's. While home reconnects, its routes are out; destinations stuck to secondaries
are kept on the adapter by /32 routes until home is back.

A game whose regions overlap has no single answer for an address; it runs on one tunnel and the log names the
pair. The server refuses such profiles (7.1); this is the backstop.

### 5.5 Deciding: the planner

The planner is a pure function of what one measuring pass found: per region, the median of 8 echoes to its
landmark (6 answered) through home, through each other relay that carries the game or is set to carry a region of
it only (7.1), and over the player's own line - the same instrument on every path.

1. **Unmeasurable stays home** - no landmark, or no number through home. (G3)
2. Candidates are relays with a number for the region.
3. **Best relay** - the lowest median; ties by profile order.
4. **Leave home only by the margin**, max(5 ms, 10%). (G2)
5. **Direct** only if the game allows it and it beats the chosen path by the margin. (G8)
6. **Cap** - at most `MaxTunnels - 1` relays besides home; over it, the relays saving the most are kept.
7. **Hysteresis** - a region keeps its current path unless the new choice beats that path by the margin, and
   only while that path is still no slower than home.

When it runs:

- **Never at connect.** (G9)
- **In the lobby after connect.** At the first supervisor pass (about 5 s) if the game has sent no UDP since its
  routes went in; otherwise once its UDP has stayed under 3 packets a second for two passes (a lobby trickle,
  a connect mid-match).
- **After every match**, in `on`: the whole pass again, the plan in force passed in for hysteresis. It replaces
  the between-matches move of home; in `record` that move stays.
- **Never during a match.** A pass stops when match traffic starts, or when home goes quiet, and is retried.
- **Applied at once** - safe at any moment because of 5.3.

Measurement never handshakes a relay that has a tunnel open (G5) - it echoes through that tunnel. Other relays
are measured in parallel, the ways into one relay one after another, within a 40 s budget. Every pass is logged
number by number and uploaded as a `regionPlan` quality record, without addresses.

### 5.6 Measurement rules

- Never handshake a relay that has a tunnel open; never probe another way into it by handshake.
- The same instrument on every path compared - a median is never compared with a best-of.
- Several echoes may be in flight through one tunnel at once (the in-game ping and a planner pass), each matched
  by its own id and sequence.
- Landmarks are never inside a routed range, so a "direct" echo cannot fall into the tunnel. Games whose
  landmarks are routed (Steam Datagram Relay) have region routing off.

### 5.7 Failure: one tunnel at a time

The supervisor is still the only place a tunnel is opened, moved, replaced or closed.

- **A secondary silent 15 s** - its regions go home, its stuck destinations are released, it is closed without a
  Disconnect. The match on it is lost, as a dying relay loses one on a single tunnel. A later plan may reopen it.
- **Home silent** - the usual reconnect, for home only; secondaries carry on (5.4). A failover never lands on a
  relay a secondary is using.
- **A secondary no region needs** closes once nothing is stuck to it.
- **A game change or a disconnect** tears every secondary down.

### 5.8 What follows the carrier

The carrier is home until another tunnel carries at least 5 game packets a second for 3 s, and more than the
carrier. A stall never moves it - a stall is what the spike recorder must record on the right tunnel. It returns
home after 60 s without a match on it, or at once when its tunnel closes.

| Follows the carrier | Notes |
|---|---|
| In-game ping | probes the match server through the carrier; the estimate uses a per-tunnel second leg |
| Spike recorder, match summary | records say `"carried": "home"` or `"other"` while region routing is in force |
| App status | relay name, pings and loss are the carrier's; `homeRelayName` and `regionPaths` list the rest |

Summed over every tunnel instead: match detection, the between-matches trigger, discovery gating, packet
counters. Entry switching stays on home (11).

### 5.9 Faults inside the dispatcher

Any unexpected exception on the packet path collapses the connection to one tunnel: every region back to home in
one swap, secondaries closed as their stuck destinations go quiet, the reason logged. Region routing stays off
until the next connect - a bug in this code costs its benefit, not the player's match.

## 6. Per game

| Game | Mode | Why |
|---|---|---|
| Delta Force | record, then on first | five regions, each with a landmark |
| Naraka | record | Tokyo and Ho Chi Minh City |
| PUBG | record | the game picks its region by probing; multi-tunnel helps only a player placed elsewhere |
| LoL, TFT, WoT, Apex | record | one region each; nothing to choose until a second exists |
| VALORANT | off | Riot Direct answers every region on the same addresses |
| CS2 | off | its landmarks are its relays, and are routed |

## 7. Server side

### 7.1 Profile

- Each game carries `regionRouting` (`off` | `record` | `on`) and `regionDirect`. Read at connect, never
  mid-match; absent reads as `off` and no direct. A Steam Datagram Relay game is always `off`.
- Each relay carries `games` (the games it may carry - home, failover, the app's relay list) and
  `secondaryGames`: games it may carry **one region** of through a second tunnel and nothing else. Such a relay is
  measured by the planner and may be opened as a secondary, but never becomes home for that game - a Hong Kong
  relay for a game whose Ho Chi Minh City matches must not leave through Hong Kong. Served only beside a non-empty
  `games` that does not list the game.
- A game's regions never overlap, and a profile never routes a relay or a landmark.
- Quality: a `regionPlan` record per pass and `carried` on match summaries and spikes - the field evidence
  for G2 (10.4).

### 7.2 Relay

- The relay reports the client id of each session (`client_id`), so one installation's sessions on several
  relays are recognised as one client rather than a shared key.
- No wire change: multi-tunnel is several ordinary sessions on different relays. Watch session counts against
  each relay's client cap as `on` spreads.

## 8. Configuration

### 8.1 Client

`%ProgramData%\GamePingBooster\config.json`:

- `regionRouting`: `off` | `record` | `on` - wins over the profile. Unset by default and never written unset.
  `record` measures and uploads what `on` would do and moves nothing; `on` puts the plan in force.
- For testing one machine only, both logged and recorded as such: `regionRoutingRelays` (relays a secondary may
  use though the game's list lacks them - never home) and `regionRoutingForce` (region -> relay, overriding the
  planner, including G2).

## 9. Rollout

| Phase | What ships | Exit criteria |
|---|---|---|
| A | The data plane with one tunnel: single reader, per-tunnel downlinks | a week of soak with in-game ping and spikes unchanged |
| B | Profile fields served; relays report client ids | client ids on live sessions |
| C | The planner in `record` for every game but CS2 and VALORANT | a week of `regionPlan` records: how often a region would leave home, by how much |
| D | `on` via config.json on one PC | matches in two regions in one session carried as planned; no reconnect caused by the dispatcher |
| E | `on` for Delta Force in the served profile | 10.4 holds for a week |
| F | `regionDirect` for one game, record first | direct chosen only where the in-match probe agrees |

Every phase is undone by config.json or the game's served mode, without a release.

## 10. Tests

### 10.1 PathCheck

Pure logic, no network: the inner-address NAT against an independent checksum implementation over random
packets of every kind; the region table against a brute-force scan; sticky destinations; the planner rule by
rule and as properties over random inputs (G2, G3, G8, the cap, determinism, no flapping on noise); the lobby
rule; the carrier rules, including random traffic on three tunnels.

### 10.2 TunnelCheck

The real pump, dispatcher and tunnels against in-process fake relays that keep relayd's rules (session resume,
anti-spoofing, roaming) and a fake adapter that catches any use after its session ends. Single-tunnel scenarios
hold the refactored data plane to the old one byte for byte; multi-tunnel scenarios cover two regions on two
relays, remapping under load, a relay dying, a dispatcher fault, ICMP errors through another relay, home being
replaced, concurrent echoes, the recorder following the match, and downlink coupling at three tunnels.

### 10.3 Existing suites

ProtocolCheck and QualityCheck, and the relay's Go tests (including the client-id report).

### 10.4 In the field

"Not worse" is measured per game and week from match summaries - in-game ping (p50/p95), spikes per match,
reconnects per hour - `on` against `record`. A region whose planned path measured faster but played slower more
than a third of the time has a landmark that does not stand for its servers, and goes back to home.

## 11. Not built, and risks

- **Direct.** The planner can choose it; the client keeps such a region on home and says so.
- **Entry switching on a secondary.** A secondary keeps the way into its relay that it opened on; only home moves
  between ways.
- **A secondary dying while home reconnects** is noticed after the reconnect.
- **ICMP is not the game's UDP** - some routes treat them differently; 10.4 checks plans against the real server.
- **A landmark stands for a region only as well as it sits next to its servers** - within ~2 ms of them from
  every relay.
- **Routing may influence matchmaking.** A game that picks its region by pinging servers inside routed ranges
  could place the player differently once a region's path changes. Unconfirmed; watched in the field.
- **Idle secondaries cost a relay slot and a keepalive a second**; closed when no region needs them.
- **Anti-cheat.** Nothing touches the game: routes, one adapter, and packets rewritten after they leave Windows'
  stack, as a home router does.
- Open: whether `MaxTunnels` should be 2.
