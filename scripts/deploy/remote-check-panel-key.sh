#!/bin/sh
set -eu
. /w/scripts/.lab_secrets
apk add -q openssh-client sshpass
sshpass -p "$SSH_PASSWORD2" ssh -o StrictHostKeyChecking=no "root@${SSH_HOST2}" <<'EOF'
ls -la /var/lib/qeli/ /etc/qeli/panel-secret.key 2>/dev/null || true
journalctl -u qeli -n 30 --no-grep | grep -iE 'encrypt|panel key|password_enc|share' || true
EOF
