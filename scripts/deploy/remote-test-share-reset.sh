#!/bin/sh
set -eu
. /w/scripts/.lab_secrets
apk add -q openssh-client sshpass curl
sshpass -p "$SSH_PASSWORD2" ssh -o StrictHostKeyChecking=no "root@${SSH_HOST2}" <<EOF
set -eu
curl -sk -c /tmp/cj -X POST 'https://127.0.0.1:8080/api/login' \
  -H 'Content-Type: application/json' \
  -d '{"username":"admin","password":"'"$PANEL_PASSWORD2"'"}'
echo
curl -sk -b /tmp/cj -X POST 'https://127.0.0.1:8080/api/share' \
  -H 'Content-Type: application/json' \
  -d '{"profile":"reality-tls","host":"clientarea.devopsworld.ru","user":"linux","allow_reset":"true","format":"ini","gateway":"true","dns":"tunnel"}'
echo
grep -A3 '^\[user:linux\]' /etc/qeli/users.conf
EOF
