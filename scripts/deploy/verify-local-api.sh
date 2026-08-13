#!/usr/bin/env bash
# Smoke-check panel login + /api/system on loopback (after nginx SNI deploy).
# Required env: PANEL_DOMAIN, PANEL_PASSWORD
set -euo pipefail
HOST="${PANEL_DOMAIN:?set PANEL_DOMAIN}"
PASS="${PANEL_PASSWORD:?set PANEL_PASSWORD}"
USER="${PANEL_USER:-admin}"

curl -sk -c /tmp/cj -X POST "https://127.0.0.1:8080/api/login" \
  -H "Host: ${HOST}" \
  -H 'Content-Type: application/json' \
  -d "{\"username\":\"${USER}\",\"password\":\"${PASS}\"}" >/dev/null
echo "== cookie"
grep qeli_session /tmp/cj || true
echo "== /api/system"
curl -sk -b /tmp/cj -H "Host: ${HOST}" "https://127.0.0.1:8080/api/system"
echo ""
VPN_USER="${VPN_USER:-daniil}"
echo "== reality-tls link"
qeli share-link "$VPN_USER" --profile reality-tls --host "$HOST" --config /etc/qeli/server.conf 2>&1 \
  | grep -o 'qeli://[^[:space:]]*' || true
echo "== reality :8443 link"
qeli share-link "$VPN_USER" --profile reality --host "$HOST" --config /etc/qeli/server.conf 2>&1 \
  | grep -o 'qeli://[^[:space:]]*' || true
