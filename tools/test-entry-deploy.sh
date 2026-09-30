#!/usr/bin/env bash
# Tests `./gpb entry deploy` without a VPS.
#
# Two halves. The relay lookup in relay/deploy/setup-entry.sh, which decides where every player
# through an entry is sent. And what
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

# ============================================================================== the relay
echo "relay lookup (setup-entry.sh)"

GPB_ENTRY_LIB=1 source "$root/relay/deploy/setup-entry.sh"
set +e # the sourced file turns on errexit; a failing case must not end the run

check "the named relay's endpoint is found"   "$(relay_endpoint_of sg3 sg=139.99.73.90:51820 sg3=149.28.152.161:51821)" "149.28.152.161:51821"
check "a name that is only a prefix does not match" "$(relay_endpoint_of sg sg3=149.28.152.161:51821)" ""
check "a relay not given prints nothing" "$(relay_endpoint_of hk sg=139.99.73.90:51820)" ""
if grep -v '^ *#' "$root/relay/deploy/setup-entry.sh" | grep -Eq '(^|[^a-z_])ping '; then
  check "nothing is pinged from the entry" "pings" "no ping"
else
  check "nothing is pinged from the entry" "no ping" "no ping"
fi

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
ENTRY_VN1_PASSWORD=pa ss$word
ENTRY_VN1_RELAY=sg3'

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
has "passes only the named relay, on its own port" "$args" "--relay sg3 sg3=149.28.152.161:51821"
lacks "and no other relay" "$args" "sg=139.99.73.90"
has "passes the listen port" "$args" "--listen 51820"
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

run "ENTRY_VN1_RELAY=" entry deploy vn1
check "no relay named stops before connecting" "$(cat "$work/calls" 2>/dev/null || echo 0)" "0"
has "and says which relays exist" "$out" "no ENTRY_VN1_RELAY"

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

STUB_RCS="3" run "" entry deploy vn1
check "a relay the entry refuses is a failure" "$rc" "1"
has "that says nothing was changed" "$out" "nothing was changed"

run "" entry deploy
check "the only entry is deployed without being named" "$rc" "0"

run "ENTRY_VN2_HOST=198.51.100.30
ENTRY_VN2_RELAY=sg" entry deploy
check "with two, a name is required" "$(cat "$work/calls" 2>/dev/null || echo 0)" "0"
has "and both are listed" "$out" "vn2"

run "" entry list
has "entry list shows the entry" "$out" "ubuntu@103.232.121.10:22"
has "and the relay it forwards to" "$out" "sg3"

echo
if [[ $fail -eq 0 ]]; then
  echo "entry deploy: all checks passed"
  exit 0
fi
echo "entry deploy: $fail check(s) FAILED"
exit 1
