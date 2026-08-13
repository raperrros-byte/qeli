#!/bin/sh
set -eu
. /w/scripts/.lab_secrets
apk add -q openssh-client sshpass
sshpass -p "$SSH_PASSWORD2" ssh -o StrictHostKeyChecking=no "root@${SSH_HOST2}" <<'EOF'
grep -A15 '^\[user:linux\]' /etc/qeli/users.conf
qeli share-link linux --profile reality-tls --host clientarea.devopsworld.ru --config /etc/qeli/server.conf 2>&1 | head -5 || true
EOF
