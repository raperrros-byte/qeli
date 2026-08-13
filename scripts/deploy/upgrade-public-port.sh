#!/usr/bin/env bash
# Upgrade qeli .deb and set bind.public_port=443 on reality-tls (nginx front).
set -euo pipefail
export DEBIAN_FRONTEND=noninteractive
DEB=/tmp/qeli_0.7.14_amd64.deb
CONF=/etc/qeli/server.conf
[ -f "$DEB" ] || { echo "missing $DEB"; exit 1; }

systemctl stop qeli || true
dpkg -i "$DEB" || apt-get install -y -f --no-install-recommends

# Keep lab minimal unit if present
if [ -f /etc/systemd/system/qeli.service ]; then
  systemctl daemon-reload
fi

# Ensure reality-tls advertises :443 to clients while listening on 4430
if grep -q '^\[profile:reality-tls\]' "$CONF"; then
  awk '
    /^\[profile:reality-tls\]/ { in_rt=1 }
    /^\[profile:/ { if ($0 != "[profile:reality-tls]") in_rt=0 }
    in_rt && /^bind\.public_port/ { next }
    in_rt && /^bind\.port = / {
      print
      print "bind.public_port = 443"
      next
    }
    { print }
  ' "$CONF" > "${CONF}.new"
  mv "${CONF}.new" "$CONF"
  chown qeli:qeli "$CONF" 2>/dev/null || true
fi

systemctl restart qeli
sleep 2
systemctl is-active qeli
echo "== reality-tls bind"
awk '/^\[profile:reality-tls\]/{f=1;print;next} /^\[profile:/{f=0} f && /^bind\./' "$CONF"
echo "== share smoke (port in URI)"
# Use first user if any
U=$(awk -F: '/^\[user:/{gsub(/\[user:|\]/,"",$1); print $1; exit}' /etc/qeli/users.conf 2>/dev/null || true)
if [ -n "$U" ]; then
  qeli share-link "$U" --profile reality-tls --config "$CONF" 2>&1 | grep -oE 'qeli://[^[:space:]]+' | head -1 || true
fi
echo OK
