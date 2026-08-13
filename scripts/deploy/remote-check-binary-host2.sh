#!/bin/sh
set -eu
. /w/scripts/.lab_secrets
apk add -q openssh-client sshpass
sshpass -p "$SSH_PASSWORD2" ssh -o StrictHostKeyChecking=no "root@${SSH_HOST2}" \
  "strings /usr/bin/qeli | grep -E 'Generate with password|Export client config|needs_password' | head -5"
