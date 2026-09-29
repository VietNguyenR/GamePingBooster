#!/usr/bin/env bash
# A multi-tunnel rig inside WSL2: two real relayd, an entry in front of each (iptables DNAT, as setup-entry.sh
# builds on the VN boxes), two echoing "game servers", and tc netem to make any one way into a relay slow or lossy.
#
#   wsl -d Ubuntu -u root -- bash tools/multi-tunnel-rig/rig.sh up        start everything, print the addresses
#   wsl -d Ubuntu -u root -- bash tools/multi-tunnel-rig/rig.sh degrade <way> <delay-ms> [loss-%]
#   wsl -d Ubuntu -u root -- bash tools/multi-tunnel-rig/rig.sh heal       every way back to normal
#   wsl -d Ubuntu -u root -- bash tools/multi-tunnel-rig/rig.sh status
#   wsl -d Ubuntu -u root -- bash tools/multi-tunnel-rig/rig.sh down
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
        a) src="$A" ;; b) src="$B" ;; e) src="$E" ;; f) src="$F" ;;
        *) echo "way must be a, b, e or f" >&2; exit 2 ;;
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
        case "$w" in a) s="$A" ;; b) s="$B" ;; e) s="$E" ;; f) s="$F" ;; esac
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

case "${1:-status}" in
    up) up ;;
    down) down ;;
    heal) heal ;;
    degrade) shift; degrade "$@" ;;
    status) status ;;
    logs) shift; logs "$@" ;;
    ip) echo "$dev $(base_ip | cut -d/ -f1)" ;;
    *) echo "usage: rig.sh up|down|heal|degrade <a|b|e|f> <delay-ms> [loss-%]|status|logs [n]" >&2; exit 2 ;;
esac
