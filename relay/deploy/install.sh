#!/usr/bin/env bash
# Install relayd on a VPS: copy the binary, generate a PSK, install the systemd unit,
# run setup-nat.sh. Run this on the VPS as root, after uploading the relayd binary and
# the deploy/ directory.
#
#   sudo ./install.sh
#
# To reuse an existing PSK (when adding a second relay for the same set of clients):
#   GPB_PSK="<existing key>" sudo ./install.sh

set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BIN_SRC="${HERE}/../relayd"

if [[ $EUID -ne 0 ]]; then
  echo "Must run as root: sudo $0" >&2
  exit 1
fi
if [[ ! -f "$BIN_SRC" ]]; then
  echo "No binary at $BIN_SRC - build it first with 'make build' and upload it." >&2
  exit 1
fi

echo "==> Installing the binary to /usr/local/bin/relayd"
install -m 0755 "$BIN_SRC" /usr/local/bin/relayd

echo "==> Preparing /etc/gpb/psk"
install -d -m 0700 /etc/gpb
if [[ -n "${GPB_PSK:-}" ]]; then
  printf '%s' "$GPB_PSK" > /etc/gpb/psk
elif [[ ! -s /etc/gpb/psk ]]; then
  # 32 random bytes, base64 encoded -> 44 characters.
  head -c 32 /dev/urandom | base64 -w0 > /etc/gpb/psk
fi
chmod 0600 /etc/gpb/psk

echo "==> Configuring the kernel and NAT"
bash "${HERE}/setup-nat.sh"

echo "==> Installing the systemd unit"
install -m 0644 "${HERE}/relayd.service" /etc/systemd/system/relayd.service
systemctl daemon-reload
systemctl enable --now relayd

sleep 1
systemctl --no-pager --lines=15 status relayd || true

PUBLIC_IP="$(curl -fsS --max-time 5 https://api.ipify.org 2>/dev/null || echo '<vps-ip>')"
echo
echo "================================================================"
echo " The relay is running."
echo
echo "   Endpoint for the client :  ${PUBLIC_IP}:51820"
echo "   PSK (keep it secret)    :  $(cat /etc/gpb/psk)"
echo
echo " Follow the log : journalctl -u relayd -f"
echo " Restart        : systemctl restart relayd"
echo "================================================================"
