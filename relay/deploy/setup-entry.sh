#!/usr/bin/env bash
# Make this VPS an ENTRY: UDP arriving on one port is forwarded, untouched, to a relay.
#
# Normally run by `./gpb entry deploy <name>`, which uploads this file and passes every relay
# gpb.conf declares. Given several relays it pings each one FROM HERE and forwards to the nearest -
# the second half of every path through this entry is exactly that hop. By hand, as root:
#
#   setup-entry.sh [--listen PORT] [--relay NAME] [--wan IFACE] NAME=IP:PORT [NAME=IP:PORT ...]
#
# No relayd runs here, and nothing is decrypted, rewritten or re-signed. The client handshakes
# THROUGH this machine with the relay behind it and checks that relay's own signature, so a
# forwarder pointed at the wrong place produces a failed handshake - never a trusted wrong relay.
#
# THE CHOICE IS STICKY. The licence server lists this entry under ONE relay, and a client checks
# that relay's signature through it: forward somewhere else and every handshake through the entry
# fails until the entry is moved in /admin/relays as well. So the relay chosen is remembered, in
# /etc/gpb/entry-<port>.relay, and a later run only REPORTS a nearer one. It moves by itself only
# when its relay has stopped answering or is no longer declared - an entry pointing at nothing is
# no better off - and it then says so in a way that is hard to miss. --relay forces one.
#
# Why an entry exists at all: a line that leaves the country the long way round can still reach a
# datacentre at home in a few milliseconds, and that datacentre can reach the relay by a cable the
# line itself never uses. Entry at home, exit next to the game. No client hears of this machine
# until it is added as an entry of its relay on the licence server; from then on every client is
# told about it, and measures it only when no relay beats that player's own connection.
#
# Exit status: 0 done, 1 bad input or setup failure, 2 no relay answered (nothing was changed),
# 3 --relay names a relay that was not given. Never 90 or 91 - ./gpb reserves those for sudo.

set -euo pipefail

PINGS=20

die() { echo "$*" >&2; exit 1; }

# "avg loss" from ping's summary, avg "-" when nothing came back. Both the iputils and the busybox
# wording, since a small VPS image can carry either.
parse_ping() {
  local out loss avg
  out=$(cat)
  loss=$(printf '%s\n' "$out" | sed -n 's/.* \([0-9.]*\)% packet loss.*/\1/p' | head -1)
  avg=$(printf '%s\n' "$out" | sed -n 's#^\(rtt\|round-trip\) [^=]*= [0-9.]*/\([0-9.]*\)/.*#\2#p' | head -1)
  printf '%s %s\n' "${avg:--}" "${loss:-100}"
}

measure() {
  ping -n -c "$PINGS" -i 0.2 -W 1 -q "$1" 2>/dev/null | parse_ping || true
}

# Quiet, non-interactive, with one retry after refreshing the package lists - a fresh VPS image
# often has none. Returns non-zero on a system without apt.
apt_install() {
  command -v apt-get >/dev/null 2>&1 || return 1
  DEBIAN_FRONTEND=noninteractive apt-get install -y -q "$@" >/dev/null 2>&1 && return 0
  apt-get update -q >/dev/null 2>&1 || return 1
  DEBIAN_FRONTEND=noninteractive apt-get install -y -q "$@" >/dev/null 2>&1
}

# Which relay to forward to.
#
#   choose_relay <table> <pinned name> <current endpoint>
#
# The table is one "name endpoint avg loss" per line, avg "-" for no reply. A relay counts as
# answering when it replied at all and lost less than half. Prints "name endpoint why", why being
# pinned | kept | first | moved-unreachable | moved-undeclared, and a second line
# "nearer name avg" when the relay kept is clearly further than the nearest - by max(5 ms, 10%),
# the same margin the client uses to decide a path is worth switching to. Returns 2 when nothing
# answers and 3 when the pinned relay is not in the table.
choose_relay() {
  printf '%s\n' "$1" | awk -v pinned="$2" -v current="$3" '
    NF < 4 { next }
    {
      name[NR] = $1; ep[NR] = $2; avg[NR] = $3
      ok[NR] = ($3 != "-" && $4 + 0 < 50)
      if (ok[NR] && (best == "" || $3 + 0 < avg[best] + 0)) best = NR
      if ($1 == pinned) pin = NR
      if ($2 == current) cur = NR
    }
    END {
      if (pinned != "") {
        if (pin == "") exit 3
        print name[pin], ep[pin], "pinned"
        exit 0
      }
      if (current != "" && cur != "" && ok[cur]) {
        print name[cur], ep[cur], "kept"
        margin = avg[cur] * 0.1
        if (margin < 5) margin = 5
        if (best != cur && avg[best] + 0 < avg[cur] - margin) print "nearer", name[best], avg[best]
        exit 0
      }
      if (best == "") exit 2
      why = "first"
      if (current != "") why = (cur == "" ? "moved-undeclared" : "moved-unreachable")
      print name[best], ep[best], why
    }'
}

# ------------------------------------------------------------------------------------ forwarding

apply_forwarding() { # apply_forwarding <relay ip> <relay port> <listen port> <wan> <changed 0|1>
  local relay=$1 relay_port=$2 port=$3 wan=$4 changed=$5
  local tag="gpb-entry-${port}"

  # Forwarding rules take every packet on their port in PREROUTING, before any local socket sees
  # it. A relayd already listening here would simply stop receiving - worth saying first.
  if command -v ss >/dev/null 2>&1 && ss -Hlun "sport = :$port" 2>/dev/null | grep -q .; then
    echo "!! Something on this machine already listens on UDP $port (a relayd?). After this it" >&2
    echo "   receives nothing on that port. Stop it, or give this entry another port." >&2
  fi

  echo "==> Enabling IP forwarding"
  cat > /etc/sysctl.d/99-gpb-entry.conf <<'SYSCTL'
net.ipv4.ip_forward = 1
SYSCTL
  sysctl -q --system

  if command -v firewall-cmd >/dev/null 2>&1 && systemctl is-active --quiet firewalld 2>/dev/null; then
    echo "==> Configuring via firewalld"
    local zone rule
    zone="$(firewall-cmd --get-default-zone)"
    # Drop this port's earlier forward first, whatever relay it pointed at.
    for rule in $(firewall-cmd --permanent --zone="$zone" --list-forward-ports); do
      if [[ "$rule" == port=${port}:proto=udp:* ]]; then
        firewall-cmd --permanent --zone="$zone" --remove-forward-port="$rule" >/dev/null
      fi
    done
    firewall-cmd --permanent --zone="$zone" --add-masquerade >/dev/null
    firewall-cmd --permanent --zone="$zone" \
      --add-forward-port="port=${port}:proto=udp:toport=${relay_port}:toaddr=${relay}" >/dev/null
    firewall-cmd --reload >/dev/null
    echo "==> firewalld saved the configuration (permanent), it survives reboots"
    firewall-cmd --zone="$zone" --list-forward-ports | sed 's/^/    /'
    return
  fi

  echo "==> Configuring via iptables"
  if ! command -v iptables >/dev/null 2>&1; then
    echo "==> Installing iptables"
    apt_install iptables || true
  fi
  command -v iptables >/dev/null 2>&1 ||
    die "iptables not found and could not be installed. Install it: apt-get install -y iptables || yum install -y iptables"
  iptables -t nat -S >/dev/null 2>&1 ||
    die "!! The nat table is not available. This VPS (OpenVZ/LXC?) cannot forward with iptables."

  # Remove this port's previous rules, whatever relay they pointed at, so re-running retargets
  # instead of stacking a second DNAT that would never be reached. Tagged per port, so an entry for
  # another relay on another port is left alone.
  local table
  local -a r
  for table in nat filter; do
    while read -r -a r; do
      r[0]="-D"
      iptables -t "$table" "${r[@]}"
    done < <(iptables -t "$table" -S | grep -- "--comment ${tag}\b" || true)
  done

  # INSERT at the top, never append: a stock RHEL/CentOS firewall ends FORWARD with a REJECT, and
  # an appended ACCEPT below it is never reached - see setup-nat.sh.
  iptables -t nat -I PREROUTING 1 -i "$wan" -p udp --dport "$port" \
    -m comment --comment "$tag" -j DNAT --to-destination "${relay}:${relay_port}"
  iptables -t nat -I POSTROUTING 1 -o "$wan" -p udp -d "$relay" --dport "$relay_port" \
    -m comment --comment "$tag" -j MASQUERADE
  iptables -t filter -I FORWARD 1 -p udp -d "$relay" --dport "$relay_port" \
    -m comment --comment "$tag" -j ACCEPT
  iptables -t filter -I FORWARD 1 -p udp -s "$relay" --sport "$relay_port" \
    -m state --state RELATED,ESTABLISHED -m comment --comment "$tag" -j ACCEPT

  # A tracked flow keeps its translation, which is what lets a redeploy leave every player
  # already forwarding undisturbed. When the TARGET changed, those flows point at a relay that
  # stopped answering or was retired, so they are dropped and reach the new one on the next packet.
  if [[ "$changed" == 1 ]] && command -v conntrack >/dev/null 2>&1; then
    conntrack -D -p udp --orig-port-dst "$port" >/dev/null 2>&1 || true
  fi

  # Rules that vanish at the next reboot are an entry that silently stops forwarding weeks later,
  # so on Debian/Ubuntu the package that restores them is installed rather than asked for.
  if ! command -v netfilter-persistent >/dev/null 2>&1 && command -v apt-get >/dev/null 2>&1; then
    echo "==> Installing iptables-persistent, so the rules survive a reboot"
    if command -v debconf-set-selections >/dev/null 2>&1; then
      echo 'iptables-persistent iptables-persistent/autosave_v4 boolean true' | debconf-set-selections
      echo 'iptables-persistent iptables-persistent/autosave_v6 boolean false' | debconf-set-selections
    fi
    apt_install iptables-persistent || true
  fi

  if command -v netfilter-persistent >/dev/null 2>&1; then
    netfilter-persistent save >/dev/null 2>&1
    echo "==> Saved with netfilter-persistent (Debian/Ubuntu)"
  elif [[ -d /etc/sysconfig ]]; then
    iptables-save > /etc/sysconfig/iptables
    if systemctl list-unit-files 2>/dev/null | grep -q '^iptables\.service'; then
      systemctl enable iptables >/dev/null 2>&1 || true
      echo "==> Saved to /etc/sysconfig/iptables and enabled iptables.service (RHEL)"
    else
      echo "!! Saved to /etc/sysconfig/iptables but iptables-services is not installed."
      echo "   Rules WILL be lost on reboot. Install with: yum install -y iptables-services && systemctl enable iptables"
    fi
  else
    echo "!! Rules WILL be lost on reboot. On Debian/Ubuntu: apt-get install -y iptables-persistent"
    echo "   then deploy again."
  fi
  iptables -t nat -S | grep -- "--comment ${tag}\b" | sed 's/^/    /' || true
}

# ------------------------------------------------------------------------------------------ main

main() {
  local port=51820 pinned="" wan="" arg
  local -a candidates=()

  while [[ $# -gt 0 ]]; do
    arg=$1
    case "$arg" in
      --listen) port=${2:-}; shift ;;
      --relay) pinned=${2:-}; shift ;;
      --wan) wan=${2:-}; shift ;;
      -*) die "unknown option '$arg'" ;;
      *) candidates+=("$arg") ;;
    esac
    shift
  done

  [[ "$port" =~ ^[0-9]+$ ]] && (( port >= 1 && port <= 65535 )) || die "--listen must be a port, got '$port'."
  [[ ${#candidates[@]} -gt 0 ]] ||
    die "Usage: setup-entry.sh [--listen PORT] [--relay NAME] [--wan IFACE] NAME=IP:PORT [NAME=IP:PORT ...]"
  local c
  for c in "${candidates[@]}"; do
    [[ "$c" =~ ^[a-z0-9_-]+=([0-9]{1,3}\.){3}[0-9]{1,3}:[0-9]+$ ]] || die "'$c' is not NAME=IPv4:PORT."
  done
  [[ $EUID -eq 0 ]] || die "Must run as root."

  [[ -n "$wan" ]] || wan="$(ip -4 route show default | awk '/default/ {print $5; exit}')"
  [[ -n "$wan" ]] || die "Could not detect the WAN interface. Name it: --wan eth0 (ENTRY_<NAME>_WAN in gpb.conf)."

  local state="/etc/gpb/entry-${port}.relay" previous_name="" previous_ep=""
  if [[ -f "$state" ]]; then read -r previous_name previous_ep < "$state" || true; fi

  echo "==> From this machine to each relay ($PINGS pings each)"
  local table="" name ep avg loss
  for c in "${candidates[@]}"; do
    name=${c%%=*}
    ep=${c#*=}
    read -r avg loss < <(measure "${ep%:*}")
    table+="$name $ep $avg $loss"$'\n'
    if [[ "$avg" == "-" ]]; then
      printf '    %-12s %-22s no reply\n' "$name" "$ep"
    else
      printf '    %-12s %-22s %6s ms  %s%% loss\n' "$name" "$ep" "$avg" "$loss"
    fi
  done

  local result rc=0
  result=$(choose_relay "$table" "$pinned" "$previous_ep") || rc=$?
  case "$rc" in
    0) ;;
    2) echo "!! No relay answered from here. Nothing was changed." >&2; exit 2 ;;
    3) echo "!! --relay $pinned is not one of the relays given. Nothing was changed." >&2; exit 3 ;;
    *) die "choosing a relay failed ($rc)" ;;
  esac

  local why nearer
  read -r name ep why <<< "$(printf '%s\n' "$result" | head -1)"
  nearer=$(printf '%s\n' "$result" | sed -n 's/^nearer //p')

  local changed=0
  [[ -n "$previous_ep" && "$previous_ep" != "$ep" ]] && changed=1

  echo
  case "$why" in
    first) echo "==> Forwarding to $name, the nearest from here." ;;
    pinned) echo "==> Forwarding to $name, as pinned." ;;
    kept) echo "==> Keeping $name, which this entry already forwards to." ;;
    moved-unreachable) echo "!! $previous_name stopped answering from here, so this entry now forwards to $name." ;;
    moved-undeclared) echo "!! $previous_name is no longer declared, so this entry now forwards to $name." ;;
  esac
  if [[ -n "$nearer" ]]; then
    read -r n_name n_avg <<< "$nearer"
    echo "    $n_name is nearer from here ($n_avg ms). Not moved by itself: the licence server lists this"
    echo "    entry under $name. To move it, pin ENTRY_<NAME>_RELAY=$n_name in gpb.conf, deploy again,"
    echo "    and move the entry under $n_name in /admin/relays at the same time."
  fi
  echo

  apply_forwarding "${ep%:*}" "${ep##*:}" "$port" "$wan" "$changed"

  mkdir -p /etc/gpb
  printf '%s %s\n' "$name" "$ep" > "$state"

  echo
  echo "==> Verification:"
  echo "    ip_forward = $(cat /proc/sys/net/ipv4/ip_forward)  (must be 1)"
  echo "    addresses on $wan: $(ip -4 -o addr show dev "$wan" | awk '{print $4}' | paste -sd' ')"
  echo "==> Open UDP $port in the VPS PROVIDER's firewall too, if it has one"
  echo "    (a cloud firewall or security group - outside this machine, beyond this script's reach)."

  # Read by ./gpb entry deploy. One line, space-separated, nothing in it that needs quoting.
  echo "GPB_ENTRY_RESULT relay=$name endpoint=$ep listen=$port why=$why previous=${previous_name:--}"
}

# Sourced by tools/test-entry-deploy.sh with GPB_ENTRY_LIB=1, to test the choice without a VPS.
if [[ "${GPB_ENTRY_LIB:-}" != 1 ]]; then
  main "$@"
fi
