#!/bin/sh
set -eu
. /w/scripts/.lab_secrets
apk add -q openssh-client sshpass
sshpass -p "$SSH_PASSWORD2" ssh -o StrictHostKeyChecking=no "root@${SSH_HOST2}" <<'EOF'
set -eu
awk '
  /^\[profile:/ {
    if (name != "") printf "%-16s enabled=%-5s listen=%-15s push=%s\n", name, en, listen, push
    name=$0; gsub(/[\[\]]/,"",name); sub(/^profile:/,"",name)
    en="?"; listen="-"; push="-"
  }
  /^dns\.enabled/ { split($0,a,"="); gsub(/ /,"",a[2]); en=a[2] }
  /^dns\.listen/  { split($0,a,"="); gsub(/ /,"",a[2]); listen=a[2] }
  /^dns\.push_servers/ { split($0,a,"="); gsub(/ /,"",a[2]); push=a[2] }
  END {
    if (name != "") printf "%-16s enabled=%-5s listen=%-15s push=%s\n", name, en, listen, push
  }
' /etc/qeli/server.conf
EOF
