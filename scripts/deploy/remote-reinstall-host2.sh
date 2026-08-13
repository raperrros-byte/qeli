#!/usr/bin/env bash
# Upload artifacts and run clean reinstall on lab host2 (SSH_HOST2 from .lab_secrets).
# Run inside Docker alpine with repo mounted at /w, or adapt paths.
set -euo pipefail
SECRETS="${SECRETS:-/w/scripts/.lab_secrets}"
# shellcheck disable=SC1090
source "$SECRETS"

HOST="${SSH_HOST2:?}"
PASS="${SSH_PASSWORD2:?}"
DOMAIN="${PANEL_DOMAIN:?set PANEL_DOMAIN in .lab_secrets}"
ALLOW="${PANEL_ALLOW_CIDRS:?set PANEL_ALLOW_CIDRS in .lab_secrets}"
PANEL_PASS="${PANEL_PASSWORD2:?}"

apk add -q openssh-client sshpass

SCP=(sshpass -p "$PASS" scp -o StrictHostKeyChecking=no)
SSH=(sshpass -p "$PASS" ssh -o StrictHostKeyChecking=no "root@${HOST}")

"${SCP[@]}" /w/qeli/debian/qeli_0.7.14_amd64.deb "root@${HOST}:/tmp/"
"${SCP[@]}" /w/install-qeli-server.sh "root@${HOST}:/tmp/"
"${SCP[@]}" /w/scripts/deploy/nginx-panel-sni.sh "root@${HOST}:/tmp/"
"${SCP[@]}" /w/scripts/deploy/clean-reinstall-lab.sh "root@${HOST}:/tmp/"
"${SCP[@]}" /w/scripts/deploy/qeli-systemd-lab.service "root@${HOST}:/tmp/"
if [ -n "${TLS_CERT_PEM:-}" ] && [ -f "$TLS_CERT_PEM" ]; then
  "${SCP[@]}" "$TLS_CERT_PEM" "root@${HOST}:/tmp/qeli-panel.pem"
else
  echo "WARN: set TLS_CERT_PEM to a local PEM path before scp" >&2
fi

"${SSH[@]}" bash -s <<EOF
set -euo pipefail
chmod +x /tmp/clean-reinstall-lab.sh /tmp/nginx-panel-sni.sh /tmp/install-qeli-server.sh
export PANEL_DOMAIN=${DOMAIN}
export PANEL_ALLOW_CIDRS=${ALLOW}
export TLS_CERT_PEM=/tmp/qeli-panel.pem
export PANEL_PASSWORD=${PANEL_PASS}
export SCRIPT_DIR=/tmp
bash /tmp/clean-reinstall-lab.sh
EOF

echo "Reinstall complete on ${HOST}"
