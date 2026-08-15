#!/bin/sh
# In-place qeli .deb upgrade on lab host2 (preserves /etc/qeli).
set -eu
SECRETS="${SECRETS:-/w/scripts/.lab_secrets}"
. "$SECRETS"

HOST="${SSH_HOST2:?}"
PASS="${SSH_PASSWORD2:?}"
DEB="${DEB:-/w/qeli/debian/qeli_0.7.15_amd64.deb}"

apk add -q openssh-client sshpass

test -f "$DEB"

sshpass -p "$PASS" scp -o StrictHostKeyChecking=no "$DEB" "root@${HOST}:/tmp/qeli_0.7.15_amd64.deb"
sshpass -p "$PASS" scp -o StrictHostKeyChecking=no /w/update-qeli-server.sh "root@${HOST}:/tmp/update-qeli-server.sh"

sshpass -p "$PASS" ssh -o StrictHostKeyChecking=no "root@${HOST}" bash -s <<'EOF'
set -eu
chmod +x /tmp/update-qeli-server.sh
QELI_DEB=/tmp/qeli_0.7.15_amd64.deb QELI_FORCE=1 bash /tmp/update-qeli-server.sh
# Deb postinst may restore qeli:qeli ownership; lab unit runs as root and the
# process drops to the package user for ACL — normalize after install.
chown -R root:root /etc/qeli /var/log/qeli /var/lib/qeli
chmod 600 /etc/qeli/users.conf /etc/qeli/web-tls-key.pem /etc/qeli/panel-secret.key /var/lib/qeli/panel-secret.key 2>/dev/null || true
systemctl reset-failed qeli || true
systemctl restart qeli
sleep 2
systemctl is-active qeli
systemctl is-active nginx || true
/usr/bin/qeli --version 2>/dev/null || qeli --version
EOF

echo "Upgrade complete on ${HOST}"
