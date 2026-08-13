#!/bin/sh
set -eu
. /w/scripts/.lab_secrets
apk add -q openssh-client sshpass
sshpass -p "$SSH_PASSWORD2" scp -o StrictHostKeyChecking=no /w/qeli/debian/qeli_0.7.15_amd64.deb "root@${SSH_HOST2}:/tmp/qeli_0.7.15_amd64.deb"
sshpass -p "$SSH_PASSWORD2" ssh -o StrictHostKeyChecking=no "root@${SSH_HOST2}" \
  "set -eu; chown -R root:root /etc/qeli /var/log/qeli /var/lib/qeli; chmod 600 /etc/qeli/users.conf /var/lib/qeli/panel-secret.key 2>/dev/null || true; dpkg -i /tmp/qeli_0.7.15_amd64.deb; systemctl restart qeli; sleep 2; systemctl is-active qeli"
