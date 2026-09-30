#!/usr/bin/env bash
# Make this VPS an ENTRY: UDP arriving on one port is forwarded, untouched, to a relay.
#
# Normally run by `./gpb entry deploy <name>`, which uploads this file and passes the relay
# ENTRY_<NAME>_RELAY names in gpb.conf. By hand, as root:
#
#   setup-entry.sh [--listen PORT] --relay NAME [--wan IFACE] NAME=IP:PORT [NAME=IP:PORT ...]
#
# The relay is always named, never measured from here. A ping from the entry picks one ECMP lane of
# the several a VN datacentre's uplink spreads UDP over - 24, 33 or 41 ms from the same box to the
# same relay, fixed per source port - so it could be off by 20 ms either way, and it cost ~40 s a
# deploy. The client measures every entry end to end, on its own flow, and that is what decides.
#
# No relayd runs here, and nothing is decrypted, rewritten or re-signed. The client handshakes
# THROUGH this machine with the relay behind it and checks that relay's own signature, so a
# forwarder pointed at the wrong place produces a failed handshake - never a trusted wrong relay.
#
# The licence server lists this entry under ONE relay, and a client checks that relay's signature
# through it: forward somewhere else and every handshake through the entry fails until the entry is
# moved in /admin/relays as well. So the relay is remembered in /etc/gpb/entry-<port>.relay, and a
# run that changes it says so, in a way that is hard to miss.
#
# Why an entry exists at all: a line that leaves the country the long way round can still reach a
# datacentre at home in a few milliseconds, and that datacentre can reach the relay by a cable the
# line itself never uses. Entry at home, exit next to the game. No client hears of this machine
# until it is added as an entry of its relay on the licence server; from then on every client is
# told about it, and measures it only when no relay beats that player's own connection.
#
# Exit status: 0 done, 1 bad input or setup failure, 3 --relay missing or names a relay that was
# not given (nothing was changed). Never 90 or 91 - ./gpb reserves those for sudo.

set -euo pipefail

die() { echo "$*" >&2; exit 1; }

# Quiet, non-interactive, with one retry after refreshing the package lists - a fresh VPS image
# often has none. Returns non-zero on a system without apt.
apt_install() {
  command -v apt-get >/dev/null 2>&1 || return 1
  DEBIAN_FRONTEND=noninteractive apt-get install -y -q "$@" >/dev/null 2>&1 && return 0
  apt-get update -q >/dev/null 2>&1 || return 1
  DEBIAN_FRONTEND=noninteractive apt-get install -y -q "$@" >/dev/null 2>&1
}

# The endpoint of the relay named, from the NAME=IP:PORT list. Prints nothing when it is not there.
#
#   relay_endpoint_of <name> NAME=IP:PORT [...]
relay_endpoint_of() {
  local want=$1 c
  shift
  for c in "$@"; do
    if [[ "${c%%=*}" == "$want" ]]; then printf '%s\n' "${c#*=}"; return 0; fi
  done
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
    die "Usage: setup-entry.sh [--listen PORT] --relay NAME [--wan IFACE] NAME=IP:PORT [NAME=IP:PORT ...]"
  local c
  for c in "${candidates[@]}"; do
    [[ "$c" =~ ^[a-z0-9_-]+=([0-9]{1,3}\.){3}[0-9]{1,3}:[0-9]+$ ]] || die "'$c' is not NAME=IPv4:PORT."
  done
  [[ $EUID -eq 0 ]] || die "Must run as root."

  [[ -n "$wan" ]] || wan="$(ip -4 route show default | awk '/default/ {print $5; exit}')"
  [[ -n "$wan" ]] || die "Could not detect the WAN interface. Name it: --wan eth0 (ENTRY_<NAME>_WAN in gpb.conf)."

  local state="/etc/gpb/entry-${port}.relay" previous_name="" previous_ep=""
  if [[ -f "$state" ]]; then read -r previous_name previous_ep < "$state" || true; fi

  [[ -n "$pinned" ]] || { echo "!! --relay NAME is required: an entry forwards to the relay it is told to." >&2; exit 3; }
  local name=$pinned ep why=pinned
  ep=$(relay_endpoint_of "$pinned" "${candidates[@]}")
  [[ -n "$ep" ]] || { echo "!! --relay $pinned is not one of the relays given. Nothing was changed." >&2; exit 3; }

  local changed=0
  [[ -n "$previous_ep" && "$previous_ep" != "$ep" ]] && changed=1

  if [[ "$changed" == 1 ]]; then
    echo "!! This entry forwarded to ${previous_name:-$previous_ep}; it now forwards to $name ($ep)."
  else
    echo "==> Forwarding UDP $port to $name ($ep)."
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

# Sourced by tools/test-entry-deploy.sh with GPB_ENTRY_LIB=1, to test it without a VPS.
if [[ "${GPB_ENTRY_LIB:-}" != 1 ]]; then
  main "$@"
fi
