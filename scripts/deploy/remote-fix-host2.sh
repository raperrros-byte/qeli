#!/bin/sh
set -eu
. /w/scripts/.lab_secrets
apk add -q openssh-client sshpass
sshpass -p "$SSH_PASSWORD2" ssh -o StrictHostKeyChecking=no "root@${SSH_HOST2}" <<'EOF'
set -eu
echo '== service user =='
grep -E '^(User|Group)=' /etc/systemd/system/qeli.service /etc/systemd/system/qeli.service.d/*.conf 2>/dev/null || true
echo '== /etc/qeli perms =='
ls -la /etc/qeli | head -20
echo '== /var/log/qeli =='
ls -la /var/log/qeli 2>/dev/null | head -10 || true
echo '== fix ownership for root unit =='
chown -R root:root /etc/qeli /var/log/qeli /var/lib/qeli
chmod 600 /etc/qeli/users.conf /etc/qeli/web-tls-key.pem /var/lib/qeli/panel-secret.key 2>/dev/null || true
find /etc/qeli -type d -exec chmod 755 {} \;
find /etc/qeli -type f -exec chmod 644 {} \;
chmod 600 /etc/qeli/users.conf /etc/qeli/web-tls-key.pem 2>/dev/null || true
chmod 755 /etc/qeli/clients 2>/dev/null || true
systemctl reset-failed qeli || true
systemctl restart qeli
sleep 3
systemctl is-active qeli
systemctl status qeli --no-pager -l | head -15
EOF
