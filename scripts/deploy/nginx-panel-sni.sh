#!/usr/bin/env bash
#
# Deploy nginx stream SNI on :443 + qeli panel on a domain + reality-tls passthrough.
# Run on the VPS as root AFTER qeli .deb is installed.
#
# Required env (or pass as arguments):
#   PANEL_DOMAIN          — panel hostname (SNI), e.g. panel.example.com
#   PANEL_ALLOW_CIDRS     — comma-separated CIDRs allowed to open the panel
#                           e.g. 203.0.113.0/24,10.9.0.0/16
#   TLS_CERT_PEM          — path to PEM bundle (cert chain + private key)
#
# Optional:
#   QELI_REALITY_BACKEND_PORT  — reality-tls listen port behind nginx (default 4430)
#   PANEL_LOOP_PORT            — qeli [web] port (default 8080)
#   QELI_CONF                  — server config path (default /etc/qeli/server.conf)
#   SKIP_QELI_RECONFIG=1       — do not rewrite server.conf (nginx + TLS only)
#
# Upload TLS_CERT_PEM to the server first, e.g. scp bundle.pem root@SERVER:/tmp/qeli-panel.pem
# and set TLS_CERT_PEM=/tmp/qeli-panel.pem
#
set -euo pipefail

PANEL_DOMAIN="${PANEL_DOMAIN:-${1:-}}"
PANEL_ALLOW_CIDRS="${PANEL_ALLOW_CIDRS:-${2:-}}"
TLS_CERT_PEM="${TLS_CERT_PEM:-${3:-/tmp/qeli-panel.pem}}"
QELI_REALITY_BACKEND_PORT="${QELI_REALITY_BACKEND_PORT:-4430}"
PANEL_LOOP_PORT="${PANEL_LOOP_PORT:-8080}"
CONF="${QELI_CONF:-/etc/qeli/server.conf}"
EXAMPLE="/etc/qeli/server-multiprofile.conf.example"
RUN_STAMP="$(date +%Y%m%d-%H%M%S)"

log(){ printf '\n== %s\n' "$*"; }
die(){ printf 'ERROR: %s\n' "$*" >&2; exit 1; }

[ "$(id -u)" -eq 0 ] || die "run as root"
[ -n "$PANEL_DOMAIN" ] || die "set PANEL_DOMAIN (panel hostname)"
[ -n "$PANEL_ALLOW_CIDRS" ] || die "set PANEL_ALLOW_CIDRS (comma-separated CIDRs)"
[ -f "$TLS_CERT_PEM" ] || die "TLS bundle missing: $TLS_CERT_PEM"

_conf_web_set(){
  local key="$1" val="$2" tmp
  tmp="$(mktemp)"
  awk -v k="$key" -v v="$val" '
    BEGIN { insec = 0; done = 0 }
    /^\[/ {
      insec = ($0 == "[web]")
      print
      if (insec && !done) { print k " = " v; done = 1 }
      next
    }
    insec && $0 ~ ("^[[:space:]]*#?[[:space:]]*" k "[[:space:]]*=") { next }
    { print }
    END { if (!done) { print ""; print "[web]"; print k " = " v } }
  ' "$CONF" > "$tmp"
  [ -s "$tmp" ] || die "failed to set web.${key}"
  cat "$tmp" > "$CONF"
  rm -f "$tmp"
}

# Build nginx geo block from comma-separated CIDR list.
geo_lines=""
IFS=',' read -r -a cidrs <<< "$PANEL_ALLOW_CIDRS"
for c in "${cidrs[@]}"; do
  c="$(printf '%s' "$c" | tr -d '[:space:]')"
  [ -n "$c" ] || continue
  geo_lines="${geo_lines}    ${c} 1;"$'\n'
done
[ -n "$geo_lines" ] || die "PANEL_ALLOW_CIDRS parsed empty"

REALITY_SID=""
if [ -f "$CONF" ]; then
  REALITY_SID="$(awk '
    /^\[profile:reality-tls\]/ { in_p=1; next }
    /^\[profile:/ { in_p=0 }
    in_p && /^obf\.tls\.reality_proxy\.short_ids/ { print $3; exit }
  ' "$CONF" || true)"
fi
[ -n "$REALITY_SID" ] || REALITY_SID="$(openssl rand -hex 8)"
echo "reality-tls short_id (preserve if existed): ${REALITY_SID}"

log "Installing nginx + stream module"
export DEBIAN_FRONTEND=noninteractive
apt-get update -y
apt-get install -y nginx openssl libnginx-mod-stream

log "Installing TLS material for qeli panel"
install -d -m 750 /etc/qeli
awk '/-----BEGIN CERTIFICATE-----/,/-----END CERTIFICATE-----/' "$TLS_CERT_PEM" > /etc/qeli/web-tls-cert.pem
awk '/-----BEGIN PRIVATE KEY-----/,/-----END PRIVATE KEY-----/' "$TLS_CERT_PEM" > /etc/qeli/web-tls-key.pem
[ -s /etc/qeli/web-tls-cert.pem ] && [ -s /etc/qeli/web-tls-key.pem ] || die "failed to split TLS bundle"
chmod 640 /etc/qeli/web-tls-cert.pem /etc/qeli/web-tls-key.pem
chown root:qeli /etc/qeli/web-tls-cert.pem /etc/qeli/web-tls-key.pem 2>/dev/null || chown root:root /etc/qeli/web-tls-*.pem

log "Writing nginx stream SNI router (:443)"
mkdir -p /etc/nginx/stream.d
cat > /etc/nginx/stream.d/qeli-sni.conf <<EOF
# Panel SNI filtered by source IP. VPN (other SNI) is not filtered.
geo \$remote_addr \$panel_allowed {
${geo_lines}    default 0;
}

map "\$ssl_preread_server_name:\$panel_allowed" \$qeli_upstream {
    "${PANEL_DOMAIN}:1"  127.0.0.1:${PANEL_LOOP_PORT};
    "${PANEL_DOMAIN}:0"  127.0.0.1:9;
    default              127.0.0.1:${QELI_REALITY_BACKEND_PORT};
}

server {
    listen 443 reuseport;
    listen [::]:443 reuseport;
    proxy_pass \$qeli_upstream;
    ssl_preread on;
    proxy_connect_timeout 10s;
    proxy_timeout 24h;
}
EOF

if ! grep -q '^stream {' /etc/nginx/nginx.conf 2>/dev/null; then
  cat >> /etc/nginx/nginx.conf <<'EOF'

stream {
    include /etc/nginx/stream.d/*.conf;
}
EOF
fi

rm -f /etc/nginx/sites-enabled/qeli-panel.conf 2>/dev/null || true

if [ "${SKIP_QELI_RECONFIG:-0}" != "1" ]; then
  log "Rebuilding qeli server.conf (all profiles, reality-tls behind nginx)"
  [ -f "$EXAMPLE" ] || die "$EXAMPLE missing — install qeli .deb first"
  if [ -f "$CONF" ]; then
    cp -a "$CONF" "${CONF}.bak-${RUN_STAMP}"
    echo "  backup → ${CONF}.bak-${RUN_STAMP}"
  fi
  cp "$EXAMPLE" "$CONF"
  awk -v sid="$REALITY_SID" -v rp="$QELI_REALITY_BACKEND_PORT" '
    /^\[profile:reality-tls\]/ { in_rt=1 }
    /^\[profile:/ && !/^\[profile:reality-tls\]/ { in_rt=0 }
    in_rt && /^bind\.address/ { print "bind.address = 127.0.0.1"; next }
    in_rt && /^bind\.port = 443/ { print "bind.port = " rp; print "bind.public_port = 443"; next }
    in_rt && /^obf\.tls\.reality_proxy\.short_ids/ { print "obf.tls.reality_proxy.short_ids = " sid; next }
    /^enabled = false/ { print "enabled = true"; next }
    { print }
  ' "$CONF" > "${CONF}.new"
  mv "${CONF}.new" "$CONF"

  tmp="$(mktemp)"
  awk -v keep_sid="$REALITY_SID" '
    /^\[profile:reality-tls\]/ { in_rt=1 }
    /^\[profile:/ { if ($0 != "[profile:reality-tls]") in_rt=0 }
    /^obf\.tls\.reality_proxy\.short_ids/ {
      if (in_rt) { print "obf.tls.reality_proxy.short_ids = " keep_sid; next }
      print "obf.tls.reality_proxy.short_ids = PLACEHOLDER"; next
    }
    { print }
  ' "$CONF" > "$tmp"
  mv "$tmp" "$CONF"
  while grep -q PLACEHOLDER "$CONF"; do
    sid="$(openssl rand -hex 8)"
    sed -i "0,/PLACEHOLDER/s//${sid}/" "$CONF"
  done
  while grep -q CHANGEME "$CONF"; do
    key="$(openssl rand -hex 16)"
    sed -i "0,/CHANGEME/s//${key}/" "$CONF"
  done
  sed -i "/^routing.nat.interface/d" "$CONF"
fi

OLD_HASH=""
OLD_USER="admin"
bak="$(ls -1t "${CONF}.bak-"* 2>/dev/null | head -1 || true)"
if [ -n "$bak" ] && [ -f "$bak" ]; then
  OLD_HASH="$(awk '/^password_hash =/ {print $3; exit}' "$bak" || true)"
  OLD_USER="$(awk '/^username =/ {print $3; exit}' "$bak" || true)"
fi

log "Configuring qeli [web] (TLS on loopback; allowlist in nginx geo only)"
_conf_web_set enabled true
_conf_web_set bind 127.0.0.1
_conf_web_set port "$PANEL_LOOP_PORT"
_conf_web_set tls true
_conf_web_set tls_cert /etc/qeli/web-tls-cert.pem
_conf_web_set tls_key /etc/qeli/web-tls-key.pem
_conf_web_set secure_cookie true
_conf_web_set public_host "$PANEL_DOMAIN"
_conf_web_set allowed_origins "$PANEL_DOMAIN"
_conf_web_set allowed_ips ""
_conf_web_set trusted_proxies ""
_conf_web_set username "${OLD_USER:-admin}"
[ -n "$OLD_HASH" ] && _conf_web_set password_hash "$OLD_HASH"

chown -R qeli:qeli "$CONF" /etc/qeli/identity 2>/dev/null || true

log "Stopping qeli before nginx binds :443"
systemctl stop qeli || true

nginx -t
systemctl enable nginx
systemctl restart nginx

log "Starting qeli"
systemctl restart qeli
sleep 2
systemctl is-active --quiet nginx || die "nginx not active"
systemctl is-active --quiet qeli || die "qeli not active"

log "Listeners"
ss -lntp | grep -E ':(443|8080|4430)\b' || true

echo ""
echo "Done."
echo "  Panel:  https://${PANEL_DOMAIN}/  (allowed: ${PANEL_ALLOW_CIDRS})"
echo "  VPN:    reality-tls on public :443 → 127.0.0.1:${QELI_REALITY_BACKEND_PORT}"
echo "  Note:   web.allowed_ips must stay empty — stream proxy peer is 127.0.0.1"
