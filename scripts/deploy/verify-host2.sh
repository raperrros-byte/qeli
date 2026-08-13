#!/usr/bin/env bash
# Post-deploy checks on the VPS. Required env: PANEL_DOMAIN, PANEL_PASSWORD
set -euo pipefail
HOST="${PANEL_DOMAIN:?set PANEL_DOMAIN}"
PASS="${PANEL_PASSWORD:?set PANEL_PASSWORD}"
USER="${PANEL_USER:-admin}"
VPN_USER="${VPN_USER:-daniil}"

echo "== reality-tls bind"
awk '/^\[profile:reality-tls\]/{f=1;print;next} /^\[profile:/{f=0} f && /^bind\./' /etc/qeli/server.conf
echo "== listeners"
ss -lntp | grep -E 'qeli|:4430|:8080|:8443' || true
echo "== panel login (via public HTTPS)"
curl -sk -c /tmp/cj -X POST "https://${HOST}/api/login" \
  -H 'Content-Type: application/json' \
  -d "{\"username\":\"${USER}\",\"password\":\"${PASS}\"}" >/dev/null
echo "== metrics"
curl -sk -b /tmp/cj "https://${HOST}/api/system" | head -c 400
echo ""
echo "== reality-tls share link"
qeli share-link "$VPN_USER" --profile reality-tls --host "$HOST" --config /etc/qeli/server.conf 2>&1 \
  | grep -o 'qeli://[^[:space:]]*' || true
