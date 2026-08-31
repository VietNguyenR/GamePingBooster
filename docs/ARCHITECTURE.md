# Architecture

This document describes **what is actually implemented in the repository**.

The foundational choices, briefly, so the rest reads in context:

- **Route-based, not socket-based.** Traffic is selected by destination address in the Windows
  routing table. The alternative - filtering per process or per socket, as WinDivert does -
  means sitting in the game's network path, which is a worse place to be when an anti-cheat is
  watching. This design never touches the game.
- **Wintun for the virtual adapter.** WireGuard's TUN driver: signed, proven, and the driver is
  embedded in the DLL so there is nothing to install separately.
- **C# with Native AOT for the client.** The output is real machine code, so users do not need
  the .NET runtime installed, and startup is immediate.
- **Avalonia for the UI.** XAML, close to WPF, with official Native AOT support.
- **Go for the relay.** A solo project needs to ship; Go's garbage collector pauses well under
  100 microseconds, which is nothing next to the network latency being optimised. Tailscale runs
  its DERP relays on Go, which is the same shape of problem.

## The path of a packet

```
   PUBG (TslGame.exe)
        |  sends UDP to 198.51.100.7:20522
        v
   Windows network stack
        |  routing table says: 198.51.100.0/24 -> adapter "Game Ping Booster"
        v
   Wintun adapter  ------------->  gpb-service.exe reads the raw IP packet
                                        |  wraps it in a 9-byte header (docs/PROTOCOL.md)
                                        v
                                  UDP socket -> internet via the physical adapter
                                        |      (a pinned /32 route keeps the relay on this path)
        =============== submarine cable ===============
                                        v
   Relay VPS: relayd receives it, strips the header, writes the IP packet into TUN gpb0
        |
        v
   Linux kernel: forward + MASQUERADE -> on to 198.51.100.7:20522, source = the VPS address
```

The return path is the reverse: the kernel un-NATs the address, the packet lands on `gpb0`,
`relayd` looks up the inner destination address to find the session, the client receives it and
pushes it into Wintun, and Windows hands it to PUBG.

The most valuable property of this design: **NAT is done by the Linux kernel**, not by the Go
code. TCP, UDP, ICMP, fragmentation and connection tracking are therefore all correct without a
line being written for them - the same arrangement a WireGuard server uses.

## Three processes

| Process | Privilege | Language | Job |
|---|---|---|---|
| `GamePingBooster.exe` | normal user | C# + Avalonia | Show status, one toggle button |
| `gpb-service.exe` | **LocalSystem** | C# Native AOT | Virtual adapter, routing, tunnel, game detection |
| `relayd` | root on the VPS | Go | Wrap/unwrap packets, manage sessions |

The UI and the service talk over a **named pipe**, `\\.\pipe\GamePingBooster`, one line of JSON
per message (`client/src/GamePingBooster.Core/Ipc/`).

### Why the UI is separate from the service

Three reasons, most decisive first:

1. **It is technically required.** `WintunCreateAdapter` demands `LocalSystem`; running the UI
   "as administrator" is not enough, and fails with access denied. Since something has to run as
   LocalSystem anyway, it may as well be a service.
2. **User experience.** The service starts with Windows, so the user never sees a UAC prompt
   after installation.
3. **Attack surface.** The UI has by far the most code (XAML, bindings, HTTP) and runs with the
   least privilege. The privilege boundary is the named pipe, and it accepts exactly **four
   fixed verbs** - `connect` / `disconnect` / `status` / `reload-profile`. No file paths, no
   arbitrary commands; every parameter is checked against the profile the service loaded itself.

## Directory map

```
relay/                          Go, runs on the Linux VPS
  cmd/relayd/main.go            command-line flags, PSK loading, shutdown signals
  internal/protocol/            wire format (source of truth, has tests)
  internal/tun/                 opens /dev/net/tun with raw ioctls, no cgo
  internal/server/              UDP loop + TUN loop + session table
  deploy/                       setup-nat.sh, install.sh, relayd.service

client/
  src/GamePingBooster.Core/     shared by UI and service: protocol, IPC, profile model
  src/GamePingBooster.Service/  the engine, runs as LocalSystem
    Native/                     P/Invoke into Wintun and iphlpapi
    Network/                    RouteManager, GameProcessWatcher
    Tunnel/                     TunnelClient (socket + 2 pump threads), TunnelEngine (conductor)
    Ipc/PipeServer.cs           the privilege boundary
  src/GamePingBooster.App/      Avalonia UI, runs as a normal user

profiles/pubg-vn.json           game IP ranges + relay list (fetched at runtime)
tools/profile-builder/          scripts that build and validate profiles
docs/                           this document and its neighbours
```

## Small decisions that matter

**The two pump loops are dedicated `Thread`s, not `Task`s.** The thread pool can add
milliseconds of delay when the machine is under load - precisely when someone is playing a game.
For software whose entire value is measured in milliseconds, that is the wrong trade. See
`TunnelClient.StartPumping()`.

**Every route is added with `store=active`.** Routes live in RAM only and are gone after a
reboot. Combined with the virtual adapter disappearing when the process dies, a user can never
end up in the state where the app is gone but the machine has no internet.

**The pinned relay route is installed before any other route.** Skipping this produces a routing
loop. See `TunnelEngine.ConnectAsync()`.

**Game routes only exist while the game process is running.** AWS and Azure ranges are shared
with thousands of unrelated services; leaving the routes in place permanently would drag other
applications' traffic through the relay.

**The relay drops packets addressed to private ranges.** Otherwise anyone holding the PSK could
use the tunnel to reach the VPS's own private network. See `server.isForbiddenDst()`.

**The relay checks that the inner source address matches the one it assigned.** This stops one
client spoofing another's address to receive their return traffic.

## Security: the trade that was made

Version 1 **authenticates the handshake with HMAC-SHA256 over a pre-shared key, but does not
encrypt the data packets**.

What that buys: 9 bytes of overhead per packet, no encryption CPU cost, less code, and a tunnel
that can be debugged in Wireshark.

What it costs, stated plainly:

- An ISP, or anyone else on the path, **can read** the game traffic (which usually has its own
  application-layer encryption, but not always).
- An attacker who learns the session id - it is in the clear in every packet - can inject
  packets into the tunnel. The inner source address check limits the damage but does not
  eliminate it.
- The PSK is shared by every user: one compromised machine exposes everyone.

When this has to change: **before there are real users beyond friends.** The upgrade path is
already reserved - add type `0x7` for a Noise/X25519 handshake, keep type `0x3` for data but
wrap it in ChaCha20-Poly1305, and bump the version in the high nibble of the first byte so that
mismatched peers recognise each other.
