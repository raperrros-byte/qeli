#!/usr/bin/env bash
# Clean reinstall qeli on lab VPS: wipe config/users/identity, install .deb, nginx SNI, user daniil.
# Run ON THE SERVER as root. Env required:
#   PANEL_DOMAIN, PANEL_ALLOW_CIDRS, TLS_CERT_PEM, PANEL_PASSWORD
# Optional: QELI_DEB (default /tmp/qeli_0.7.14_amd64.deb), VPN_USER (default daniil)
set -euo pipefail

PANEL_DOMAIN="${PANEL_DOMAIN:?set PANEL_DOMAIN}"
PANEL_ALLOW_CIDRS="${PANEL_ALLOW_CIDRS:?set PANEL_ALLOW_CIDRS}"
TLS_CERT_PEM="${TLS_CERT_PEM:?set TLS_CERT_PEM}"
PANEL_PASSWORD="${PANEL_PASSWORD:?set PANEL_PASSWORD}"
QELI_DEB="${QELI_DEB:-/tmp/qeli_0.7.14_amd64.deb}"
VPN_USER="${VPN_USER:-daniil}"
PUBLIC_IP="${PUBLIC_IP:-$(curl -4fsS ifconfig.me 2>/dev/null || hostname -I | awk '{print $1}')}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

log(){ printf '\n== %s\n' "$*"; }
die(){ printf 'ERROR: %s\n' "$*" >&2; exit 1; }

[ "$(id -u)" -eq 0 ] || die "run as root"
[ -f "$QELI_DEB" ] || die "missing deb: $QELI_DEB"
[ -f "$TLS_CERT_PEM" ] || die "missing TLS: $TLS_CERT_PEM"

log "Stopping services"
systemctl stop qeli 2>/dev/null || true

log "Wiping qeli state (config, users, identity, client links)"
STAMP="$(date +%Y%m%d-%H%M%S)"
mkdir -p /etc/qeli/backup
for f in /etc/qeli/server.conf /etc/qeli/users.conf; do
  [ -f "$f" ] && cp -a "$f" "/etc/qeli/backup/$(basename "$f").${STAMP}" || true
done
rm -rf /etc/qeli/identity /etc/qeli/client-links 2>/dev/null || true
rm -f /etc/qeli/server.conf
: > /etc/qeli/users.conf

log "Installing package"
dpkg -i "$QELI_DEB" || apt-get install -f -y
dpkg -i "$QELI_DEB"

log "Base server config (all profiles)"
QELI_FORCE_RECONFIG=1 QELI_RUN_AS=root NUM_USERS=0 bash /tmp/install-qeli-server.sh "$PUBLIC_IP" || {
  # install script may refuse if conf missing — force from example
  EXAMPLE="/etc/qeli/server-multiprofile.conf.example"
  [ -f "$EXAMPLE" ] || die "$EXAMPLE missing"
  cp "$EXAMPLE" /etc/qeli/server.conf
  sed -i 's/^enabled = false/enabled = true/g' /etc/qeli/server.conf
  while grep -q CHANGEME /etc/qeli/server.conf; do
    k="$(openssl rand -hex 16)"
    sed -i "0,/CHANGEME/s//${k}/" /etc/qeli/server.conf
  done
  while grep -q PLACEHOLDER /etc/qeli/server.conf; do
    s="$(openssl rand -hex 8)"
    sed -i "0,/PLACEHOLDER/s//${s}/" /etc/qeli/server.conf
  done
  qeli show-identity --config /etc/qeli/server.conf
}

log "Nginx + panel + reality-tls on :443"
PANEL_DOMAIN="$PANEL_DOMAIN" \
PANEL_ALLOW_CIDRS="$PANEL_ALLOW_CIDRS" \
TLS_CERT_PEM="$TLS_CERT_PEM" \
bash "${SCRIPT_DIR}/nginx-panel-sni.sh"

log "Panel admin password"
qeli set-web-password --password "$PANEL_PASSWORD" --config /etc/qeli/server.conf

log "VPN user: ${VPN_USER}"
VPN_PASS="$(openssl rand -base64 24 | tr -d '/+=' | head -c 32)"
qeli add-client "$VPN_USER" --password "$VPN_PASS" --config /etc/qeli/server.conf

log "Generating share links for all profiles"
mkdir -p /etc/qeli/client-links
LINK_FILE="/etc/qeli/client-links/${VPN_USER}-all.txt"
: > "$LINK_FILE"
mapfile -t PROFILES < <(awk '/^\[profile:/ {gsub(/[\[\]]/,""); print $1}' /etc/qeli/server.conf | sed 's/^profile://')
for p in "${PROFILES[@]}"; do
  uri="$(qeli share-link "$VPN_USER" --host "$PANEL_DOMAIN" --profile "$p" --label "${p}" --config /etc/qeli/server.conf)"
  echo "$uri" >> "$LINK_FILE"
  echo "  ${p}: ${uri}"
done

log "Systemd (lab root unit if present)"
if [ -f "${SCRIPT_DIR}/qeli-systemd-lab.service" ]; then
  cp "${SCRIPT_DIR}/qeli-systemd-lab.service" /etc/systemd/system/qeli.service
  systemctl daemon-reload
fi

log "Fix ownership (service runs as root on lab)"
chown -R root:root /etc/qeli /var/log/qeli 2>/dev/null || true
chmod 600 /etc/qeli/users.conf 2>/dev/null || true

systemctl enable qeli nginx
systemctl restart nginx qeli
sleep 2
systemctl is-active --quiet qeli || die "qeli not active"
systemctl is-active --quiet nginx || die "nginx not active"

log "Listeners"
ss -lntp | grep -E ':(443|8080|4430|8443|8444|8445)\b' || true

echo ""
echo "=== DONE ==="
echo "Panel:  https://${PANEL_DOMAIN}/  (admin / ${PANEL_PASSWORD})"
echo "VPN:    ${VPN_USER} / ${VPN_PASS}"
echo "Links:  ${LINK_FILE}"
echo ""
echo "Metrics in Win client: Settings → Panel URL https://${PANEL_DOMAIN}, user admin, password above."
