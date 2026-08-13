#!/bin/sh
set -eu
. /w/scripts/.lab_secrets
apk add -q openssh-client sshpass
sshpass -p "$SSH_PASSWORD2" ssh -o StrictHostKeyChecking=no "root@${SSH_HOST2}" <<'EOF'
set -eu
echo '== panel secret key =='
ls -la /etc/qeli/panel-secret.key /etc/qeli/.session_key 2>/dev/null || true
echo '== user linux in users.conf =='
grep -A20 '^\[user:linux\]' /etc/qeli/users.conf || echo 'user linux not found'
echo '== all users =='
grep '^\[user:' /etc/qeli/users.conf
echo '== test decrypt via share-link =='
qeli share-link linux --profile reality-tls --host clientarea.devopsworld.ru --config /etc/qeli/server.conf 2>&1 | head -5 || true
qeli share-link daniil-test-linux --profile reality-tls --host clientarea.devopsworld.ru --config /etc/qeli/server.conf 2>&1 | head -5 || true
EOF
