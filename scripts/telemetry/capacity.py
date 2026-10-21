#!/usr/bin/env python3
"""Conservative co-located budget; no production queries, credentials or mutations.

Constants are reconciled against Compose/systemd by test_telemetry_capacity.py.
This gate is necessary, never evidence of load/isolation/restore acceptance.
"""
from pathlib import Path
import re
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


def validate_memory(text):
    values = {}
    for key in ('MemTotal', 'MemAvailable'):
        matches = re.findall(rf'^{key}:\s+(\d+) kB$', text, re.MULTILINE)
        if len(matches) != 1:
            raise ValueError('missing or invalid host memory inventory')
        values[key] = int(matches[0]) * 1024
    if values['MemTotal'] < REQUIRED_MIB * MIB:
        raise ValueError(f'capacity denied: need at least {REQUIRED_MIB} MiB MemTotal; '
                         'keep gateway disabled; resize or review a separate-host topology')
    if values['MemAvailable'] < HEADROOM_MIB * MIB:
        raise ValueError('capacity denied: less than 1536 MiB MemAvailable')


def validate_media_slice(runner=subprocess.check_output):
    value = runner(['/usr/bin/systemctl', 'show', 'duora-media.slice',
                    '--property=MemoryMax', '--value'], text=True, timeout=10).strip()
    if value != str(MEDIA_MIB * MIB):
        raise ValueError('capacity denied: reviewed duora-media.slice 1024 MiB limit required')


def check_capacity():
    validate_memory(Path('/proc/meminfo').read_text())
    validate_media_slice()


if __name__ == '__main__':
    # Installer runs before creating any resources; Ansible installs the media slice separately.
    try:
        validate_memory(Path('/proc/meminfo').read_text())
    except (ValueError, OSError) as error:
        raise SystemExit(str(error))
    print('memory planning gate passed; runtime limits and acceptance still required')
