#!/usr/bin/env bash
# Two-container smoke on local Docker: server (fake-tls) + client (gateway, dns=off).
# Usage: bash scripts/agent/docker_smoke_local.sh [image-tag]
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
IMG="${1:-qeli:local-smoke}"
NET="qeli-smoke-net"
SRV="qeli-smoke-server"
CLI="qeli-smoke-client"
# Use repo-local path so docker.sock callers (e.g. docker:cli on Windows) mount host dirs.
BASE="${ROOT}/.agent-smoke/run"
USER="test"
PASS="testpass123"
# argon2id("smokepass123")
HASH='$argon2id$v=19$m=16384,t=2,p=1$cWVsaVNhbHRWYWw$CCYuTv8pvqQrvhrBQW3KjPpEN0MZaFfTKv3HOcGqB8w'

cleanup() {
  docker rm -f "$SRV" "$CLI" >/dev/null 2>&1 || true
  docker network rm "$NET" >/dev/null 2>&1 || true
  rm -rf "$BASE"
}
trap cleanup EXIT

pass() { echo "PASS  $1"; }
fail() { echo "FAIL  $1"; exit 1; }

echo "== 0. build image ${IMG} =="
docker build -f "$ROOT/release/docker/Dockerfile" -t "$IMG" "$ROOT"

echo "== 1. prepare configs =="
mkdir -p "$BASE/server/etc/identity" "$BASE/client/etc"
cat >"$BASE/server/etc/server.conf" <<EOF
[auth]
users_file = /etc/qeli/users.conf

[logging]
level = info

[profile:tcp]
identity_key = /etc/qeli/identity/tcp.key
bind.address = 0.0.0.0
bind.port = 443
bind.transport = tcp
tun.name = vpn0
tun.address = 10.8.0.1
tun.mtu = 1400
pool.cidr = 10.8.0.0/24
pool.exclude = 10.8.0.1
routing.nat.enabled = true
dns.enabled = false
obf.mode = fake-tls
obf.tls.server_name = www.microsoft.com
EOF
cat >"$BASE/server/etc/users.conf" <<EOF
[user:${USER}]
password_hash = ${HASH}
enabled = true
EOF

echo "== 2. start server =="
docker network create "$NET" >/dev/null
docker run -d --name "$SRV" --network "$NET" \
  --cap-add NET_ADMIN --cap-add NET_RAW --cap-add NET_BIND_SERVICE \
  --device /dev/net/tun \
  --sysctl net.ipv4.ip_forward=1 \
  -v "$BASE/server/etc:/etc/qeli" \
  -e QELI_CONFIG=/etc/qeli/server.conf \
  "$IMG" server
sleep 3

PUB="$(docker exec "$SRV" qeli show-identity --config /etc/qeli/server.conf 2>/dev/null | awk 'NR==2 {print $NF; exit}')"
test -n "$PUB" || fail "server identity pubkey"

cat >"$BASE/client/etc/client.conf" <<EOF
[qeli]
server = ${SRV}:443
proto = tcp
user = ${USER}
pass = ${PASS}
key = ${PUB}
mode = fake-tls
sni = www.microsoft.com
dns = off
gateway = true

[logging]
level = info
EOF

echo "== 3. check-config =="
docker run --rm --network "$NET" \
  --cap-add NET_ADMIN --device /dev/net/tun \
  -v "$BASE/client/etc:/etc/qeli:ro" \
  --entrypoint /usr/local/bin/qeli "$IMG" \
  check-config --client --config /etc/qeli/client.conf

echo "== 4. client tunnel + ping =="
docker run -d --name "$CLI" --network "$NET" \
  --cap-add NET_ADMIN --device /dev/net/tun \
  --sysctl net.ipv4.ip_forward=1 \
  -v "$BASE/client/etc:/etc/qeli:ro" \
  "$IMG" client
sleep 6

docker logs "$CLI" 2>&1 | grep -q 'Auth OK' || fail "client auth"
pass "client Auth OK"

docker exec "$CLI" ping -c 2 -W 2 10.8.0.1 >/dev/null || fail "ping gateway"
pass "ping VPN gateway 10.8.0.1"

docker exec "$CLI" ping -c 2 -W 3 1.1.1.1 >/dev/null || fail "ping egress"
pass "ping 1.1.1.1 via NAT"

echo
echo "SMOKE_OK: local docker 2-container test passed"
