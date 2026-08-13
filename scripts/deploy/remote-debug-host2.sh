#!/bin/sh
set -eu
. /w/scripts/.lab_secrets
apk add -q openssh-client sshpass
sshpass -p "$SSH_PASSWORD2" ssh -o StrictHostKeyChecking=no "root@${SSH_HOST2}" <<'EOF'
systemctl status qeli --no-pager -l || true
echo '--- journal ---'
journalctl -u qeli -n 50 --no-pager || true
echo '--- restart ---'
systemctl restart qeli || true
sleep 2
systemctl is-active qeli || true
journalctl -u qeli -n 20 --no-pager || true
EOF
