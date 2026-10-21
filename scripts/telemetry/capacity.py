#!/usr/bin/env python3
"""Conservative co-located budget; no production queries, credentials or mutations.

Constants are reconciled against Compose/systemd by test_telemetry_capacity.py.
This gate is necessary, never evidence of load/isolation/restore acceptance.
"""
from pathlib import Path
import re
import json
import stat
import subprocess

MIB = 1024**2
PERMANENT_MIB = 5248
GATEWAY_MIB = 1536
OBSERVERS_MIB = 256
MIGRATOR_MIB = 512
MEDIA_MIB = 1024
HEADROOM_MIB = 1536
REQUIRED_MIB = sum((PERMANENT_MIB, GATEWAY_MIB, OBSERVERS_MIB,
                    MIGRATOR_MIB, MEDIA_MIB, HEADROOM_MIB))


SOURCE_REQUIRED_MIB = PERMANENT_MIB + 128 + 64 + MIGRATOR_MIB + MEDIA_MIB + HEADROOM_MIB
REMOTE_REQUIRED_MIB = GATEWAY_MIB + 3 * 128 + HEADROOM_MIB


def deployment_profile(path=Path('/etc/duora-agent/profile.json')):
    if not path.exists():
        return 'colocated'
    info = path.lstat()
    if not stat.S_ISREG(info.st_mode) or info.st_uid != 0 or stat.S_IMODE(info.st_mode) != 0o644:
        raise ValueError('unsafe deployment profile')
    if json.loads(path.read_text()) != {'profile': 'remote-gateway'}:
        raise ValueError('invalid deployment profile')
    return 'remote-gateway'


def no_product_containers(runner=subprocess.check_output):
    if runner(['/usr/bin/docker', 'ps', '-aq', '--filter', 'label=com.docker.compose.project=duora'],
              text=True, timeout=10).strip():
        raise ValueError('remote gateway requires a host without Duora containers')


def validate_memory(text, profile='colocated'):
    budgets = {'colocated': REQUIRED_MIB, 'source': SOURCE_REQUIRED_MIB, 'remote-gateway': REMOTE_REQUIRED_MIB}
    if profile not in budgets:
        raise ValueError('unknown deployment profile')
    required = budgets[profile]
    values = {}
    for key in ('MemTotal', 'MemAvailable'):
        matches = re.findall(rf'^{key}:\s+(\d+) kB$', text, re.MULTILINE)
        if len(matches) != 1:
            raise ValueError('missing or invalid host memory inventory')
        values[key] = int(matches[0]) * 1024
    if values['MemTotal'] < required * MIB:
        raise ValueError(f'capacity denied: need at least {required} MiB MemTotal; '
                         'keep gateway disabled; resize or review a separate-host topology')
    if values['MemAvailable'] < HEADROOM_MIB * MIB:
        raise ValueError('capacity denied: less than 1536 MiB MemAvailable')


def validate_media_slice(runner=subprocess.check_output):
    value = runner(['/usr/bin/systemctl', 'show', 'duora-media.slice',
                    '--property=MemoryMax', '--value'], text=True, timeout=10).strip()
    if value != str(MEDIA_MIB * MIB):
        raise ValueError('capacity denied: reviewed duora-media.slice 1024 MiB limit required')


def check_capacity():
    profile = deployment_profile()
    validate_memory(Path('/proc/meminfo').read_text(), profile)
    if profile == 'remote-gateway':
        no_product_containers()
        for name in ('duora-telemetry-receiver', 'duora-observer', 'duora-monitor'):
            value = subprocess.check_output(['/usr/bin/systemctl', 'show', name + '.service',
                       '--property=MemoryMax', '--value'], text=True, timeout=10).strip()
            if value != str(128 * MIB):
                raise ValueError('remote resource limits not installed')
    else:
        validate_media_slice()


if __name__ == '__main__':
    # Installer runs before creating any resources; Ansible installs the media slice separately.
    import argparse
    parser = argparse.ArgumentParser()
    parser.add_argument('--profile', choices=('colocated', 'source', 'remote-gateway'), default='colocated')
    parser.add_argument('--runtime', action='store_true')
    args = parser.parse_args()
    profile = args.profile
    try:
        validate_memory(Path('/proc/meminfo').read_text(), profile)
        if profile == 'remote-gateway':
            no_product_containers()
        if profile == 'source' and args.runtime:
            validate_media_slice()
    except (ValueError, OSError) as error:
        raise SystemExit(str(error))
    print('memory planning gate passed; runtime limits and acceptance still required')
