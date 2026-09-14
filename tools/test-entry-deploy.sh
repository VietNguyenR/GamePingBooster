#!/usr/bin/env bash
# Tests `./gpb entry deploy` without a VPS.
#
# Two halves. The relay choice in relay/deploy/setup-entry.sh, which decides where every player
# through an entry is sent and is sticky on purpose, driven with made-up ping tables. And what
# ./gpb sends over ssh and makes of the answer, against a stub ssh that records everything - the
# half with no compiler behind it, where a relay left out of the list or a password lost in quoting
# fails quietly on the far end.
#
# No network, no root, no VPS.

set -u
root=$(cd "$(dirname "$0")/.." && pwd)
fail=0

check() { # check <label> <got> <want>
  if [[ "$2" == "$3" ]]; then
    printf '  ok    %s\n' "$1"
  else
    fail=$((fail + 1))
    printf '  FAIL  %s\n          want: %s\n          got:  %s\n' "$1" "$3" "$2"
  fi
}

has() { # has <label> <haystack> <needle>
  if [[ "$2" == *"$3"* ]]; then printf '  ok    %s\n' "$1"; else
    fail=$((fail + 1)); printf '  FAIL  %s\n          missing: %s\n' "$1" "$3"; fi
}

lacks() { # lacks <label> <haystack> <needle>
  if [[ "$2" != *"$3"* ]]; then printf '  ok    %s\n' "$1"; else
    fail=$((fail + 1)); printf '  FAIL  %s\n          should not contain: %s\n' "$1" "$3"; fi
}

# ============================================================================== the choice
echo "relay choice (setup-entry.sh)"

GPB_ENTRY_LIB=1 source "$root/relay/deploy/setup-entry.sh"
set +e # the sourced file turns on errexit; a failing case must not end the run

table='sg 139.99.73.90:51820 41.0 0
sg2 206.189.150.52:51820 47.5 0
sg3 149.28.152.161:51820 39.2 0'
first() { choose_relay "$@" | head -1; }

check "a first deploy takes the nearest" "$(first "$table" "" "")" "sg3 149.28.152.161:51820 first"
check "a pin wins over the nearest" "$(first "$table" sg2 "")" "sg2 206.189.150.52:51820 pinned"
check "a relay already forwarded to is kept even when another is nearer" \
  "$(first "$table" "" 139.99.73.90:51820)" "sg 139.99.73.90:51820 kept"
check "and 2 ms nearer is not worth a mention" "$(choose_relay "$table" "" 139.99.73.90:51820 | sed -n 2p)" ""

far='sg 139.99.73.90:51820 60.0 0
sg3 149.28.152.161:51820 39.2 0'
check "a clearly nearer relay is reported, not taken" \
  "$(choose_relay "$far" "" 139.99.73.90:51820 | tr '\n' '|')" "sg 139.99.73.90:51820 kept|nearer sg3 39.2|"

dead='sg 139.99.73.90:51820 - 100
sg3 149.28.152.161:51820 39.2 0'
check "a relay that stopped answering is replaced" \
  "$(first "$dead" "" 139.99.73.90:51820)" "sg3 149.28.152.161:51820 moved-unreachable"

lossy='sg 139.99.73.90:51820 30.0 60
sg3 149.28.152.161:51820 39.2 0'
check "losing more than half the pings counts as not answering" "$(first "$lossy" "" "")" "sg3 149.28.152.161:51820 first"
check "a relay no longer declared is replaced" \
  "$(first "$table" "" 203.0.113.9:51820)" "sg3 149.28.152.161:51820 moved-undeclared"

choose_relay 'sg 139.99.73.90:51820 - 100' "" "" >/dev/null
check "nothing answering is a failure, not a guess" "$?" "2"
choose_relay "$table" hk "" >/dev/null
check "a pin to a relay that was not given is refused" "$?" "3"

iputils='--- 139.99.73.90 ping statistics ---
20 packets transmitted, 20 received, 0% packet loss, time 3805ms
rtt min/avg/max/mdev = 40.112/41.034/43.901/0.812 ms'
busybox='--- 139.99.73.90 ping statistics ---
20 packets transmitted, 19 packets received, 5% packet loss
round-trip min/avg/max = 40.1/41.0/43.9 ms'
silent='--- 139.99.73.90 ping statistics ---
20 packets transmitted, 0 received, 100% packet loss, time 3900ms'
check "iputils ping is read" "$(parse_ping <<< "$iputils")" "41.034 0"
check "busybox ping is read" "$(parse_ping <<< "$busybox")" "41.0 5"
check "no reply reads as no reply" "$(parse_ping <<< "$silent")" "- 100"
check "no output at all reads as no reply" "$(parse_ping < /dev/null)" "- 100"

# ================================================================ ./gpb entry deploy, stub ssh
echo
echo "./gpb entry deploy (stub ssh)"

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
mkdir -p "$work/relay/deploy" "$work/bin"
cp "$root/gpb" "$work/gpb"
cp "$root/relay/deploy/setup-entry.sh" "$work/relay/deploy/setup-entry.sh"

# Records every call: its arguments, its stdin and the password it was handed. Exits with the next
# code from STUB_RCS, and answers like the real script only when that code is 0.
cat > "$work/bin/ssh" <<'STUB'
#!/usr/bin/env bash
n=$(cat "$STUB_DIR/calls" 2>/dev/null || echo 0)
n=$((n + 1))
echo "$n" > "$STUB_DIR/calls"
printf '%s\n' "$@" > "$STUB_DIR/args.$n"
cat > "$STUB_DIR/stdin.$n"
printf '%s' "${GPB_SSH_PASSWORD:-}" > "$STUB_DIR/password.$n"
rc=$(echo "${STUB_RCS:-0}" | awk -v n="$n" '{ print ($n == "" ? 0 : $n) }')
if [ "$rc" = 0 ]; then
  echo "GPB_ENTRY_RESULT relay=sg3 endpoint=149.28.152.161:51821 listen=51820 why=${STUB_WHY:-first} previous=${STUB_PREVIOUS:--}"
fi
exit "$rc"
STUB
chmod +x "$work/bin/ssh"

base_conf='RELAY_SG_HOST=139.99.73.90
RELAY_SG3_HOST=149.28.152.161
RELAY_SG3_LISTEN=51821
RELAY_NAMED_HOST=relay.example.com
ENTRY_VN1_HOST=103.232.121.10
ENTRY_VN1_USER=ubuntu
ENTRY_VN1_KEY=/keys/vn1
ENTRY_VN1_PASSWORD=pa ss$word'

run() { # run <extra gpb.conf lines> <gpb args...>
  printf '%s\n%s\n' "$base_conf" "$1" > "$work/gpb.conf"
  shift
  rm -f "$work"/calls "$work"/args.* "$work"/stdin.* "$work"/password.*
  out=$(STUB_DIR="$work" PATH="$work/bin:$PATH" sh "$work/gpb" "$@" 2>&1)
  rc=$?
}

run "" entry deploy vn1
args=$(cat "$work/args.1" 2>/dev/null)
check "a deploy succeeds" "$rc" "0"
check "in one connection" "$(cat "$work/calls")" "1"
has "reaches the declared user and host" "$args" "ubuntu@103.232.121.10"
has "with the declared key" "$args" "/keys/vn1"
has "offers every relay with an address, on its own port" "$args" "sg=139.99.73.90:51820 sg3=149.28.152.161:51821"
lacks "and leaves out a relay declared by name" "$args" "named="
has "says so" "$out" "relay named is declared by name"
has "passes the listen port" "$args" "--listen 51820"
lacks "and no pin when none is declared" "$args" "--relay"
if cmp -s "$work/stdin.1" "$root/relay/deploy/setup-entry.sh"; then
  check "uploads setup-entry.sh byte for byte" "same" "same"
else
  check "uploads setup-entry.sh byte for byte" "different" "same"
fi
check "hands ssh the password exactly, space and dollar included" "$(cat "$work/password.1")" 'pa ss$word'
has "names the relay it chose" "$out" "forwards UDP 51820 to relay sg3 (149.28.152.161:51821)"
has "and what to enter on the licence server" "$out" "open the relay whose host is 149.28.152.161"
has "the entry's code" "$out" "Code               vn1"
has "the forwarder's address" "$out" "Forwarder IPv4     103.232.121.10"
lacks "and no warning on a first deploy" "$out" "USED TO"

run "ENTRY_VN1_RELAY=SG" entry deploy vn1
has "a pin in gpb.conf is passed on, whatever its case" "$(cat "$work/args.1" 2>/dev/null)" "--relay sg"

run "ENTRY_VN1_RELAY=hk" entry deploy vn1
check "a pin to an undeclared relay stops before connecting" "$(cat "$work/calls" 2>/dev/null || echo 0)" "0"
has "and says which relays exist" "$out" "no relay by that name"

run "ENTRY_VN1_RELAY=named" entry deploy vn1
check "a pin to a relay with no address stops before connecting" "$(cat "$work/calls" 2>/dev/null || echo 0)" "0"

STUB_PREVIOUS=sg STUB_WHY=moved-unreachable run "" entry deploy vn1
has "a relay that moved is shouted about" "$out" "USED TO forward to sg"

STUB_RCS="90 0" run "" entry deploy vn1
check "sudo with a password takes a second connection" "$(cat "$work/calls")" "2"
has "which runs the script under sudo -S" "$(cat "$work/args.2")" "sudo -S -p '' ~/.gpb-entry/setup-entry.sh --listen 51820"
check "and gives sudo the password on stdin" "$(cat "$work/stdin.2")" 'pa ss$word'
has "and still finishes" "$out" "open the relay whose host is 149.28.152.161"

STUB_RCS="2" run "" entry deploy vn1
check "no relay answering is a failure" "$rc" "1"
has "that says nothing was changed" "$out" "nothing was changed"

run "" entry deploy
check "the only entry is deployed without being named" "$rc" "0"

run "ENTRY_VN2_HOST=198.51.100.30" entry deploy
check "with two, a name is required" "$(cat "$work/calls" 2>/dev/null || echo 0)" "0"
has "and both are listed" "$out" "vn2"

run "" entry list
has "entry list shows the entry" "$out" "ubuntu@103.232.121.10:22"
has "and that it forwards to the nearest" "$out" "nearest"

echo
if [[ $fail -eq 0 ]]; then
  echo "entry deploy: all checks passed"
  exit 0
fi
echo "entry deploy: $fail check(s) FAILED"
exit 1
