#!/usr/bin/env bash
# Regenerate qeli:// links for one user across all multiprofile names.
# Required env: PANEL_DOMAIN
set -euo pipefail
HOST="${PANEL_DOMAIN:?set PANEL_DOMAIN}"
VPN_USER="${VPN_USER:-daniil}"
F="/etc/qeli/client-links/${VPN_USER}-all.txt"
mkdir -p /etc/qeli/client-links
: > "$F"
for p in reality-tls reality fake-tls obfs-ws obfs-none plain udp-fake-tls udp-quic udp-obfs obfs-awg; do
  qeli share-link "$VPN_USER" --host "$HOST" --profile "$p" --label "${p}" --config /etc/qeli/server.conf 2>&1 \
    | grep -o 'qeli://[^[:space:]]*' >> "$F" || true
done
cat "$F"
