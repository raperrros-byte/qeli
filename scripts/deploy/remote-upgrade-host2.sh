#!/bin/sh
# In-place qeli .deb upgrade on lab host2 (preserves /etc/qeli).
# Lab unit runs as root — normalize ownership before AND after dpkg so postinst
# cannot leave qeli:qeli files that block startup (see README fork-notice).
set -eu
SECRETS="${SECRETS:-/w/scripts/.lab_secrets}"
. "$SECRETS"

HOST="${SSH_HOST2:?}"
PASS="${SSH_PASSWORD2:?}"
VERSION="${QELI_VERSION:-$(sed -n 's/^Version: //p' /w/qeli/debian/control)}"
ARCH="${QELI_ARCH:-$(sed -n 's/^Architecture: //p' /w/qeli/debian/control)}"
PKG="qeli_${VERSION}_${ARCH}"
DEB="${DEB:-/w/qeli/debian/${PKG}.deb}"
REMOTE_DEB="/tmp/${PKG}.deb"

apk add -q openssh-client sshpass

test -f "$DEB"

sshpass -p "$PASS" scp -o StrictHostKeyChecking=no "$DEB" "root@${HOST}:${REMOTE_DEB}"

sshpass -p "$PASS" ssh -o StrictHostKeyChecking=no "root@${HOST}" bash -s <<EOF
set -eu
normalize_ownership() {
  chown -R root:root /etc/qeli /var/log/qeli /var/lib/qeli
  chmod 600 /etc/qeli/users.conf /etc/qeli/web-tls-key.pem /etc/qeli/panel-secret.key /var/lib/qeli/panel-secret.key 2>/dev/null || true
  find /etc/qeli -type d -exec chmod 755 {} \;
  find /etc/qeli -type f -exec chmod 644 {} \;
  chmod 600 /etc/qeli/users.conf /etc/qeli/web-tls-key.pem 2>/dev/null || true
  chmod 755 /etc/qeli/clients 2>/dev/null || true
}

echo "== pre-install ownership =="
normalize_ownership

echo "== dpkg -i ${REMOTE_DEB} =="
dpkg -i ${REMOTE_DEB}

echo "== post-install ownership =="
normalize_ownership

systemctl reset-failed qeli || true
systemctl restart qeli
sleep 3
systemctl is-active qeli
systemctl is-active nginx || true
/usr/bin/qeli --version 2>/dev/null || qeli --version
EOF

echo "Upgrade complete on ${HOST}"
