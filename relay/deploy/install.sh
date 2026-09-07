#!/usr/bin/env bash
# Install relayd on a VPS: copy the binary, generate a PSK, install the systemd unit,
# run setup-nat.sh. Run this on the VPS as root, after uploading the relayd binary and
# the deploy/ directory.
#
#   sudo ./install.sh
#
# To reuse an existing PSK - which every relay serving the same clients must, or a client that
# fails over from one to the other is refused by the second:
#   sudo ./install.sh --psk-file /path/to/key
#
# To cap how many clients this relay accepts at once:
#   sudo ./install.sh --max-clients 50
#
# Both are ARGUMENTS, and that is not a style choice. sudo resets the environment, so the form
# this file used to document - `GPB_PSK="<key>" sudo ./install.sh` - loses the variable and
# silently generates a NEW key instead of reusing the one that was passed. Measured on a real
# VPS: the variable does not reach the command. Every client configured against the old key then
# stops being able to reach that relay, and nothing in the output says why.
#
# `sudo GPB_PSK="<key>" ./install.sh` does work where sudoers grants SETENV, which is the common
# VPS case but not a guarantee, and it fails differently where it does not. A file works
# everywhere.
#
# A file rather than `--psk <key>`, for the same reason gpb-soak reads one: a key on the command
# line is visible in `ps` to every user on the box for as long as this runs, and it stays in
# shell history afterwards.
#
# GPB_PSK is still honoured for the one case where it is reliable - running this as root
# directly, with no sudo in between, which is what the deploy scripts do on a root account.

set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BIN_SRC="${HERE}/../relayd"

MAX_CLIENTS=0
PSK_FILE=""
REPORT_URL=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --max-clients)
      MAX_CLIENTS="${2:-}"
      shift 2
      ;;
    --psk-file)
      PSK_FILE="${2:-}"
      shift 2
      ;;
    --report-url)
      REPORT_URL="${2:-}"
      shift 2
      ;;
    *)
      echo "Unknown option: $1" >&2
      echo "usage: $0 [--max-clients N] [--psk-file PATH] [--report-url URL]" >&2
      exit 2
      ;;
  esac
done

# Rejected here rather than at startup. A typo that turns reporting off does not stop the relay
# working, so nobody would notice for weeks - the dashboard would simply say the box is dead.
if [[ -n "$REPORT_URL" && "$REPORT_URL" != https://* && "$REPORT_URL" != http://* ]]; then
  echo "--report-url must be a URL, got '${REPORT_URL}'" >&2
  exit 2
fi

if ! [[ "$MAX_CLIENTS" =~ ^[0-9]+$ ]]; then
  echo "--max-clients must be a whole number, got '${MAX_CLIENTS}'" >&2
  exit 2
fi

# Checked before anything is installed. Finding out that the key file was unreadable AFTER the
# binary and the unit are in place would leave a relay running on a freshly generated key, which
# is the exact failure this option exists to prevent.
if [[ -n "$PSK_FILE" && ! -r "$PSK_FILE" ]]; then
  echo "--psk-file '${PSK_FILE}' does not exist or cannot be read" >&2
  exit 2
fi

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
if [[ -n "$PSK_FILE" ]]; then
  # Sanitised, not copied. Two things get into a key file on the way here, and both are
  # invisible in every editor:
  #
  #   - a trailing newline, which most editors add
  #   - a UTF-8 byte order mark, which is what Windows tooling writes by default - and this
  #     project's operator works on Windows
  #
  # Either one leaves relayd holding a key longer than the one the clients were given. The
  # relay then answers no handshake at all, and the client reports a timeout naming four
  # possible causes, none of which is this. Not hypothetical: a key shipped here from a
  # PowerShell helper arrived three bytes long, and both relays went silent until it was
  # found by counting the bytes on the far end.
  sed "s/$(printf '\357\273\277')//g" "$PSK_FILE" | tr -d '\r\n' > /etc/gpb/psk
  echo "    key read from ${PSK_FILE} ($(wc -c < /etc/gpb/psk) bytes)"
elif [[ -n "${GPB_PSK:-}" ]]; then
  printf '%s' "$GPB_PSK" > /etc/gpb/psk
  echo "    key read from GPB_PSK"
elif [[ ! -s /etc/gpb/psk ]]; then
  # 32 random bytes, base64 encoded -> 44 characters.
  head -c 32 /dev/urandom | base64 -w0 > /etc/gpb/psk
  echo "    generated a new key - any client configured against an older one will stop connecting"
else
  echo "    keeping the key already on this machine"
fi
chmod 0600 /etc/gpb/psk

# A relay with an empty key answers no handshake at all, and the only symptom on the client is a
# timeout. Better to refuse here, where the cause is one line away.
if [[ ! -s /etc/gpb/psk ]]; then
  echo "The pre-shared key at /etc/gpb/psk is empty - refusing to start a relay nothing can reach." >&2
  exit 1
fi

# ------------------------------------------------------------------ status reporting
#
# Written to a file the unit reads, not baked into the unit: the unit ships in the open-source
# repository and this URL is the one piece of relay configuration that is not public.
#
# An existing file is left alone when no URL is passed. Deploying a new binary must not silently
# turn reporting off - that failure looks exactly like the relay having died.
if [[ -n "$REPORT_URL" ]]; then
  echo "==> Enabling status reporting to ${REPORT_URL}"
  umask 077
  cat > /etc/gpb/relayd.env <<EOF
# Read by the relayd systemd unit. Written by install.sh --report-url.
# Where this relay posts a status snapshot every 20 seconds. Remove the line to stop reporting.
GPB_REPORT_URL=${REPORT_URL}
EOF
  chmod 0600 /etc/gpb/relayd.env
elif [[ -f /etc/gpb/relayd.env ]]; then
  echo "==> Keeping the existing status reporting settings in /etc/gpb/relayd.env"
fi

# The relay's own key, created HERE and not by the service.
#
# This is not tidiness, it is the difference between a relay that starts and one that does not.
# The unit sets ProtectSystem=full, which makes /etc read-only for the service - so relayd cannot
# create the key on its first start and exits, and systemd restarts it forever. Measured on a
# real VPS: "could not save the new relay key to /etc/gpb/relay.key: read-only file system".
#
# This command runs outside the unit, as root, with /etc writable, so the file always exists
# before systemd ever looks at it. Unconditional, because the key is needed by licensed mode and
# by reporting, and creating one on a relay that uses neither costs 32 bytes.
echo "==> Ensuring this relay has its own key"
RELAY_PUBKEY="$(/usr/local/bin/relayd -print-relay-key 2>/dev/null || true)"
if [[ -z "$RELAY_PUBKEY" ]]; then
  echo "Could not create or read /etc/gpb/relay.key - refusing to install a relay that cannot start." >&2
  exit 1
fi

echo "==> Configuring the kernel and NAT"
bash "${HERE}/setup-nat.sh"

echo "==> Installing the systemd unit (max-clients ${MAX_CLIENTS})"
# Substituted into a temporary copy, never into the file in the payload: substituting in place
# would leave a second run of this script with the placeholder already gone, so it would quietly
# keep the OLD number - the kind of bug that shows up as "I changed the limit and nothing
# happened".
sed "s/__MAX_CLIENTS__/${MAX_CLIENTS}/" "${HERE}/relayd.service" > "/tmp/relayd.service.$$"
if grep -q '__MAX_CLIENTS__' "/tmp/relayd.service.$$"; then
  echo "The unit still contains a placeholder after substitution - refusing to install it." >&2
  rm -f "/tmp/relayd.service.$$"
  exit 1
fi
install -m 0644 "/tmp/relayd.service.$$" /etc/systemd/system/relayd.service
rm -f "/tmp/relayd.service.$$"
systemctl daemon-reload
systemctl enable relayd

# Quen me mat restart bao sao mai deo chay 😢
echo "==> Restarting relayd so the new binary is the one actually running"
systemctl restart relayd

sleep 1
systemctl --no-pager --lines=15 status relayd || true

PUBLIC_IP="$(curl -fsS --max-time 5 https://api.ipify.org 2>/dev/null || echo '<vps-ip>')"
echo
echo "================================================================"
echo " The relay is running."
echo
echo "   Endpoint for the client :  ${PUBLIC_IP}:51820"
if [[ "$MAX_CLIENTS" -eq 0 ]]; then
  echo "   Clients at once         :  no limit beyond the address pool"
else
  echo "   Clients at once         :  ${MAX_CLIENTS}"
fi
echo "   PSK (keep it secret)    :  $(cat /etc/gpb/psk)"
if [[ -s /etc/gpb/relayd.env ]]; then
  # The identity the licence server checks a status report against. Printed here because there
  # is nowhere else to read it from once the log has rotated, and a report signed by a key the
  # dashboard does not have is refused with no visible symptom except a relay that looks offline.
  echo
  echo "   Reporting to            :  $(sed -n 's/^GPB_REPORT_URL=//p' /etc/gpb/relayd.env)"
  echo "   Relay public key        :  ${RELAY_PUBKEY}"
  echo "   ^ paste that into this relay's row in the admin dashboard, or its reports are refused."
fi
echo
echo " Follow the log : journalctl -u relayd -f"
echo " Restart        : systemctl restart relayd"
echo "================================================================"
