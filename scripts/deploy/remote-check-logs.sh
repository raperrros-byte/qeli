#!/bin/sh
set -eu
. /w/scripts/.lab_secrets
apk add -q openssh-client sshpass
sshpass -p "$SSH_PASSWORD2" ssh -o StrictHostKeyChecking=no "root@${SSH_HOST2}" \
  "journalctl -u qeli -n 200 --no-pager | grep -iE 'encrypt|panel key|share/' | tail -20"
