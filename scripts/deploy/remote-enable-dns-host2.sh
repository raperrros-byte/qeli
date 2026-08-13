#!/bin/sh
# Enable in-tunnel DNS on profiles that currently push none (mobile/Windows full-tunnel fails).
set -eu
. /w/scripts/.lab_secrets
apk add -q openssh-client sshpass
sshpass -p "$SSH_PASSWORD2" ssh -o StrictHostKeyChecking=no "root@${SSH_HOST2}" <<'EOF'
set -eu
python3 - <<'PY'
from pathlib import Path
path = Path("/etc/qeli/server.conf")
text = path.read_text()
# Enable dns for profiles that currently have dns.enabled = false under [profile:*]
# Only flip enabled=false -> true when inside a profile section and the key is dns.enabled.
out = []
in_profile = False
changed = []
current = None
for line in text.splitlines(keepends=True):
    raw = line.strip()
    if raw.startswith("[profile:"):
        in_profile = True
        current = raw[9:-1]
    elif raw.startswith("[") and raw.endswith("]"):
        in_profile = False
        current = None
    if in_profile and raw.startswith("dns.enabled"):
        key, _, val = raw.partition("=")
        if val.strip().lower() in ("false", "0", "no", "off"):
            indent = line[: len(line) - len(line.lstrip())]
            line = f"{indent}dns.enabled = true\n"
            changed.append(current)
    out.append(line)
if changed:
    path.write_text("".join(out))
    print("enabled dns for:", ", ".join(changed))
else:
    print("no profiles needed dns.enabled flip")
PY
systemctl restart qeli
sleep 2
systemctl is-active qeli
awk '
  /^\[profile:/ {
    if (name != "") printf "%-16s enabled=%s\n", name, en
    name=$0; gsub(/[\[\]]/,"",name); sub(/^profile:/,"",name); en="?"
  }
  /^dns\.enabled/ { split($0,a,"="); gsub(/ /,"",a[2]); en=a[2] }
  END { if (name != "") printf "%-16s enabled=%s\n", name, en }
' /etc/qeli/server.conf
EOF
