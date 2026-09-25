# Multi-tunnel: one path per game region

Status, 2026-09-25:

- **Phase A built, not yet soaked on real hardware.** The adapter's read moved out of `TunnelClient` into
  `AdapterPump` (one target, the home tunnel); `WintunAdapter` implements `IPacketDevice`; the engine starts
  and swaps tunnels through `StartTunnel` / `StopUplink` only. Proven in-process by
  `GamePingBooster.TunnelCheck` (section 10.2) and a Native AOT publish with no new warnings.
- The multi-tunnel core (`Core/Paths`, `Core/Net/InnerNat.cs`) and `GamePingBooster.PathCheck` exist but are
  not wired in.
- **Phase B, relay side built, not deployed** (2026-09-25): the handshake's client id is kept in the session
  and reported as `client_id` (token mode only), refreshed when a reinstalled client resumes its session.
  Checked by `TestTheReportNamesTheInstallationOfTheLiveHandshake`.
- **Phase C, client side built, not released** (2026-09-26). `RegionRouting.Resolve` (config.json, then the
  game's `regionRouting`, else off; routed landmarks always off) decides; in `record` and `on` alike the
  supervisor runs one measuring pass per connection, game and home relay (`TunnelEngine.RegionPlan.cs`): in
  the lobby - the game's UDP under 3 packets a second for two passes, so a lobby trickle does not block it -
  every region with a landmark is measured through home (live tunnel), over the player's own line (ICMP,
  skipped for a routed landmark) and through every other relay that carries the game, each way in, fastest
  kept; median of 8, 6 answered. `RegionPlanner.Plan` decides, the log shows every number and choice, and a
  `"type":"regionPlan"` quality record carries them - no addresses. **Nothing is moved**; `on` records what
  it would do and says so. The pass stops for a match loading (game UDP past 10 + 3/s) or a home tunnel
  quiet 5 s and is tried again (3 tries), and a 40 s budget ends it as incomplete. It runs on the supervisor
  after the between-matches rescan, never beside it. Checked by PathCheck (mode resolution and the profile
  fields) and TunnelCheck, which holds the record to `testdata/region-plan-record.json` byte for byte.
  Not done yet: `ProbeGameServerAsync`'s single slot (5.6) - a lobby echo that meets the in-game ping loop
  counts as unanswered, so a region can read "no answer through home" and follow home in the record.
  First live pass 2026-09-26 (PUBG, home sg-2): complete in 22 s, match after it unaffected; asia-kr would
  leave for hk-2 (67 ms against 108 through home), asia-sg stays.
- **Phase D1 built, not soaked** (2026-09-26). In `on`, the plan is put in force (`TunnelEngine.MultiTunnel.cs`):
  `PathDispatcher` routes every packet the pump reads - sticky table first (G1), then region table, then
  plan, else home - and rewrites the source for a tunnel whose inner address differs; each tunnel's downlink
  hands it replies to refresh the sticky entry and rewrite back. Created on the first plan that leaves home,
  with the adapter address fixed from then on. Secondaries (at most 2, one per relay, G5) open through the
  way that measured fastest, are pinned (`RouteManager.PinPathRoutes`), measured through their live tunnel by
  later passes, closed once no region needs them and nothing is stuck to them, and dropped without a
  Disconnect after 15 s of silence (their regions go home, G6). A home reconnect keeps the adapter address,
  pins every destination stuck to a secondary as a /32 into the adapter while the ranges are out
  (`PinStuckDestinations`), keeps the pump reading for them, and never fails over to a relay a secondary is
  on. A dispatcher fault collapses to home for the connection (5.9). A game change and disconnect tear it all
  down and re-address the adapter if a reconnect moved home. Rescans between matches are off while it is in
  force; the lobby and match detection count every tunnel's game UDP. TunnelCheck: `TwoRegionsLeaveByTwoRelays`,
  `RemappingNeverMovesAFlowInUse` (1 000 remaps, 3 flows), `ARelayThatDiesSendsOnlyItsOwnHome`,
  `ADispatcherFaultCollapsesToHome`, `AnIcmpErrorReachesWindowsAboutItsOwnPacket`,
  `AReplacedHomeRewritesInsteadOfReaddressing` - 6 deliberate bugs, 6 caught. Native AOT publish clean.
  **Not in D1** (5.8): the in-game ping, spike recorder, match summary, entry switching and the app's status
  follow the home tunnel only - a match on another tunnel shows in the log (`Tunnel via ...` lines) and not
  in the app's ping; `direct` stays home; one plan per connection and home relay, no re-plan after a match;
  a secondary's death is noticed only while home is healthy (the supervisor is inside a home reconnect
  otherwise); `DownlinkCoupling` is not measured yet.
- **Phase D2, first half built** (2026-09-26). In `on`, the end of every match (MatchGap, now fed every
  tunnel's game UDP) runs the planner again instead of the between-matches rescan: every relay for every
  region, relays with a tunnel open measured through it, the plan in force passed in for hysteresis, and the
  new plan applied at once - safe at any moment because servers in use keep their tunnel. Home no longer
  moves between matches in `on`; each region gets its own relay instead. `record` keeps the rescan. The
  quality record says which pass it was (`trigger`: `connect` or `after-match`). Still to do in D2: the
  in-game ping, spike recorder, match summary and app status following the tunnel that carries the match.

Two deliberate differences in Phase A, both in the failure direction: an unexpected exception while sending
one packet now costs that packet and a log line instead of ending the uplink for good (the old loop left a
tunnel that answered keepalives and carried nothing up - invisible to the supervisor); and teardown disposes
the reader and tunnel a second time right before the adapter session ends, so a reconnect that raced the
teardown cannot leave anything inside the ring (a race the old code had too).

Found while building the harness, unchanged by Phase A: packets waiting in the adapter during a swap to a
relay that hands out a DIFFERENT inner address are dropped by that relay's anti-spoofing - they were written
with the old address before the adapter was re-addressed. Multi-tunnel's fixed adapter address (5.2) is what
ends it.

This document is written to be argued with. Every guarantee in section 3 names the mechanism that
enforces it and the test that fails if it stops being true. If a change to the code breaks one of
them, the change is wrong or this document is - say which.

---

## 1. The problem

The client measures relays once, at connect, against **one** region: the one whose landmark answers
fastest over the player's own line (`ChooseTargetRegionAsync`), on the assumption that the game picks
its region the same way. It opens **one** tunnel, and every region of the game goes through it.

That assumption is true for PUBG. It is false for the games added since:

- **Delta Force** puts the same player on Ho Chi Minh City (Zenlayer), Hong Kong, Singapore, Jakarta and
  Bangkok match to match (discovery sightings, 2026-09-24). Its profile has those as five regions, with a
  landmark on Hong Kong only.
- **Naraka: Bladepoint** plays in Tokyo (GCP) and Ho Chi Minh City (Zenlayer) on the AS region.
- **Domestic servers are not safe to leave alone.** A Vietnamese line can reach a server in Ho Chi Minh
  City through Hong Kong: 20 ms becomes 60-80. League of Legends is 100% Vietnamese servers and players
  use the VN relays for it daily for exactly that reason.

So the relay that is best for one region is routinely the wrong one for the next match, and today a
player on a line that detours to HCM is helped only if the relay chosen for Hong Kong happens to be a
VN one.

## 2. Why it can be solved at all

A game server learns the player's address from the **first packet** of a flow. Until that packet
arrives it has seen nothing, so the path that first packet takes can be chosen freely. What must never
happen is the exit address changing **after** that - which is why today a relay switch drops a match
and entry switching (same relay, same exit) does not. See `docs/ARCHITECTURE.md` and
`TunnelClient.MoveTo`.

A match lives on one server, and a server lives in one region. So:

> Route each region through its own best path, decided **before** the region's first packet, and never
> move a destination that is in use.

The client does not need to detect "the match was found in Hong Kong" and react. The routing table is
installed before matchmaking; the first packet to a Hong Kong address goes into the Hong Kong path by
the destination address alone.

Two facts from production make this safe, and both were checked, not assumed:

- **Servers do not bind the match to the lobby's address.** Delta Force, Naraka, LoL, Apex and PUBG
  before lobby routes all ran the lobby over the player's own address and the match through a relay's.
  Those matches work (Delta Force's 76 discovered servers came from matches played that way). A match
  going through a different relay from the lobby is the same situation.
- **A game can talk to a server before playing on it.** Naraka sent 1 packet/s to 34.146.241.71 from
  the lobby and later played a whole match on it (`games.json`, naraka note). Whatever carried the
  lobby trickle must carry the match. Section 5.3 (sticky destinations) exists for this.

## 3. The guarantees

| # | Guarantee | Enforced by | Checked by |
|---|---|---|---|
| G1 | The exit address of a destination in use never changes. | Sticky destination table in the uplink (5.3); a tunnel stays open while any destination is stuck to it; routes for a region going direct are replaced by /32 pins for its stuck destinations first (5.4). | PathCheck `Sticky*`; FakeRelay harness `RemapUnderLoad` |
| G2 | No region is sent down a path measured slower than the path today's client would give it (the home tunnel). A region leaves home only when another path beats it by `RelayPaths.HelpMargin` (max(5 ms, 10%)). | `RegionPlanner` rule 3-4 | PathCheck `PlannerNeverWorseThanHome` (property test, 20 000 random plans) |
| G3 | A region that cannot be measured fairly stays on home - exactly today's behaviour. No landmark, a landmark that does not answer through home, or a comparison that would mix a one-leg and a two-leg number. | `RegionPlanner` rule 1 | PathCheck `UnmeasurableRegionStaysHome` |
| G4 | With the mode off, the client behaves as it does today. | Phase A ships the refactored data plane with one tunnel and no behaviour change, and is soaked before any multi-tunnel code can run (section 9). | FakeRelay harness `SingleTunnelMatchesLegacy`; live soak |
| G5 | Never two paths into one relayd. A second handshake to a relay in use moves that session's return address and blackholes the live tunnel (`allocSession` resume path, `server.go`). | Planner assigns regions to **relays**, not to doors; measurement through a relay in use goes through its live tunnel (`ProbeGameServerAsync`), never a new handshake; door choice stays entry switching's. | PathCheck `OnePathPerRelay`; code review rule in 5.6 |
| G6 | A tunnel that dies takes only its own regions to the player's own line; the others carry on. | Per-tunnel supervisor (5.7) | FakeRelay harness `OneRelayDies` |
| G7 | Three ways back to one tunnel without a release: `regionRouting` in config.json, the game's `regionRouting` in the profile, and an automatic collapse on any internal fault in the dispatcher. | 8.1, 5.9 | FakeRelay harness `DispatcherFaultCollapses` |
| G8 | "Direct" (the player's own line) is chosen only when allowed for the game and faster than both home and the best relay the plan can still use (within `MaxTunnels`) by the same margin. | `RegionPlanner` rule 5 | PathCheck `DirectOnlyWhenAllowedAndFaster` |
| G9 | Connect is not slower. Connect chooses home exactly as today; everything else is measured afterwards, in the lobby. | 5.5 | PhaseTimer line in the log |

"Tối ưu kết nối tốt nhất cho khách hàng", made precise: **for every region, the path with the lowest
measured end-to-end latency to that region's landmark, chosen by a margin large enough that noise cannot
choose it, and never at the price of a match in progress.** G1 is the "never at the price"; G2/G3 make
multi-tunnel monotone over today's client; G8 lets the player's own line win when it really is better.

## 4. Terms

- **Region** - a profile region: ranges (`cidrs`) and landmarks. The unit a path is chosen for.
- **Path** - how a region's packets leave: `home`, a relay `k` (through whichever way into `k` entry
  switching has it on), or `direct` (not routed at all).
- **Home tunnel** - the tunnel connect opens, chosen exactly as today. Carries the lobby, every region
  that has not been moved, and every routed packet that matches no region.
- **Tunnel** - one `TunnelClient`: one session on one relay. At most `MaxTunnels` (3) at once.
- **Active tunnel** - the tunnel carrying the game's UDP right now. What the in-game ping, the spike
  recorder and entry switching follow.
- **Stuck destination** - a destination address that has carried a packet in either direction within
  `StickyFor` (120 s), and the tunnel it is stuck to.

## 5. Design

### 5.1 The data plane

```
                 Windows routes: every region's cidrs + lobby /32s  ->  one Wintun adapter
                                              |
                               AdapterPump (ONE uplink thread, reads the ring)
                                              |
                     StickyDestinations.Resolve(dst)  -- stuck?  --> that tunnel
                                              | no
                          RegionTable.Find(dst) -> region -> plan -> tunnel (home if none)
                                              |
                    InnerNat.RewriteSource(adapterIp -> tunnel.InnerIp)   (only if they differ)
                                              |
                                    tunnel.SendInner(packet)
                                              |
      TunnelClient k:  socket <-> relay k;  own downlink thread:  consume probes/echoes,
                        InnerNat.RewriteDestination(tunnel.InnerIp -> adapterIp), adapter.SendPacket
```

- **One reader of the ring.** `TunnelClient.UplinkLoop` moves out into `AdapterPump`. Each tunnel keeps
  its downlink and keepalive threads. Wintun documents all four ring calls as thread-safe
  (`WintunReceivePacket`, `WintunReleaseReceivePacket`, `WintunAllocateSendPacket`,
  `WintunSendPacket`: "This function is thread-safe", api/wintun.h). Several downlink threads writing is
  therefore allowed. One cost is real: allocation order is delivery order, so a downlink thread
  descheduled between `AllocateSendPacket` and `SendPacket` holds back the others' packets. The window
  is two calls long and the threads run AboveNormal; the harness measures it (`DownlinkCoupling`).
- **Lookup cost.** One sticky lookup (a dictionary on a `uint`) and, on a miss, a binary search over
  the region table's sorted, non-overlapping ranges. A game sends 50-150 packets a second to one or two
  servers; this is noise next to a syscall.
- **Everything the tunnel used to count, it still counts** - per tunnel. Local noise and oversize are
  counted by the pump, which owns the read.

### 5.2 The inner address, and why the adapter keeps one

Every relay hands out its own inner address from 10.77.0.0/24 and drops a packet whose source is not
the address it assigned (`handleData`, anti-spoofing). The adapter has one address. So a packet leaving
through tunnel `k` must carry `k`'s address, and a reply must be given back to Windows with the
adapter's.

**The adapter's address is fixed for the life of a connection: the home tunnel's inner address at
connect.** Consequences, each deliberate:

- The home tunnel at connect needs no rewrite - the single-tunnel path is byte-for-byte today's.
- Every other tunnel rewrites. When its address happens to equal the adapter's (pools are stacks, so
  the first client on two relays often gets 10.77.0.2 on both) the rewrite is skipped; harmless either way.
- **In multi-tunnel mode a failover of home does not re-address the adapter**; the new home tunnel
  rewrites instead. Re-addressing would break every flow on the OTHER tunnels whose game socket bound
  or connected to the old address (a connected UDP socket fixes its source at connect). Today there are
  no other tunnels, so today's re-address costs nothing extra; with several it would. In mode off, the
  old re-address path stays.

The rewrite (`InnerNat`) is a NAT on one address, done with RFC 1624 incremental checksums:

| Packet | Fields rewritten | Checksums adjusted |
|---|---|---|
| IPv4, any | source (up) / destination (down) | IP header, including options (IHL > 5) |
| UDP, first fragment | - | UDP checksum over the pseudo-header; 0 stays 0 (none), a result of 0 becomes 0xFFFF (RFC 768) |
| TCP, first fragment | - | TCP checksum over the pseudo-header |
| Non-first fragment | IP header only | IP header only - there is no L4 header to touch |
| ICMP echo | - | none: ICMP has no pseudo-header |
| ICMP error (3, 4, 5, 11, 12), downlink | outer destination AND the embedded original header's source | outer IP; embedded IP header checksum; embedded UDP/TCP checksum when its 8 bytes are present; ICMP checksum for every changed word |
| Anything malformed or truncated | nothing | packet dropped and counted, never forwarded half-rewritten |

The ICMP error row is not optional: path-MTU discovery for the lobby's TCP and the "port unreachable"
that ends a dead UDP socket both arrive as ICMP errors quoting the packet as the relay NATted it. The
relay's conntrack already rewrote the quote back to `k`'s inner address, so without this row Windows
would receive an error about a packet it never sent and drop it.

Probes and echoes the client builds by hand (`MeasureThroughTunnelAsync`, `ProbeGameServerAsync`,
quality echoes) already carry the tunnel's own inner address and are consumed by that tunnel's downlink
before any rewrite. They never touch `InnerNat`.

MTU: every relay runs `-mtu 1400` (relayd default; `install.sh` passes nothing else). A secondary tunnel
whose handshake reports a smaller MTU than the adapter's is not used, and the log says so - adapter MTU
is never lowered mid-connection.

### 5.3 Sticky destinations

The dispatcher remembers, per destination address, the tunnel its last packet went through and when a
packet last passed in either direction (the downlink refreshes it too). While a destination is stuck,
**every packet to it goes to the same tunnel, whatever the plan now says.**

This single rule is what makes every other change safe at any moment:

- A region can be remapped from tunnel A to tunnel B mid-lobby: servers already talking keep A, new
  servers get B. No timing heuristic ("has the region been quiet for 10 s?") stands between a remap and
  a dropped match. Naraka's lobby trickle to the server it later plays on keeps that server on one tunnel.
- A tunnel is closed only when no region maps to it **and** no destination is stuck to it.
- A tunnel that dies releases its stuck destinations (5.7) - the only way a stuck destination moves,
  and only when the old exit is gone anyway.

`StickyFor` is 120 s. A connection silent for two minutes in both directions is gone; a game that
revives one after that sees a new exit, as it would after any reconnect today.

### 5.4 Routes

- Every routed region's cidrs, and the lobby /32s, point at the one adapter, as today. **Moving a region
  between tunnels changes no route** - it is a swap of an array reference in the dispatcher.
- **Direct** means the region's routes are removed. Before removing them, a /32 route is added into the
  adapter for each destination stuck in that region, so G1 holds for them; the pins go when the
  destinations unstick. `RouteManager` gains route groups keyed by region, so one region's routes can
  go without touching another's (today `RemoveGameRoutes` takes all of them).
- **Pins.** Every relay with a tunnel open is pinned to the physical adapter, and so is every way into
  it (entries). Today `PinRelayRoute` holds one prefix and `PinDoorRoutes` one relay's doors; both
  become sets keyed by relay. A profile must never route a relay's address, and the pins are the backstop
  if one does.
- **Overlap.** The region table refuses a game whose regions overlap (a /20 in one containing a /32 in
  another): longest-prefix-match across two regions has no single answer. Such a game runs single-tunnel
  and the log names the pair. A profile must never contain such a pair (section 7); this is the backstop.

### 5.5 Deciding: the planner

`RegionPlanner.Plan` is a pure function. Inputs, per region: the landmark measurements - through home,
through each candidate relay, and direct (median of `RescanScore.Samples`, `MinAnswered` required, the
same instrument rescans use). Plus: which relays are open, the previous plan, `allowDirect`,
`MaxTunnels`.

Rules, in order:

1. **Unmeasurable stays home.** No landmark, or no number through home → `home`. (G3)
2. Candidates are relays with a number. Never mixes: a relay without a number for this region is not a
   candidate for it (the all-or-nothing rule of `ChooseByEndToEnd`, per region).
3. **Best relay** = lowest median; between relays that tie, profile order. (A relay tying home never
   wins: it has to beat home by the margin.)
4. **Leave home only by margin**: `RescanScore.WorthMoving(home, best)`. Otherwise `home`. (G2)
5. **Direct** only if `allowDirect`, and `WorthMoving(chosen, direct)`. (G8)
6. **Cap**: at most `MaxTunnels - 1` relays besides home. Over the cap, the relays with the largest total
   saving across their regions are kept; regions on the rest go home (rule 5 re-applied).
7. **Hysteresis**: a region already on a path keeps it unless the new choice beats that path's *current*
   number by the margin - and only while that path still measures no slower than home (G2 holds through
   hysteresis). A path no longer available is recomputed from scratch. Any threshold flips once when a
   number sits on it; what hysteresis rules out is flipping **back** on the same noise.

When it runs:

- **Connect**: not at all. Home is chosen exactly as today; every region starts on home. (G9)
- **Lobby, after connect**: one background pass once the tunnel is up and no game UDP is flowing. Relays
  not open are handshaken once (1 attempt), measured against every landmark, then kept only if the plan
  uses them - otherwise Disconnect. Relays open are measured through their live tunnel (G5). Direct is
  an ICMP echo over the physical line: landmarks are never inside a routed range (the profile rules and
  `WarnAboutRoutedLandmarks` enforce it), so the echo cannot fall into the tunnel.
- **After a match**: the region that match played in is re-measured - replacing today's
  `RescanBetweenMatchesAsync`, which does the same for the one region there is. The budget stays 30 s.
- **Paused while a match is on.** Measurement echoes are tiny, but a handshake mid-match has no upside.
- **Applied at any time**, because of 5.3.

Cost, Delta Force on vn-1, vn-2, hk-2, sg-1 with five landmarks: 4 handshakes, 4 × 5 × 8 = 160 echoes
at 40-100 ms - about 15 s of background work in the lobby, once per connect, if run one after another.
Measured 2026-09-26 with five extra relays and their entries it ran past the 40 s budget, so it now runs
in parallel ACROSS relays and in order WITHIN one: every way into one relay resumes the same session (G5),
while different relays share nothing but the PC's uplink, and a few dozen echo packets a second are
nothing to it. Home and the player's own line are measured alongside. The pass takes about as long as the
relay with the most ways into it.

### 5.6 Measurement rules (each one has broken something before)

- **Never handshake a relay that has a tunnel open.** Its session would move to the probe's socket
  (`allocSession`, "resume" path) and the live tunnel's downlink would go silent. Measure through the
  live tunnel. This is G5, and the reason `RescanBetweenMatchesAsync` skips the current relay today.
- **Never measure another way into a relay in use by handshake** - that is DoorProbes' `Probe` (0x8),
  which relayd answers without moving anything.
- **`ProbeGameServerAsync` has one slot.** The in-game ping loop and the planner would contend; the slot
  becomes a small table (4) keyed by id and sequence.
- **Median of 8, 6 answered**, the same instrument on every path compared. Connect's best-of-3 stays
  where it is (choosing home) and is never compared with a median.
- **Landmarks are never routed.** SDR games (`landmarksRouted`) are the exception, and they are off
  (section 6).

### 5.7 Failure: one tunnel at a time

The supervisor loop stays the one place tunnels are opened, moved, replaced and closed. It now walks
every tunnel:

- A secondary silent for `SilenceBeforeDead` (15 s): its regions go to home **if home has answered in
  the last 5 s**, else direct (the routes come out, as today); its stuck destinations are released;
  it is disposed without a Disconnect (keeps the reservation). The planner's next pass may reopen it.
  The match that was on it is lost - exactly as a relay dying loses it today.
- **Home** silent: today's `ReconnectAsync`, for home only - lobby and home's regions go direct, the
  secondaries carry on. The new home rewrites rather than re-addressing the adapter (5.2).
- A move between ways into a relay (`MoveToDoor`) is unchanged and per tunnel.

### 5.8 What follows the active tunnel

The active tunnel is the one that carried the most game UDP in the last second (summed from the
per-tunnel `GameServerTally`s, which now sit behind the dispatcher).

| Today reads `_tunnel` for | Now |
|---|---|
| In-game ping (`ProbeGamePingAsync`) | the active tunnel's primary destination; a direct match is measured by plain ICMP over the physical line |
| Spike recorder, match summary | the active tunnel; a match played direct records "direct" and no relay |
| Entry switching (door probes, policy, `MoveToDoor`) | the active tunnel's relay; the others keep their door |
| `MatchGap` / rescans | game UDP summed over all tunnels |
| Discovery gating (`GameDestinationRecorder`) | summed over all tunnels - a direct match turns ETW on, which is right: its destinations are inside profile ranges and are not reported |
| Status to the UI | `RelayName` is the active tunnel's (home when idle); a new `Paths` list names each region's path |
| Throughput log | one line per tunnel |

### 5.9 Faults inside the dispatcher

Any exception in the pump other than the socket cases `UplinkLoop` handles today collapses the
connection to single-tunnel: every region is remapped to home in one swap, secondaries are closed once
nothing is stuck to them, and the log and the next quality upload say why. Multi-tunnel stays off until
the next connect. A bug in new code costs its benefit, not the player's match.

## 6. Per game

| Game | Mode to start with | Why |
|---|---|---|
| Delta Force | record → on first | five regions, one landmark today: sg, jkt, bkk and hcm need one each (Tencent/Zenlayer addresses outside the ranges, within ~2 ms of the servers - found the way 43.132.224.20 was). Without them those regions stay home (G3) and nothing is gained |
| Naraka | record | two regions (Tokyo, HCM); HCM is not routed today and needs a region + landmark before it can be measured |
| PUBG | record | the game picks its region by probing; multi-tunnel only helps a player the matchmaker puts elsewhere |
| LoL, TFT, WoT, Apex | record | one region each today; nothing changes until a second region exists |
| VALORANT | off | Riot Direct answers every region on the same addresses - there is nothing to tell apart |
| CS2 | **off** | its landmarks are its relays and are routed (`landmarksRouted`); a direct measurement cannot be taken while routes are in |

With one tunnel, a relay that is fast to Hong Kong is chosen for the Hong Kong landmark alone and then
carries the HCM matches too: 57-96 ms from a Hong Kong relay to the Zenlayer servers against 22 from a
Vietnamese one (measured 2026-09-25). This is the case multi-tunnel exists for.

## 7. Server side

### 7.1 Profile

- Each game carries `regionRouting` (`off` | `record` | `on`) and `regionDirect` (bool). Like
  `entrySwitching`: read at connect, never mid-match. A profile without them reads as `off` and no direct.
  An SDR game (`landmarksRouted`) is always `off`.
- A profile never has two regions of one game overlapping (5.4), and never routes a relay or a landmark.
- **Quality**: the client uploads a `regionPlan` record per planner pass (every region, every number, the
  choice, the mode) and the path on each match summary. This is the evidence for G2 in the field
  (section 10.4).

### 7.2 relay

- Keep the client id from the handshake (`VerifyHandshakeReqToken` returns it) in `sessionIdent`, and
  report it as `client_id`, so sessions of one installation on several relays can be recognised as one
  client. It is chosen by the client and signed, so it identifies an installation, not a person - the
  same trust it has in PSK mode. Built.
- Nothing else. No wire change: multi-tunnel is several ordinary sessions on several relays.
- Capacity: a player holds at most `MaxTunnels` sessions, on **different** relays. Watch session counts
  against `MaxClients` (30-50) once `on` spreads.

## 8. Configuration

### 8.1 Client

- `config.json` `regionRouting`: `off` | `record` | `on` - wins over the profile, exactly like
  `entrySwitching`, and must stay unset by default (the service rewrites config.json on every game
  change; a written default would pin every PC).
- `record` runs the planner and uploads what `on` would have done, and opens **no** secondary tunnel. The
  only cost is the lobby measurement pass.
- To try it on one PC: `"regionRouting": "record"` in `%ProgramData%\GamePingBooster\config.json`, then
  connect with the game open and wait in the lobby; the log shows `Region plan ...`. `"on"` puts the plan
  in force (phase D); removing the line goes back to the game's setting on the next connect.

## 9. Rollout

| Phase | What ships | Exit criteria |
|---|---|---|
| A | `AdapterPump` with one tunnel. `InnerNat`, region table and sticky table present but a single tunnel never rewrites. | Harness `SingleTunnelMatchesLegacy` green; one week on the owner's PC and one tester with in-game ping and spike counts unchanged against the week before |
| B | Profile fields served; relays redeployed with client id in reports | Relay reports carry client ids |
| C | Planner in `record`, all games but CS2/VALORANT | A week of `regionPlan` records: how often a region would leave home, by how much, how often direct would win |
| D | `on` via config.json on the owner's PC, Delta Force | Matches in two regions in one session; match summaries show the planned path carried each; no reconnects attributable to the dispatcher |
| E | `on` for Delta Force in the served profile | Section 10.4 metrics hold for a week |
| F | `regionDirect` for one game, record first | Direct chosen only where the in-match probe agrees it was faster |

Every phase can be undone by the switch in 8.1 or the game's served mode, without a release.

## 10. Test plan

### 10.1 PathCheck (pure logic, runs in `./gpb test`)

A console program like QualityCheck. No framework, no network, no admin rights.

- **InnerNat** - for UDP, TCP, ICMP echo, ICMP errors quoting UDP and TCP, first and non-first
  fragments, IP options, UDP checksum 0, odd lengths: rewrite, then verify every checksum by full
  recomputation with an independent implementation; rewrite back and compare to the original byte for
  byte. 50 000 random packets per kind. Truncated and malformed packets must be refused, not half-done.
- **RegionTable** - lookups against a brute-force scan on random ranges; overlap detection; /32s;
  boundaries (network and broadcast addresses of each range).
- **StickyDestinations** - a remap never moves a stuck destination; expiry; release on tunnel death;
  downlink refresh keeps a one-way-quiet flow stuck.
- **RegionPlanner** - the rules one by one, then property tests over 20 000 random measurement sets
  asserting G2, G3, G8, the cap, determinism and "a relay chosen has a number" on every plan, and no
  ping-pong: plan, re-plan with every number moved by up to a quarter of the margin, re-plan the original
  numbers - the third plan keeps what the second chose (G2 excepted).

### 10.2 FakeRelay harness (data plane, in-process)

`GamePingBooster.TunnelCheck`, in `./gpb test`. `IPacketDevice` replaces `WintunAdapter` behind the pump
(Wintun stays the only production device). FakeRelay is a loopback UDP server speaking the v3 PSK
handshake and keeping relayd's rules - session resume by client id, anti-spoofing, roaming on Data and
Ping but not Probe, Disconnect only from the current address - and answering echo requests like a relay's
kernel. FakeDevice counts any call inside the ring after the session ends, including one in progress.

Phase A scenarios (built, green, each mutation-tested - 7 deliberate bugs, 7 caught):

- **Uplink**: 3 000 packets, game traffic mixed with every kind of local noise - the relay receives exactly
  what the old loop sent, byte for byte against an independent model, in order; counters and tally agree.
- **Downlink**: 1 000 packets delivered in order; an echo through the live tunnel is answered and consumed.
- **Keepalive**: a ping a second, answered, RTT measured.
- **Swap**: with no target the ring is not read and packets wait; the next tunnel takes them all, in order.
- **Entry switching**: `MoveTo` at 150 pps each way - one session, other door, ≤ 30 ms of stream lost
  (0 in eight runs).
- **Reconnect**: same session and inner address after an abandon; the pump carries on.
- **Teardown**: reader, tunnel, session under load - nothing inside the ring after the end; plus a control
  run in the wrong order that the check does catch.
- **Faults**: an unwrappable packet, an oversize read and a full ring are each counted and cost only
  themselves.
- **Latency**: Windows to relay on loopback, p50 ≈ 0.12 ms, p99 ≈ 0.33 ms.

Still to write, with the multi-tunnel wiring: `TwoRegionsTwoRelays`, `RemapUnderLoad` (swap the plan
1 000 times while 3 flows run at 150 pps - no flow may change relay), `OneRelayDies`,
`HomeDiesSecondariesCarryOn`, `IcmpErrorReachesWindows`, `DispatcherFaultCollapses`, `DownlinkCoupling`
(added latency per packet at 3 tunnels × 150 pps, p99).

### 10.3 Existing suites

ProtocolCheck (wire format) and QualityCheck (spike detector) unchanged and green. Go: the client-id
report.

### 10.4 In the field

"Not worse" is measured, per game, per week, from match summaries: in-game ping measured against the
server (p50/p95), spikes per match, reconnects per hour - `on` against `record`, same players where
possible. A region whose planned path measured faster at planning but slower in the match more than a
third of the time is a landmark that does not stand for its servers, and goes back to home.

## 11. Risks and open questions

- **ICMP is not the game's UDP.** Some routes treat echoes differently. The planner compares ICMP with
  ICMP on every path, and 10.4 checks the result against the in-match probe of the real server.
- **A landmark stands for a region only as well as it sits next to its servers.** Delta Force's regions
  need landmarks checked from every relay the way 43.132.224.20 and Naraka's 34.85.0.48 were (within
  ~2 ms of the servers from each relay).
- **Idle secondary tunnels cost a relay slot and one keepalive a second.** Closed with the connection's
  idle rule; capacity watched per 7.2.
- **Games that fix their UDP source at connect() on an address that goes away.** Covered for secondaries
  by 5.2; home failover in mode off behaves as today.
- **Anti-cheat.** Nothing here touches the game: routes, one adapter, packets rewritten after they leave
  Windows' stack exactly as a home router does.
- Open: whether `MaxTunnels` should be 2. Phase C's records say how often a third would be used.

## 12. What this was checked against

Read for this design, 2026-09-25: `TunnelClient.cs` (pumps, probes, MoveTo), `TunnelEngine.cs` (connect,
selection, ChooseDoor, reconnect, routes, teardown, status), `TunnelEngine.BetweenMatches.cs`,
`RouteManager.cs`, `WintunAdapter.cs`, `GameProfile.cs`, `RelayPaths.cs`, `EntrySwitching.cs`,
`BetweenMatches.cs` (RescanScore), `DoorSwitchPolicy.cs` margins; relay `server.go` (handshake, anti-spoof,
roaming, Probe, allocSession/reservations, MaxClients), `cmd/relayd` flags (subnet, MTU); Wintun's
`api/wintun.h` for thread safety. Measured: relay RTT to every Delta Force and Naraka server
(2026-09-25).
