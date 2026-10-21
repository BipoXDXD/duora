#!/usr/bin/env python3
"""Root-owned, scheduled collector. No input from Slack/LLM; no secret/log payload export."""
import json
import math
import os
from pathlib import Path
import subprocess
import time

SERVICES = ('postgres', 'web', 'worker', 'edge', 'backup')
UNITS = ('duora-backup@verify.service', 'duora-backup@full.service',
         'duora-backup@diff.service', 'duora-backup@verify-repo.service',
         'duora-backup@verify-repo-full.service', 'duora-media-backup@media-backup.service',
         'duora-media-backup@media-verify.service', 'duora-media-backup@media-check.service',
         'duora-backup-egress.service')
TIMERS = ('duora-backup-full.timer', 'duora-backup-diff.timer', 'duora-backup-verify.timer',
          'duora-backup-verify-repo.timer', 'duora-backup-verify-repo-full.timer',
          'duora-backup-media.timer', 'duora-backup-media-verify.timer',
          'duora-backup-media-check.timer')


def run(args):
    result = subprocess.run(args, text=True, capture_output=True, timeout=8, check=True,
                            env={'PATH': '/usr/sbin:/usr/bin:/sbin:/bin', 'LC_ALL': 'C'})
    return result.stdout


def atomic(path, value, mode=0o644):
    path = Path(path)
    tmp = path.with_suffix('.tmp')
    fd = os.open(tmp, os.O_WRONLY | os.O_CREAT | os.O_TRUNC | os.O_NOFOLLOW, mode)
    with os.fdopen(fd, 'w') as stream:
        json.dump(value, stream, allow_nan=False)
        stream.flush()
        os.fsync(stream.fileno())
    os.replace(tmp, path)


def marker(path, now, maximum):
    try:
        age = now - float(path.read_text())
        return math.isfinite(age) and 0 <= age <= maximum
    except (ValueError, OSError):
        return False


def collect(runner=run, state=Path('/var/lib/duora'), now=None, include_gateway=True):
    now = time.time() if now is None else now
    checks = {}
    for service in (*SERVICES, *(['gateway'] if include_gateway else [])):
        try:
            ids = runner(['/usr/bin/docker', 'ps', '-aq', '--filter',
                          f"label=com.docker.compose.project={'duora-agent' if service == 'gateway' else 'duora'}", '--filter',
                          f'label=com.docker.compose.service={service}']).split()
            if len(ids) != 1:
                raise ValueError('missing or duplicate')
            # Inspect ONLY state, never Config.Env, mounts, labels or health log text.
            item = json.loads(runner(['/usr/bin/docker', 'inspect', '--format',
                                     '{{json .State}}', ids[0]]))
            checks[f'service:{service}'] = (item.get('Status') == 'running'
                and not item.get('Restarting') and not item.get('OOMKilled')
                and item.get('Health', {}).get('Status', 'healthy') == 'healthy')
            if service == 'edge':
                runner(['/usr/bin/docker', 'exec', ids[0], 'wget', '--quiet', '--spider',
                        '--timeout=5', 'http://web:8080/health/ready'])
                checks['web:ready'] = True
        except (OSError, ValueError, subprocess.SubprocessError):
            checks[f'service:{service}'] = False
            if service == 'edge':
                checks['web:ready'] = False
    for unit in UNITS + TIMERS:
        try:
            values = dict(line.split('=', 1) for line in runner([
                '/usr/bin/systemctl', 'show', unit, '--property=LoadState,ActiveState,Result']).splitlines())
            checks[f'unit:{unit}'] = (values.get('LoadState') == 'loaded'
                and values.get('ActiveState') != 'failed'
                and values.get('Result', 'success') == 'success'
                and (not unit.endswith('.timer') or values.get('ActiveState') == 'active'))
        except (OSError, ValueError, subprocess.SubprocessError):
            checks[f'unit:{unit}'] = False
    checks['backup:verified-recently'] = marker(state / '.backup-verify-ok', now, 3600)
    checks['media:verified-recently'] = marker(state / '.media-verify-ok', now, 30 * 3600)
    return {'collected_at': now, 'checks': checks}


def gateway_snapshot(runner=run, now=None):
    good = False
    try:
        ids = runner(['/usr/bin/docker', 'ps', '-aq', '--filter',
                      'label=com.docker.compose.project=duora-agent', '--filter',
                      'label=com.docker.compose.service=gateway']).split()
        if len(ids) == 1:
            state = json.loads(runner(['/usr/bin/docker', 'inspect', '--format', '{{json .State}}', ids[0]]))
            good = (state.get('Status') == 'running' and not state.get('Restarting')
                    and not state.get('OOMKilled') and state.get('Health', {}).get('Status') == 'healthy')
    except (OSError, ValueError, subprocess.SubprocessError):
        pass
    return {'collected_at': time.time() if now is None else now, 'checks': {'service:gateway': bool(good)}}


if __name__ == '__main__':
    import argparse
    parser = argparse.ArgumentParser()
    parser.add_argument('--mode', choices=('colocated', 'source', 'gateway'), default='colocated')
    mode = parser.parse_args().mode
    path = '/var/lib/duora-telemetry/host.json' if mode == 'source' else '/var/lib/duora-observer/host.json'
    atomic(path, gateway_snapshot() if mode == 'gateway' else collect(include_gateway=mode == 'colocated'))
