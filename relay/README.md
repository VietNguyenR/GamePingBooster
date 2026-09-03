# relayd

The UDP relay, running on a Linux VPS. It receives encapsulated IP packets from the client,
writes them into a TUN device, and lets the Linux kernel handle NAT and forwarding.

No external dependencies - the Go standard library only.

## Build

```bash
make build          # linux/amd64, static, no cgo
make build-arm64    # for ARM VPSes (Oracle Ampere, AWS Graviton)
make test
```

## Deploy

**Run this from the development machine, NOT on the VPS.** Go cross-compiles a static binary, so
the server never needs Go installed - it only receives a 2.4 MB executable.

The account does not have to be root. The payload is unpacked into `~/.gpb-deploy`, which any
account can write, and only the install step needs privilege: a deploy runs it directly when the
account is root, under `sudo` when sudo is passwordless, and otherwise opens a second connection
that can carry a sudo password - the first one cannot, because its stdin is the tarball and
`sudo -S` reads its password from stdin. See `RELAY_<NAME>_SUDO_PASSWORD` in `gpb.conf.example`.

Declare the relay once in `gpb.conf` at the repository root - host, and whichever of user, port,
key or password your VPS needs. Copy `gpb.conf.example` and edit one block; the file is
gitignored. The name in the middle of each key is yours to choose and is what you type:

```
RELAY_SG_HOST=203.0.113.10
RELAY_SG_USER=root
RELAY_SG_KEY=~/.ssh/id_ed25519
RELAY_DEFAULT=sg
```

Then, from anywhere in the repository and on any operating system:

```bash
./gpb relay list           # what gpb.conf declares, and how each one authenticates
./gpb relay deploy sg      # or just ./gpb relay deploy, for RELAY_DEFAULT
./gpb relay logs sg
```

`gpb.ps1` takes the same verbs on Windows, and `deploy.ps1` still works on its own:

```powershell
.\deploy.ps1 -RemoteHost sg
```

A name `gpb.conf` does not declare is handed to ssh unchanged, so an alias from your
`~/.ssh/config` or a plain `root@1.2.3.4` works too. With `make`:

```bash
make deploy HOST=root@1.2.3.4
```

If ssh/scp is not an option - some providers only offer a web console - build a tarball and
upload it yourself:

```powershell
.\deploy.ps1 -PackageOnly     # produces gpb-relay.tar.gz
```

```bash
# on the VPS
mkdir -p /opt/gpb && tar -xzf gpb-relay.tar.gz -C /opt/gpb
cd /opt/gpb/deploy && chmod +x *.sh && ./install.sh
```

Either way, `install.sh` does the following:

- installs the binary to `/usr/local/bin/relayd`
- generates a random PSK into `/etc/gpb/psk` (chmod 600) if there is not one already
- runs `setup-nat.sh`: enables `ip_forward`, sets `rp_filter=2`, adds MASQUERADE and MSS
  clamping, opens the UDP port
- installs and starts the `relayd` systemd unit

It finishes by printing the **endpoint** and the **PSK**. These go into two different files,
because they answer two different questions:

- the **endpoint** (`<ip>:51820`) goes into the profile, as an entry in `relays[]` - that is the
  data plane, the address the client sends packets to
- the **PSK** goes into `client/config.json`, next to a `defaultRelayId` matching the `id` of
  that relay entry

Neither goes into `gpb.conf`. That file describes how to **reach the VPS over SSH** and is read
on your machine only; the client never sees it. `./gpb relay deploy` prints both values in the
shape they belong in when it finishes.

`setup-nat.sh` detects the distribution and the firewall manager in use (firewalld, or iptables
with either Debian's `netfilter-persistent` or RHEL's `iptables-services`). It also refuses to
continue if `/dev/net/tun` is missing, which is the sign of an OpenVZ or LXC VPS: those cannot
create TUN devices at all, and you need a KVM-based host instead.

Remember to open the UDP port on the **provider's** firewall too - security groups and cloud
firewalls sit outside the machine and no script on it can reach them.

## Operating it

```bash
journalctl -u relayd -f                # follow the log
systemctl restart relayd               # restart
iptables -t nat -L POSTROUTING -n -v   # check NAT, including packet counters
```

The relay logs one statistics line every 30 seconds: session count, packets in and out, packets
dropped. If `dropped` climbs steadily while people are connected, check two things first: that
both sides share the same PSK, and that the client's MTU matches the relay's.

Per-packet logging deliberately does not exist. A single match is tens of thousands of packets;
logging each one would cost latency on the path whose latency is the entire point.

## Command-line flags

| Flag | Default | Notes |
|---|---|---|
| `-listen` | `:51820` | UDP port. Changing it means changing `setup-nat.sh` and the client profile too |
| `-tun` | `gpb0` | TUN interface name |
| `-subnet` | `10.77.0.0/24` | Inner IP pool; `.1` is the relay, the rest go to clients (253 slots) |
| `-mtu` | `1400` | **Must match the client** |
| `-psk-file` | - | Path to the PSK file (or set `GPB_PSK` instead). This is the mode to use. |
| `-idle-timeout` | `90s` | Drop a session after this long without packets and return its IP to the pool |
| `-max-session-age` | `24h` | Force a client to handshake again after this long |
| `-configure-if` | `true` | Run `ip addr/link` to configure the TUN device |
| `-log-level` | `info` | `debug` also logs why a handshake was rejected |
| `-rate-limit` | `512` | Per-session cap in **KB/s each way**. `0` disables it |
| `-rate-burst` | `0` | Burst allowance in KB; `0` means four seconds at the sustained rate |

`relayd -h` lists two further flags that belong to an authentication mode this repository does
not document. Running your own relay does not involve them: use `-psk-file`, which is what the
deploy script sets up for you.

## Rate limiting

Every client ships the same pre-shared key, so anyone who installs the software can use the relay
as a free VPN - and that traffic leaves under this VPS's address, which is what gets a server
terminated. Per-client credentials are the real answer; this is the cheap thing that caps the
damage until they exist.

The numbers come from measurement, not caution. A PUBG session on real hardware ran 73,252 packets
in 1,187 seconds: **62 packets and about 10 KB per second**. The default of 512 KB/s is fifty
times that, with a four-second burst on top, so a game never comes near it. If a player ever does
hit this limit, the setting is wrong - not the player.

Three properties matter more than the numbers:

- **It drops, it never queues.** Queuing would add latency to game packets, which is the one thing
  this project exists to avoid. A dropped packet is what a congested network does anyway.
- **It is checked after authentication**, so a forged or spoofed packet cannot spend a real
  session's allowance, and after the session is touched, so a limited session is not then timed
  out for traffic it was never allowed to send.
- **It is loud.** The first time a session is limited it says so in the log, once, and the count
  appears in the 30-second stats line as `rate_limited`. A limiter that silently eats packets is
  indistinguishable from a bad network.

What it does *not* do is bound the total: 253 sessions at 512 KB/s is more than any VPS uplink.
It caps one abuser, not all of them. Only per-client credentials fix that.

## Load testing with gpb-soak

`cmd/gpb-soak` drives a relay with many simulated clients for as long as you like. Every serious
bug found in this project so far was invisible to manual testing - they needed load, duration or
concurrency, and usually all three - so this tool exists to supply what a person with one PC
cannot.

It sends ICMP echo requests to the relay's **own** inner address (`10.77.0.1`). The Linux kernel
answers them, so each packet makes the full round trip - client, UDP, relay, TUN, kernel, TUN,
relay, UDP, client - without a single byte leaving the VPS. No game servers are involved and no
external bandwidth is used.

```
go build -o gpb-soak ./cmd/gpb-soak
./gpb-soak -relay 203.0.113.10:51820 -psk-file ./psk -clients 10 -pps 200 -duration 30m
```

It exits non-zero and names the problem when it finds one. What it detects:

| Flag | Why it matters |
|---|---|
| `-churn` | Reconnects one client at a time while the rest keep running |
| `-churn-gap` | Stays silent that long first. **Set it above the relay's `-idle-timeout`**, or the old session is still alive and the repeat handshake is answered from the live-session path, leaving the address reservation untested |
| `-joins` | A brand new client arrives periodically. **Without this the address-pool bugs are unreachable**: a returning client only loses its reserved address if somebody else takes it meanwhile, and only a client with no reservation of its own draws from the free pool |
| `-payload` | Raise it towards the MTU to probe fragmentation behaviour |

The three flags matter together. An earlier version of this tool passed cleanly against a relay
whose address reservation had been deliberately broken, because gaps never overlapped and no new
clients ever arrived. With `-churn 8s -churn-gap 40s -joins 6s` the same broken relay is caught
within two minutes:

```
a new client was handed 10.77.0.253, which client 1 is holding (it is temporarily away...)
ADDRESS HANDED OUT TWICE: 4 time(s) ...
```

A test that cannot fail is worse than no test, so verify the harness against a deliberately
broken build before trusting a green run from it.

### Running a relay locally for testing

A Linux relay can be run under WSL2 on the development machine, which is enough for everything
except NAT to the real internet:

```
CGO_ENABLED=0 GOOS=linux go build -o /tmp/relayd ./cmd/relayd
wsl -u root -e /tmp/relayd -psk-file /tmp/psk -listen :51820 -idle-timeout 5s
```

No `setup-nat.sh` is needed for this: soak traffic is addressed to the relay's own TUN address, so
the kernel delivers it locally and never has to forward or masquerade anything. Point `gpb-soak`
at the WSL VM's `eth0` address.
