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

**Client** (needs the .NET 9 SDK). Two builds, for two different jobs:

```bash
./gpb dev        # Debug build, then starts the service and the UI. The everyday loop.
./gpb publish    # Native AOT Release build, installed into %ProgramData%
```

`./gpb dev` is what you want while working. It stops anything already running, builds, and
starts the service through `psexec -s -i` - Wintun refuses to create an adapter for anything
below LocalSystem, and Administrator is not enough.

`./gpb publish` produces the real thing: a Native AOT executable that is machine code, needs no
.NET runtime on the user's PC, and starts immediately. It additionally needs **Visual Studio
Build Tools** with the "Desktop development with C++" workload.

Underneath it is just `dotnet publish`, and you can run that directly:

```bash
cd client && dotnet publish src/GamePingBooster.Service -c Release -r win-x64
cd client && dotnet publish src/GamePingBooster.App -c Release -r win-x64
```

...but `./gpb publish` also does three things that are easy to miss and whose failures name the
wrong cause:

- **Puts `vswhere.exe` on PATH first.** Native AOT needs the MSVC linker and finds it through
  `vswhere`, which is not on PATH by default. Without it the publish dies with
  `'vswhere.exe' is not recognized`, which says nothing about AOT.
- **Stops the service and the UI first.** The service holds `wintun.dll` and the UI holds
  `Core.dll`; a build that cannot overwrite them fails with a locked-file error that does not
  mention either.
- **Copies the result into `%ProgramData%\GamePingBooster\bin`**, skipping `.pdb` files.

A registered service keeps running the old binary until it is restarted, which needs
Administrator:

```
sc.exe stop GamePingBooster
sc.exe start GamePingBooster
```

## Commands

Everything day to day goes through one entry point. `./gpb` is POSIX `sh` and runs on Linux,
macOS and Git Bash; `gpb.ps1` is the same set of verbs for PowerShell. `./gpb` hands the
Windows-only verbs to `gpb.ps1` when you are on Windows, and explains itself rather than failing
strangely when you are not.

Run them from `application/`. `./gpb` with no arguments prints this list.

### Relay - works on any operating system

| Command | What it does |
|---|---|
| `./gpb relay setup` | First-time setup, explained step by step. Start here. |
| `./gpb relay list` | The relays `gpb.conf` declares, how each one authenticates, and the endpoint a client would use. Use it to check what actually got parsed. |
| `./gpb relay build` | Cross-compiles `relayd` for Linux. Static, no cgo, so the VPS never needs Go. |
| `./gpb relay deploy [name]` | Builds, ships and installs in **one** ssh connection - the payload goes over as a tar stream on stdin. Omit the name to use `RELAY_DEFAULT`. |
| `./gpb relay logs [name]` | Follows `journalctl -u relayd -f` on that relay. |
| `./gpb relay test` | Go tests only. |

`./gpb relay` on its own prints that table.

Relays are declared in `gpb.conf` (copy `gpb.conf.example`; it is gitignored and no host in this
repository is real). A name that file does not declare is handed to `ssh` unchanged, so an alias
from your `~/.ssh/config` or a plain `root@203.0.113.10` works too.

An account that is not root is fine: the payload unpacks into `~/.gpb-deploy`, and only the
install step needs privilege. It runs directly when the account is root, under `sudo` when sudo
is passwordless, and otherwise over a second connection that can carry a sudo password - the
first one cannot, because its stdin is the tarball and `sudo -S` reads its password from stdin.

### Client - Windows only

These drive a virtual adapter, the routing table, a Windows service and Npcap. They have no
meaning on another system and say so instead of failing oddly.

| Command | What it does |
|---|---|
| `./gpb dev` | The everyday loop: stop what is running, build, start the service as LocalSystem via `psexec`, start the UI. |
| `./gpb publish` | Native AOT Release build, installed into `%ProgramData%\GamePingBooster\bin`. See [Build](#build). |
| `./gpb stop` | Stops the service and the UI. |
| `./gpb status` | Asks the running service for its live counters over the named pipe: state, packets, drops, tunnel ping, loss, active routes. |
| `./gpb logs` | Follows the service log in `%ProgramData%\GamePingBooster\logs`. |
| `./gpb check` | **Run this during a match.** Answers whether the game's traffic is really going through the relay or straight out of the network card. Outside a match it has nothing to look at. |
| `./gpb capture` | Waits for the game to start, captures its traffic, and appends the server addresses it sees to `observed.txt`. Safe to Ctrl+C. |
| `./gpb profile` | Turns what `capture` collected into `profiles/pubg-vn.json`. No arguments. |
| `./gpb diag` | Collects everything needed to diagnose a client-side problem into one text file, with the PSK redacted. Attach it to a bug report. |

`capture` and `profile` are a pair and are how the profile grows: play, capture, rebuild. A
profile is only as good as the number of matches behind it.

### Both halves

| Command | What it does |
|---|---|
| `./gpb test` | Everything: `gofmt`, `go vet`, the Go tests, then the C# build and the cross-language wire-format check. Skips the C# half with a warning if the .NET SDK is missing. |
| `./gpb help` | The same list, from the script itself. |

`./gpb test` always passes `-count=1`. Go's test cache is not keyed on
`testdata/protocol-vectors.json`, so without it a tampered vector file is reported as a cached
pass - which is the exact failure that file exists to catch. The whole suite takes about six
seconds from cold.

## Running it

1. Declare your VPS in `gpb.conf` (copy `gpb.conf.example`; it is gitignored), then
   `./gpb relay deploy`. See [relay/README.md](relay/README.md). Note the endpoint and PSK it
   prints - they go into two different files, as step 3 says.
2. Download the signed `wintun.dll` into `client/native/wintun/` - see
   [client/native/wintun/README.md](client/native/wintun/README.md).
3. Copy `client/config.example.json` to `client/config.json` and fill in the PSK. Set the relay
   from the app's Settings screen once it starts, or put its endpoint into `relayEndpoint` by
   hand.

   **Two files, and the split matters.** `config.json` is *this machine's settings* - the key,
   which relay to use, the adapter name. The profile is *content* - the game's IP ranges, and
   the relays the vendor offers. Keep your own relay out of the profile: a profile is replaced
   wholesale every time it is fetched from a server, so anything of yours written into it
   disappears silently on the next update.

   There are two ways to name a relay, and they are mutually exclusive:

   | Setting | Means |
   |---|---|
   | `defaultRelayId` | Use one of the relays the profile lists, by id. |
   | `relayEndpoints` | Use your own relays. **Replaces** the profile's list entirely - somebody running their own relays wants those, not a silent fallback to somebody else's. |

   `relayEndpoints` is a list, and giving it more than one is worth doing: the client measures
   every relay it knows before connecting and takes the fastest, then falls back to the others
   if that one stops answering. One address turns both of those off.

   `profilePath` is relative to the install directory and should stay that way; an absolute path
   only works on the machine it was written on. It is not in Settings on purpose - it is
   something the installer knows, not something a user should have to.
4. Build and start both halves:
   ```
   ./gpb dev
   ```
   The service has to run **as LocalSystem** - Wintun requires it and Administrator does not
   satisfy it - which is why this goes through `psexec` rather than just launching the exe. By
   hand, if you would rather:
   ```
   psexec -accepteula -s -i <path>\gpb-service.exe --console
   ```
5. Press Connect in `GamePingBooster.exe`.

`./gpb status` reads the tunnel's live counters, `./gpb logs` follows the service log, and
`./gpb check` answers whether the game is actually going through the relay - run that one during
a match.

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
