# Game Ping Booster

Latency reducer for PUBG on Windows, aimed at players in Vietnam. Gameplay traffic is diverted
through a VPS abroad whose route to the game servers beats the one the local ISP picks by default.

No injection, no reading game memory, no DLL hooks. The whole mechanism is a virtual network
adapter plus entries in the Windows routing table: destinations belonging to the game go through
the tunnel, everything else keeps using the normal path. Detecting that the game is running means
listing processes, exactly as Task Manager does.

## Layout

```
relay/     Go         -> runs on a Linux VPS
client/    C# .NET 9  -> runs on the player's machine (Avalonia UI + Windows Service)
profiles/  JSON       -> game IP ranges, fetched at runtime
tools/     PowerShell -> build and validate profiles
docs/                 -> design documentation
```

## Where to start

| You want | Read |
|---|---|
| The system as a whole | [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) |
| The wire protocol | [docs/PROTOCOL.md](docs/PROTOCOL.md) |
| Deploying and operating the relay | [relay/README.md](relay/README.md) |
| The virtual adapter binary | [client/native/wintun/README.md](client/native/wintun/README.md) |

## How the game's addresses are found

Routing works on destination addresses, so the profile has to say which addresses belong to the
game - and it has to be narrow. AWS and Azure publish the enclosing blocks as `/17`s and `/18`s;
routing one of those would drag thousands of unrelated services through the relay.

`tools/profile-builder/` solves that in two commands:

```powershell
cd tools\profile-builder
.\Capture-GameTraffic.ps1     # leave running, play, Ctrl+C when done
.\Build-PubgProfile.ps1       # no arguments
```

The first watches for the game process, captures while it runs, attributes traffic to the process
by joining captured source ports against the Windows UDP socket table, and appends the servers it
saw. The second cross-checks every address against the official AWS and Azure range files, keeps
each one narrow, sorts it into the right region, writes `profiles/pubg-vn.json` and validates the
result.

Addresses that belong to no published cloud range are never added automatically; they are set
aside for review. In practice they turn out to be voice chat or a CDN.

## Build

**Relay** (needs Go 1.22+, builds from Windows or Linux):

```bash
cd relay && make build && make test
```

**Client** (needs the .NET 9 SDK):

```bash
cd client && dotnet build
```

A Native AOT build additionally needs **Visual Studio Build Tools** with the
"Desktop development with C++" workload:

```bash
cd client && dotnet publish src/GamePingBooster.App -c Release
cd client && dotnet publish src/GamePingBooster.Service -c Release
```

## Running it

1. Declare your VPS in `gpb.conf` (copy `gpb.conf.example`; it is gitignored), then
   `./gpb relay deploy`. See [relay/README.md](relay/README.md). Note the endpoint and PSK it
   prints - they go into two different files, as step 3 says.
2. Download the signed `wintun.dll` into `client/native/wintun/` - see
   [client/native/wintun/README.md](client/native/wintun/README.md).
3. Copy `client/config.example.json` to `client/config.json` and fill in the PSK. Put the relay's
   endpoint into `profiles/pubg-vn.json`.
4. Start the service **as LocalSystem**, which Wintun requires and Administrator does not satisfy:
   ```
   psexec -accepteula -s -i <path>\gpb-service.exe --console
   ```
5. Start `GamePingBooster.exe` and press Connect.

Things that go wrong first, in order of likelihood:

- `WintunCreateAdapter` returns error 5 - the process is not actually running as LocalSystem.
  Check with `whoami`; it must print `nt authority\system`.
- The handshake never completes - the provider's cloud firewall is blocking the UDP port.
- Packets reach the relay but nothing comes back - `net.ipv4.ip_forward` is off, or `rp_filter`
  is still in strict mode.
- Small packets work but large ones hang - MTU or MSS clamping.

## Status

**The tunnel works end to end on real hardware** (verified 2026-08-30). Traffic goes from
Windows, through the Wintun adapter, through the relay on a VPS, out to the internet and back -
confirmed with both ICMP and TCP, the latter proving MSS clamping and stateful NAT on the return
path are correct.

Not yet done:

- **Never tested with PUBG itself.** The code path that installs routes when `TslGame.exe`
  starts has not run against the real game.
- **The profile is not saturated.** `profiles/pubg-vn.json` holds 3 prefixes derived from about
  a dozen matches. Every gameplay server observed so far was on Azure `southeastasia`; none on
  AWS. More capture sessions are needed before the list can be trusted.
- **No installer.** The client currently has to be started by hand.
- **The tunnel authenticates but does not encrypt.** See the security section of
  [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for what that costs and when it has to change.

## License

MIT - see [LICENSE](LICENSE).
