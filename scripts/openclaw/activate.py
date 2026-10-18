#!/usr/bin/env python3
"""Explicit admin activation/update; stopped-state backup + rollback on failed health."""
import argparse
import fcntl
from datetime import datetime, timezone
import os
from pathlib import Path
import shutil
import subprocess
import time
from preflight import main as preflight, pinned

ROOT = Path('/etc/duora-agent')
STATE = Path('/var/lib/duoraagent')
COMPOSE = ['/usr/bin/docker', 'compose', '--env-file', str(ROOT / 'image.env'),
           '-f', '/opt/duora-agent/compose.yml']


def run(args, timeout=180):
    subprocess.run(args, check=True, timeout=timeout)


def wait_healthy():
    for _ in range(30):
        ids = subprocess.check_output(COMPOSE + ['ps', '-q', 'gateway'], text=True).split()
        if len(ids) == 1:
            health = subprocess.check_output(['/usr/bin/docker', 'inspect', '--format',
                '{{if .State.Health}}{{.State.Health.Status}}{{end}}', ids[0]], text=True).strip()
            if health == 'healthy':
                return
        time.sleep(5)
    raise RuntimeError('gateway did not become healthy')


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--approve-image', required=True, help='exact registry/image@sha256 digest approved by operator')
    args = parser.parse_args()
    if os.geteuid() != 0:
        raise SystemExit('administrator required; this command is never exposed to Slack')
    lock = open('/run/duora-agent-update.lock', 'w')
    fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
    preflight()
    pinned('OPENCLAW_IMAGE=' + args.approve_image)
    # No implicit pull: an operator must verify provenance/scan and pull this digest beforehand.
    run(['/usr/bin/docker', 'image', 'inspect', args.approve_image, '--format', '{{.Id}}'])
    old = (ROOT / 'image.env').read_text()
    enabled = subprocess.run(['/usr/bin/systemctl', 'is-active', '--quiet', 'duora-agent']).returncode == 0
    run(['/usr/bin/systemctl', 'stop', 'duora-agent'])
    stamp = datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%S%fZ')
    archive = Path('/var/backups/duora-agent') / stamp
    archive.mkdir(parents=True, mode=0o700)
    os.chmod(archive.parent, 0o700)
    try:
        shutil.copytree(STATE, archive / 'state', copy_function=shutil.copy2, symlinks=True)
        shutil.copy2(ROOT / 'image.env', archive / 'image.env')
        shutil.copy2(ROOT / 'openclaw.json', archive / 'openclaw.json')
    except Exception:
        if enabled:
            run(['/usr/bin/systemctl', 'start', 'duora-agent'])
        raise SystemExit('State backup failed; image and original state unchanged')
    try:
        (ROOT / 'image.env').write_text(f'OPENCLAW_IMAGE={args.approve_image}\n')
        preflight()
        run(['/bin/sh', '/opt/duora-agent/firewall.sh'])
        # Separate ephemeral container: checks schema/plugin/config; no auto-fix of immutable configuration.
        run(COMPOSE + ['run', '--rm', '--no-deps', 'gateway', 'config', 'validate'])
        run(['/usr/bin/systemctl', 'start', 'duora-agent'])
        wait_healthy()
        run(COMPOSE + ['exec', '-T', 'gateway', 'node', '/opt/duora-agent/entrypoint.mjs', 'mcp', 'doctor', 'duora', '--probe'])
        print('Gateway health and MCP passed. Slack round-trip and mobile alert acceptance still required.')
    except Exception:
        run(['/usr/bin/systemctl', 'stop', 'duora-agent'])
        (ROOT / 'image.env').write_text(old)
        for item in STATE.iterdir():
            if item.is_dir() and not item.is_symlink():
                shutil.rmtree(item)
            else:
                item.unlink()
        shutil.copytree(archive / 'state', STATE, copy_function=shutil.copy2, symlinks=True, dirs_exist_ok=True)
        # copytree does not preserve ownership; all runtime files belong to container uid 22001.
        for directory, dirs, files in os.walk(STATE):
            os.chown(directory, 22001, 22001)
            for name in files + dirs:
                os.chown(Path(directory) / name, 22001, 22001, follow_symlinks=False)
        if enabled:
            run(['/usr/bin/systemctl', 'start', 'duora-agent'])
        raise SystemExit('Activation failed; previous image/state restored. Inspect sanitized service logs.')


if __name__ == '__main__':
    main()
