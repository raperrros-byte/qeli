#!/bin/sh
set -eu
. /w/scripts/.lab_secrets
apk add -q openssh-client sshpass
VERSION="${QELI_VERSION:-$(sed -n 's/^Version: //p' /w/qeli/debian/control)}"
ARCH="${QELI_ARCH:-$(sed -n 's/^Architecture: //p' /w/qeli/debian/control)}"
PKG="qeli_${VERSION}_${ARCH}"
REMOTE_DEB="/tmp/${PKG}.deb"

sshpass -p "$SSH_PASSWORD2" scp -o StrictHostKeyChecking=no "/w/qeli/debian/${PKG}.deb" "root@${SSH_HOST2}:${REMOTE_DEB}"
sshpass -p "$SSH_PASSWORD2" ssh -o StrictHostKeyChecking=no "root@${SSH_HOST2}" \
  "set -eu; chown -R root:root /etc/qeli /var/log/qeli /var/lib/qeli; chmod 600 /etc/qeli/users.conf /var/lib/qeli/panel-secret.key 2>/dev/null || true; dpkg -i ${REMOTE_DEB}; systemctl restart qeli; sleep 2; systemctl is-active qeli"
