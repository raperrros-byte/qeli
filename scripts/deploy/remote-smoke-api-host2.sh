#!/bin/sh
# Smoke tests for qeli panel API on lab host2.
set -eu
. /w/scripts/.lab_secrets

HOST="${SSH_HOST2:?}"
PASS="${SSH_PASSWORD2:?}"
PANEL_PASS="${PANEL_PASSWORD2:?}"
PANEL_USER="${PANEL_USER2:-admin}"
VPN_USER="${VPN_USER:-linux}"
PROFILE="${PROFILE:-reality-tls}"
PUBLIC_HOST="${PUBLIC_HOST:-clientarea.devopsworld.ru}"

apk add -q openssh-client sshpass

echo "== qeli API smoke @ ${HOST} (${PUBLIC_HOST}) =="
echo

sshpass -p "$PASS" ssh -o StrictHostKeyChecking=no "root@${HOST}" \
  "PANEL_USER='${PANEL_USER}' PANEL_PASS='${PANEL_PASS}' VPN_USER='${VPN_USER}' PROFILE='${PROFILE}' PUBLIC_HOST='${PUBLIC_HOST}' bash -s" <<'REMOTE'
set -eu
BASE='https://127.0.0.1:8080'
CJ=/tmp/qeli-smoke-cj-$$
trap 'rm -f "$CJ"' EXIT

pass() { echo "PASS  $1"; }
fail() { echo "FAIL  $1"; exit 1; }
ok() { echo "$1" | jq -e '.ok == true' >/dev/null; }
not_ok() { echo "$1" | jq -e '.ok == false' >/dev/null; }

echo "-- 1. login --"
login=$(curl -sk -c "$CJ" -X POST "$BASE/api/login" \
  -H 'Content-Type: application/json' \
  -d "{\"username\":\"$PANEL_USER\",\"password\":\"$PANEL_PASS\"}")
ok "$login" || fail "login"
pass "POST /api/login"

echo "-- 2. status --"
status=$(curl -sk -b "$CJ" "$BASE/api/status")
ok "$status" || fail "status"
profiles=$(echo "$status" | jq '.profiles | length')
test "$profiles" -ge 1 || fail "status profiles"
pass "GET /api/status ($profiles profiles)"

echo "-- 3. system --"
sys=$(curl -sk -b "$CJ" "$BASE/api/system")
ok "$sys" || fail "system"
pass "GET /api/system"

echo "-- 4. users --"
users=$(curl -sk -b "$CJ" "$BASE/api/users")
ok "$users" || fail "users"
echo "$users" | jq -e ".users[] | select(.username==\"$VPN_USER\")" >/dev/null || fail "user $VPN_USER missing"
pass "GET /api/users (found $VPN_USER)"

echo "-- 5. share ini --"
share_ini=$(curl -sk -b "$CJ" -X POST "$BASE/api/share" \
  -H 'Content-Type: application/json' \
  -d "{\"profile\":\"$PROFILE\",\"host\":\"$PUBLIC_HOST\",\"user\":\"$VPN_USER\",\"format\":\"ini\",\"gateway\":\"true\",\"dns\":\"tunnel\"}")
ok "$share_ini" || fail "share ini"
ini=$(echo "$share_ini" | jq -r '.ini')
echo "$ini" | grep -q 'gateway = true' || fail "ini gateway"
echo "$ini" | grep -q 'dns = tunnel' || fail "ini dns"
echo "$ini" | grep -q 'mode = reality-tls' || fail "ini mode"
echo "$share_ini" | jq -e '.reset == false' >/dev/null || fail "unexpected reset"
pass "POST /api/share format=ini"

echo "-- 6. share link + qr --"
share_link=$(curl -sk -b "$CJ" -X POST "$BASE/api/share" \
  -H 'Content-Type: application/json' \
  -d "{\"profile\":\"$PROFILE\",\"host\":\"$PUBLIC_HOST\",\"user\":\"$VPN_USER\",\"format\":\"link\",\"label\":\"smoke\"}")
ok "$share_link" || fail "share link"
uri=$(echo "$share_link" | jq -r '.uri')
echo "$uri" | grep -q '^qeli://' || fail "uri scheme"
echo "$share_link" | jq -r '.qr_svg' | grep -q '<svg' || fail "qr svg"
pass "POST /api/share format=link"

echo "-- 7. share validation --"
no_user=$(curl -sk -b "$CJ" -X POST "$BASE/api/share" \
  -H 'Content-Type: application/json' \
  -d "{\"profile\":\"$PROFILE\",\"host\":\"$PUBLIC_HOST\",\"user\":\"no-such-user\",\"format\":\"link\"}")
not_ok "$no_user" || fail "share unknown user should fail"
bad_profile=$(curl -sk -b "$CJ" -X POST "$BASE/api/share" \
  -H 'Content-Type: application/json' \
  -d "{\"profile\":\"no-such-profile\",\"host\":\"$PUBLIC_HOST\",\"user\":\"$VPN_USER\",\"format\":\"link\"}")
not_ok "$bad_profile" || fail "share bad profile should fail"
pass "POST /api/share validation errors"

echo "-- 8. transport health --"
health=$(curl -sk -b "$CJ" "$BASE/api/transport/health")
ok "$health" || fail "transport health"
pass "GET /api/transport/health"

echo "-- 9. logout --"
logout=$(curl -sk -b "$CJ" -X POST "$BASE/api/logout")
ok "$logout" || fail "logout"
pass "POST /api/logout"

echo "-- 10. service --"
systemctl is-active --quiet qeli || fail "qeli systemd"
systemctl is-active --quiet nginx || fail "nginx systemd"
ss -lntp | grep -q '127.0.0.1:8080' || fail "panel listen"
ss -lntp | grep -q '127.0.0.1:4430' || fail "reality-tls listen"
pass "systemd + listeners"

echo
echo "SMOKE_OK: 10 checks passed"
REMOTE

echo
echo "Remote smoke: OK"
