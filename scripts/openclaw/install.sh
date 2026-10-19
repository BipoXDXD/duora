#!/bin/sh
# Installs reviewed files only. Does not start services, read production secrets, or edit Duora policy.
set -eu
[ "$(id -u)" = 0 ] || { echo 'Run as administrator after reviewing this change.' >&2; exit 1; }
[ "$#" = 1 ] && [ "$1" = --approve-install ] || { echo 'Required: --approve-install' >&2; exit 1; }
ROOT=$(CDPATH='' cd -- "$(dirname -- "$0")/../.." && pwd)
DEST=/opt/duora-agent
[ ! -e "$DEST" ] || { echo 'Already installed; use the reviewed update procedure.' >&2; exit 1; }
python3 -c "import shutil; assert shutil.disk_usage('/var/lib').free > 4 * 1024**3, 'Need >4 GiB free before reserving agent state'"
command -v docker >/dev/null
docker compose version >/dev/null
getent passwd duora-monitor >/dev/null || useradd --system --no-create-home --shell /usr/sbin/nologin duora-monitor
# A dedicated container UID is required: do not share a host account with uid 22001.
if getent passwd 22001 >/dev/null; then
  echo 'UID 22001 already belongs to a host account: use a dedicated VM/user-namespace mapping before installing.' >&2
  exit 1
fi
install -d -m 0755 "$DEST" "$DEST/workspace" /etc/duora-agent /etc/duora-observer /var/lib/duora-observer
install -d -m 0700 /etc/duora-agent/secrets /etc/duora-observer/secrets
install -d -m 0700 /var/lib/duoraagent
[ ! -e /var/lib/duoraagent.ext4 ]
# Reserve the full 2 GiB now; no sparse file that can later exhaust production space.
fallocate -l 2G /var/lib/duoraagent.ext4
chmod 0600 /var/lib/duoraagent.ext4
mkfs.ext4 -q /var/lib/duoraagent.ext4
install -d -m 0755 -o duora-monitor -g duora-monitor /var/lib/duora-monitor /var/lib/duora-monitor/public
install -m 0644 "$ROOT"/scripts/openclaw/*.py "$ROOT"/scripts/openclaw/*.mjs "$DEST/"
install -m 0755 "$ROOT"/scripts/openclaw/firewall.sh "$DEST/"
install -m 0644 "$ROOT/infra/openclaw/compose.yml" "$DEST/compose.yml"
install -m 0644 "$ROOT/infra/openclaw/workspace/AGENTS.md" "$DEST/workspace/AGENTS.md"
install -m 0644 "$ROOT/infra/openclaw/openclaw.example.json" /etc/duora-agent/openclaw.json
install -m 0644 "$ROOT/infra/openclaw/monitor.example.json" /etc/duora-observer/monitor.json
install -m 0644 "$ROOT"/infra/openclaw/systemd/* /etc/systemd/system/
systemctl daemon-reload
systemctl start var-lib-duoraagent.mount
chown 22001:22001 /var/lib/duoraagent
chmod 0700 /var/lib/duoraagent
printf '%s\n' 'Files installed; services NOT enabled. Provision credentials privately, pin the image and complete the runbook.'
