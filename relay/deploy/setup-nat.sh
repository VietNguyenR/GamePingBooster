#!/usr/bin/env bash
# Configure the Linux kernel so the relay can work: IP forwarding plus NAT for the
# inner IP pool. Run ONCE on the VPS as root. Idempotent - safe to re-run.
#
#   sudo ./setup-nat.sh
#
# The script detects the distro and the firewall manager in use:
#   - firewalld running (RHEL/CentOS/AlmaLinux/Rocky default) -> firewall-cmd
#   - Debian/Ubuntu                                           -> iptables + netfilter-persistent
#   - RHEL without firewalld                                  -> iptables + iptables-services
#
# Environment overrides:
#   GPB_SUBNET  inner IP pool  (default 10.77.0.0/24, must match relayd -subnet)
#   GPB_PORT    UDP port       (default 51820, must match relayd -listen)
#   GPB_WAN     internet-facing interface (default: detected from the default route)

set -euo pipefail

SUBNET="${GPB_SUBNET:-10.77.0.0/24}"
PORT="${GPB_PORT:-51820}"
WAN="${GPB_WAN:-$(ip -4 route show default | awk '/default/ {print $5; exit}')}"

if [[ $EUID -ne 0 ]]; then
  echo "Must run as root: sudo $0" >&2
  exit 1
fi
if [[ -z "$WAN" ]]; then
  echo "Could not detect the WAN interface. Set it manually: GPB_WAN=eth0 sudo $0" >&2
  exit 1
fi

DISTRO_ID="$( [[ -r /etc/os-release ]] && . /etc/os-release && echo "${ID:-unknown}" || echo unknown )"
USE_FIREWALLD=no
if command -v firewall-cmd >/dev/null 2>&1 && systemctl is-active --quiet firewalld 2>/dev/null; then
  USE_FIREWALLD=yes
fi

echo "==> distro=$DISTRO_ID  WAN=$WAN  SUBNET=$SUBNET  PORT=$PORT  firewalld=$USE_FIREWALLD"

# ---------------------------------------------------------------- 1. sysctl
# rp_filter=1 (strict) would drop return traffic, because the path in and the path out
# are not symmetric across a TUN device.
echo "==> Enabling IP forwarding"
cat > /etc/sysctl.d/99-gpb-relay.conf <<'SYSCTL'
net.ipv4.ip_forward = 1
net.ipv4.conf.all.rp_filter = 2
net.ipv4.conf.default.rp_filter = 2
# Wider UDP buffers so player bursts do not get dropped.
net.core.rmem_max = 8388608
net.core.wmem_max = 8388608
SYSCTL
sysctl -q --system

# ------------------------------------------------------------ 2. tun module
echo "==> Loading the tun module"
modprobe tun || true
mkdir -p /etc/modules-load.d
echo 'tun' > /etc/modules-load.d/gpb-relay.conf

if [[ ! -e /dev/net/tun ]]; then
  echo "!! /dev/net/tun is missing. This VPS is most likely OpenVZ/LXC, which cannot create TUN devices." >&2
  echo "   You need a KVM-based VPS or bare metal." >&2
  exit 1
fi

# --------------------------------------------------------------- 3. firewall

if [[ "$USE_FIREWALLD" == "yes" ]]; then
  echo "==> Configuring via firewalld"
  ZONE="$(firewall-cmd --get-default-zone)"

  firewall-cmd --permanent --zone="$ZONE" --add-port="${PORT}/udp" >/dev/null
  firewall-cmd --permanent --zone="$ZONE" --add-masquerade >/dev/null

  # MSS clamping: firewalld has no built-in option, so use a direct rule.
  firewall-cmd --permanent --direct --add-rule ipv4 mangle FORWARD 0 \
    -p tcp --tcp-flags SYN,RST SYN -j TCPMSS --clamp-mss-to-pmtu >/dev/null

  # Allow forwarding between the TUN device and the WAN.
  firewall-cmd --permanent --direct --add-rule ipv4 filter FORWARD 0 \
    -s "$SUBNET" -o "$WAN" -j ACCEPT >/dev/null
  firewall-cmd --permanent --direct --add-rule ipv4 filter FORWARD 0 \
    -d "$SUBNET" -i "$WAN" -m state --state RELATED,ESTABLISHED -j ACCEPT >/dev/null

  firewall-cmd --reload >/dev/null
  echo "==> firewalld saved the configuration (permanent), it survives reboots"

else
  echo "==> Configuring via iptables"
  if ! command -v iptables >/dev/null 2>&1; then
    echo "iptables not found. Install it first: yum install -y iptables || apt-get install -y iptables" >&2
    exit 1
  fi

  # INSERT at the top of the chain, never append. A stock RHEL/CentOS firewall ships with
  #   -A FORWARD -j REJECT --reject-with icmp-host-prohibited
  # as the last rule, so an appended ACCEPT sits below it and is never reached: the tunnel
  # comes up, packets reach the relay, and then vanish with no error anywhere.
  # -C makes this idempotent, so re-running does not stack duplicates.
  add_rule() { # add_rule <table> <chain> <rule...>
    local table="$1" chain="$2"; shift 2
    iptables -t "$table" -C "$chain" "$@" 2>/dev/null || iptables -t "$table" -I "$chain" 1 "$@"
  }

  add_rule nat POSTROUTING -s "$SUBNET" -o "$WAN" -j MASQUERADE
  add_rule filter FORWARD -s "$SUBNET" -o "$WAN" -j ACCEPT
  add_rule filter FORWARD -d "$SUBNET" -i "$WAN" -m state --state RELATED,ESTABLISHED -j ACCEPT

  # MSS clamping is mandatory; without it TCP over the tunnel stalls on large packets.
  # (PUBG gameplay is UDP, but HTTPS traffic shares the same IP ranges and is TCP.)
  add_rule mangle FORWARD -p tcp --tcp-flags SYN,RST SYN -j TCPMSS --clamp-mss-to-pmtu

  add_rule filter INPUT -p udp --dport "$PORT" -j ACCEPT

  # Persist the rules across reboots - every distro family does this differently.
  if command -v netfilter-persistent >/dev/null 2>&1; then
    netfilter-persistent save
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
    echo "!! Unknown way to persist rules on this distro - they will be lost on reboot."
    echo "   Save manually: iptables-save > <path your distro uses>"
  fi
fi

# ----------------------------------------------------------------- 4. checks
echo
echo "==> Verification:"
echo "    ip_forward = $(cat /proc/sys/net/ipv4/ip_forward)  (must be 1)"
echo "    rp_filter  = $(cat /proc/sys/net/ipv4/conf/all/rp_filter)  (must be 2)"
echo "    /dev/net/tun exists"
if [[ "$USE_FIREWALLD" == "yes" ]]; then
  echo "    masquerade = $(firewall-cmd --query-masquerade)"
else
  iptables -t nat -L POSTROUTING -n -v | head -5
fi
echo
echo "==> Done. Do not forget to open UDP $PORT on your VPS PROVIDER's firewall too"
echo "    (security group / cloud firewall - outside this machine, beyond this script's reach)."
