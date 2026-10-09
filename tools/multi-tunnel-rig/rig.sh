#!/usr/bin/env bash
# A multi-tunnel rig inside WSL2: two real relayd, an entry in front of each (iptables DNAT, as setup-entry.sh
# builds on the VN boxes), two echoing "game servers", and tc netem to make any one way into a relay slow or lossy.
#
#   wsl -d Ubuntu -u root -- bash tools/multi-tunnel-rig/rig.sh up        start everything, print the addresses
#   wsl -d Ubuntu -u root -- bash tools/multi-tunnel-rig/rig.sh degrade <way> <delay-ms> [loss-%]
#   wsl -d Ubuntu -u root -- bash tools/multi-tunnel-rig/rig.sh lanes <way> <delay-ms>   ECMP lanes on one way
#   wsl -d Ubuntu -u root -- bash tools/multi-tunnel-rig/rig.sh heal       every way back to normal
#   wsl -d Ubuntu -u root -- bash tools/multi-tunnel-rig/rig.sh status
#   wsl -d Ubuntu -u root -- bash tools/multi-tunnel-rig/rig.sh down
#   wsl -d Ubuntu -u root -- bash tools/multi-tunnel-rig/rig.sh ticket up|status|mint <device.pub>|down
#
# `ticket` adds a LICENSED relayd C with an entry G in front of it, for measurement tickets (the relay list's ping,
# docs/PROTOCOL-v3.md): its own licence key pair, made on first use, and `mint` signs a token for a device key. Needs
# relay/licence-gen as well as relay/relayd, both built for linux. Ways c and g can be degraded like the others.
#
# Ways: a (relay A direct), f (entry F -> A), b (relay B direct), e (entry E -> B).
# Relay A is home; relay B carries the "kr" region. Game servers: 198.51.100.10 (sg, via home) and 198.51.100.20
# (kr, via B), UDP echo on port 27015 and ICMP echo - also the regions' landmarks.
#
# netem delays and drops what LEAVES the rig on eth0, filtered by source address: a relay's replies to the PC down one
# way. Replies down an entry leave with the entry's address (conntrack undoes the DNAT), so each way is shaped alone.
#
# Needs: root, iptables, tc (sch_netem), /dev/net/tun. The relayd binary is the one `./gpb relay build` makes.
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
relayd="$repo/relay/relayd"
state=/run/gpb-rig
dev=eth0

base_ip() { ip -4 -o addr show "$dev" | awk '{print $4}' | head -1; }

addresses() {
    local cidr ip prefix o1 o2 o3
    cidr="$(base_ip)"; ip="${cidr%/*}"; prefix="${cidr#*/}"
    IFS=. read -r o1 o2 o3 _ <<<"$ip"
    PREFIX="$prefix"
    A="$o1.$o2.$o3.201"; B="$o1.$o2.$o3.202"; E="$o1.$o2.$o3.203"; F="$o1.$o2.$o3.204"
    C="$o1.$o2.$o3.205"; G="$o1.$o2.$o3.206"
}

up() {
    addresses
    mkdir -p "$state"
    [ -x "$relayd" ] || cp "$relayd" "$state/relayd" 2>/dev/null || true
    install -m 755 "$relayd" "$state/relayd"
    printf 'gpb-multi-tunnel-rig-psk-0123456789abcdef' >"$state/psk"

    for ip in "$A" "$B" "$E" "$F"; do ip addr replace "$ip/$PREFIX" dev "$dev"; done

    # The game servers, on a dummy interface: relayd hands their packets to the kernel, which delivers them here.
    ip link add gpbgame type dummy 2>/dev/null || true
    ip link set gpbgame up
    ip addr replace 198.51.100.10/32 dev gpbgame
    ip addr replace 198.51.100.20/32 dev gpbgame

    # Entries: DNAT, as on the VN boxes. The relay sees the PC's address on another port - a way in of its own.
    iptables -t nat -D PREROUTING -d "$E" -p udp --dport 51820 -j DNAT --to-destination "$B:51820" 2>/dev/null || true
    iptables -t nat -D PREROUTING -d "$F" -p udp --dport 51820 -j DNAT --to-destination "$A:51820" 2>/dev/null || true
    iptables -t nat -A PREROUTING -d "$E" -p udp --dport 51820 -j DNAT --to-destination "$B:51820"
    iptables -t nat -A PREROUTING -d "$F" -p udp --dport 51820 -j DNAT --to-destination "$A:51820"
    sysctl -qw net.ipv4.ip_forward=1

    pkill -f "$state/relayd" 2>/dev/null || true
    pkill -f "$state/echo.py" 2>/dev/null || true
    sleep 0.3
    nohup "$state/relayd" -listen "$A:51820" -tun gpbA -subnet 10.77.0.0/24 -psk-file "$state/psk" -rate-limit 0 \
        >"$state/relay-a.log" 2>&1 &
    nohup "$state/relayd" -listen "$B:51820" -tun gpbB -subnet 10.78.0.0/24 -psk-file "$state/psk" -rate-limit 0 \
        >"$state/relay-b.log" 2>&1 &

    # One socket per server address: a socket on 0.0.0.0 answers from the address the route picks - the relay's TUN
    # address - and a real game server answers from its own.
    cat >"$state/echo.py" <<'PY'
import select, socket
socks = []
for ip in ("198.51.100.10", "198.51.100.20"):
    s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    s.setsockopt(socket.SOL_SOCKET, socket.SO_RCVBUF, 4 << 20)
    s.bind((ip, 27015))
    socks.append(s)
while True:
    ready, _, _ = select.select(socks, [], [])
    for s in ready:
        data, addr = s.recvfrom(2048)
        s.sendto(data, addr)
PY
    nohup python3 "$state/echo.py" >"$state/echo.log" 2>&1 &

    heal
    sleep 0.5
    status
}

# The shaping: a prio qdisc whose third band is netem, one per way; a u32 filter per shaped source address.
shape() {
    tc qdisc del dev "$dev" root 2>/dev/null || true
    tc qdisc add dev "$dev" root handle 1: prio bands 4 priomap 1 1 1 1 1 1 1 1 1 1 1 1 1 1 1 1
    tc qdisc add dev "$dev" parent 1:4 handle 40: netem delay 0ms
}

heal() {
    tc qdisc del dev "$dev" root 2>/dev/null || true
    rm -f "$state"/shaped-*
    echo "healed: every way unshaped"
}

degrade() {
    addresses
    local way="$1" delay="${2:-0}" loss="${3:-0}" src
    case "$way" in
        a) src="$A" ;; b) src="$B" ;; e) src="$E" ;; f) src="$F" ;; c) src="$C" ;; g) src="$G" ;;
        *) echo "way must be a, b, c, e, f or g" >&2; exit 2 ;;
    esac
    # One netem per shaped way, each in a band of its own - rebuilt whole, so ways can be shaped together.
    touch "$state/shaped-$way"
    echo "$delay $loss" >"$state/shaped-$way"
    tc qdisc del dev "$dev" root 2>/dev/null || true
    local ways=(); for f in "$state"/shaped-*; do [ -e "$f" ] && ways+=("${f##*-}"); done
    local bands=$(( ${#ways[@]} + 3 ))
    tc qdisc add dev "$dev" root handle 1: prio bands "$bands" priomap 1 1 1 1 1 1 1 1 1 1 1 1 1 1 1 1
    local band=4
    for w in "${ways[@]}"; do
        local d l s
        read -r d l <"$state/shaped-$w"
        case "$w" in a) s="$A" ;; b) s="$B" ;; e) s="$E" ;; f) s="$F" ;; c) s="$C" ;; g) s="$G" ;; esac
        if [ "$l" != "0" ]; then
            tc qdisc add dev "$dev" parent "1:$band" handle "${band}0:" netem delay "${d}ms" loss "${l}%"
        else
            tc qdisc add dev "$dev" parent "1:$band" handle "${band}0:" netem delay "${d}ms"
        fi
        tc filter add dev "$dev" protocol ip parent 1:0 prio 1 u32 match ip src "$s/32" flowid "1:$band"
        band=$((band + 1))
    done
    echo "way $way: +${delay} ms, ${loss}% lost (shaped: ${ways[*]})"
}

# ECMP lanes on one way, as a VN datacentre's uplink has them (LanePick, measured 2026-09-30): replies down the way
# are held <delay-ms> when the PC's port - their destination port - is not a multiple of four, so one port in four
# is the fast link. Filtered on the way's source address AND the destination port's low two bits. Replaces all
# other shaping; `heal` removes it.
lanes() {
    addresses
    # Optionally a second way held whole, e.g. "lanes e 20 b 60": a slow road beside an entry with lanes.
    local way="$1" delay="${2:-20}" slow="${3:-}" slowdelay="${4:-0}" src slowsrc=""
    case "$way" in
        a) src="$A" ;; b) src="$B" ;; e) src="$E" ;; f) src="$F" ;;
        *) echo "way must be a, b, e or f" >&2; exit 2 ;;
    esac
    case "$slow" in
        "") ;; a) slowsrc="$A" ;; b) slowsrc="$B" ;; e) slowsrc="$E" ;; f) slowsrc="$F" ;;
        *) echo "the slow way must be a, b, e or f" >&2; exit 2 ;;
    esac
    rm -f "$state"/shaped-*
    tc qdisc del dev "$dev" root 2>/dev/null || true
    tc qdisc add dev "$dev" root handle 1: prio bands 5 priomap 1 1 1 1 1 1 1 1 1 1 1 1 1 1 1 1
    tc qdisc add dev "$dev" parent 1:4 handle 40: netem delay "${delay}ms"
    if [ -n "$slowsrc" ]; then
        tc qdisc add dev "$dev" parent 1:5 handle 50: netem delay "${slowdelay}ms"
        tc filter add dev "$dev" protocol ip parent 1:0 prio 2 u32 match ip src "$slowsrc/32" flowid 1:5
        echo "way $slow: held ${slowdelay} ms"
    fi
    local bits
    for bits in 1 2 3; do
        # u16 at 22: the UDP destination port, behind a 20-byte IPv4 header.
        tc filter add dev "$dev" protocol ip parent 1:0 prio 1 u32             match ip src "$src/32" match ip protocol 17 0xff match u16 "$bits" 0x0003 at 22 flowid 1:4
    done
    echo "way $way: lanes - ports with (port & 3) != 0 held ${delay} ms, the rest at once"
}

status() {
    addresses
    echo "A=$A:51820 (home relay)  F=$F:51820 (entry -> A)"
    echo "B=$B:51820 (kr relay)    E=$E:51820 (entry -> B)"
    echo "game servers: 198.51.100.10 (sg), 198.51.100.20 (kr), udp/27015"
    echo "relayd running: $(pgrep -fc "$state/relayd" || true), echo running: $(pgrep -fc "$state/echo.py" || true)"
    tc qdisc show dev "$dev" | sed 's/^/  tc: /'
}

down() {
    addresses
    heal >/dev/null
    pkill -f "$state/relayd" 2>/dev/null || true
    pkill -f "$state/echo.py" 2>/dev/null || true
    iptables -t nat -D PREROUTING -d "$E" -p udp --dport 51820 -j DNAT --to-destination "$B:51820" 2>/dev/null || true
    iptables -t nat -D PREROUTING -d "$F" -p udp --dport 51820 -j DNAT --to-destination "$A:51820" 2>/dev/null || true
    for ip in "$A" "$B" "$E" "$F"; do ip addr del "$ip/$PREFIX" dev "$dev" 2>/dev/null || true; done
    ip link del gpbgame 2>/dev/null || true
    echo "rig down"
}

logs() { tail -n "${1:-40}" "$state/relay-a.log" "$state/relay-b.log"; }

# The licensed relay C and its entry G, in a directory of their own: pkill/pgrep on "$state/relayd" must not see it.
ticket_up() {
    addresses
    local dir="$state/ticket"
    mkdir -p "$dir"
    install -m 755 "$relayd" "$dir/relayd"
    install -m 755 "$repo/relay/licence-gen" "$dir/licence-gen"
    [ -f "$dir/licence.key" ] || "$dir/licence-gen" keygen -name "$dir/licence" >/dev/null

    for ip in "$C" "$G"; do ip addr replace "$ip/$PREFIX" dev "$dev"; done
    iptables -t nat -D PREROUTING -d "$G" -p udp --dport 51820 -j DNAT --to-destination "$C:51820" 2>/dev/null || true
    iptables -t nat -A PREROUTING -d "$G" -p udp --dport 51820 -j DNAT --to-destination "$C:51820"
    sysctl -qw net.ipv4.ip_forward=1

    pkill -f "$dir/relayd" 2>/dev/null || true
    sleep 0.3
    nohup "$dir/relayd" -listen "$C:51820" -tun gpbC -subnet 10.79.0.0/24 -licence-key "$dir/licence.pub" \
        -relay-key "$dir/relay.key" -rate-limit 0 -log-level debug >"$dir/relay-c.log" 2>&1 &
    sleep 0.5
    ticket_status
}

ticket_status() {
    addresses
    local dir="$state/ticket"
    echo "C=$C:51820 (licensed relay)  G=$G:51820 (entry -> C)"
    echo "relay key: $("$dir/relayd" -print-relay-key -relay-key "$dir/relay.key" 2>/dev/null)"
    echo "licensed relayd running: $(pgrep -fc "$dir/relayd -listen" || true)"
}

# A token for the device public key in the file given (hex, 65 bytes), signed with the rig's licence key; hex on stdout.
ticket_mint() {
    "$state/ticket/licence-gen" mint -key "$state/ticket/licence.key" -device "$1" -user 1 -expiry 1h
}

ticket_down() {
    addresses
    pkill -f "$state/ticket/relayd" 2>/dev/null || true
    iptables -t nat -D PREROUTING -d "$G" -p udp --dport 51820 -j DNAT --to-destination "$C:51820" 2>/dev/null || true
    for ip in "$C" "$G"; do ip addr del "$ip/$PREFIX" dev "$dev" 2>/dev/null || true; done
    echo "licensed relay down"
}

ticket() {
    case "${1:-status}" in
        up) ticket_up ;;
        status) ticket_status ;;
        mint) shift; ticket_mint "$@" ;;
        down) ticket_down ;;
        logs) tail -n "${2:-40}" "$state/ticket/relay-c.log" ;;
        *) echo "usage: rig.sh ticket up|status|mint <device.pub>|logs [n]|down" >&2; exit 2 ;;
    esac
}

case "${1:-status}" in
    up) up ;;
    down) down ;;
    heal) heal ;;
    degrade) shift; degrade "$@" ;;
    lanes) shift; lanes "$@" ;;
    status) status ;;
    logs) shift; logs "$@" ;;
    ip) echo "$dev $(base_ip | cut -d/ -f1)" ;;
    ticket) shift; ticket "$@" ;;
    *) echo "usage: rig.sh up|down|heal|degrade <a|b|e|f> <delay-ms> [loss-%]|lanes <a|b|e|f> <delay-ms> [<slow-way> <delay-ms>]|status|logs [n]" >&2; exit 2 ;;
esac
