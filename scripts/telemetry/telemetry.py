"""Fixed public telemetry contract; no commands, paths, names, logs or participant data."""
import json
import math
import os
from pathlib import Path
import re
import ssl
from collect import SERVICES, UNITS, TIMERS

MAX_BYTES = 16384
MAX_AGE = 120
CHECKS = frozenset([*(f'service:{s}' for s in SERVICES), 'web:ready',
                    *(f'unit:{u}' for u in UNITS + TIMERS),
                    'backup:verified-recently', 'media:verified-recently'])


def decode(raw):
    if len(raw) > MAX_BYTES:
        raise ValueError('oversize snapshot')
    def unique(pairs):
        result = {}
        for key, value in pairs:
            if key in result:
                raise ValueError('duplicate key')
            result[key] = value
        return result
    try:
        value = json.loads(raw, object_pairs_hook=unique)
    except (ValueError, UnicodeError, RecursionError) as error:
        raise ValueError('invalid snapshot JSON') from error
    if type(value) is not dict:
        raise ValueError('snapshot must be an object')
    return value


def snapshot(value, now):
    if type(value) is not dict or set(value) != {'collected_at', 'checks'}:
        raise ValueError('unexpected snapshot fields')
    stamp = value['collected_at']
    if type(stamp) not in (int, float) or not 0 < stamp <= now or now - stamp > MAX_AGE or not math.isfinite(stamp):
        raise ValueError('invalid snapshot age')
    checks = value['checks']
    if type(checks) is not dict or set(checks) != CHECKS or any(type(v) is not bool for v in checks.values()):
        raise ValueError('unexpected snapshot checks')
    return {'collected_at': stamp, 'checks': {key: checks[key] for key in sorted(CHECKS)}}


def bounded_read(path):
    with Path(path).open('rb') as stream:
        raw = stream.read(MAX_BYTES + 1)
    return decode(raw)


def publish(path, value):
    # One durable file is both telemetry and replay watermark: acknowledge only after directory fsync.
    path = Path(path)
    tmp = path.with_suffix('.tmp')
    fd = os.open(tmp, os.O_WRONLY | os.O_CREAT | os.O_TRUNC | os.O_NOFOLLOW, 0o644)
    with os.fdopen(fd, 'w') as stream:
        json.dump(value, stream, allow_nan=False, sort_keys=True)
        stream.flush()
        os.fsync(stream.fileno())
    os.replace(tmp, path)
    fd = os.open(path.parent, os.O_RDONLY)
    try:
        os.fsync(fd)
    finally:
        os.close(fd)


def confirm_durable(path):
    # A previous rename may have succeeded just before fsync failed or the process crashed.
    # Idempotent ACK must reestablish durability even after receiver restart.
    path = Path(path)
    for target in (path, path.parent):
        fd = os.open(target, os.O_RDONLY | os.O_NOFOLLOW)
        try:
            os.fsync(fd)
        finally:
            os.close(fd)


def pins(values):
    if type(values) is not list or not 1 <= len(values) <= 2 or any(
            type(p) is not str or re.fullmatch(r'[0-9a-f]{64}', p) is None for p in values):
        raise ValueError('one or two reviewed certificate fingerprints required')
    return values


def tls_context(role, credentials):
    # Explicit contexts do not inherit SSLKEYLOGFILE or system trust/proxy settings.
    context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER if role == 'server' else ssl.PROTOCOL_TLS_CLIENT)
    context.minimum_version = ssl.TLSVersion.TLSv1_2
    context.verify_mode = ssl.CERT_REQUIRED
    context.load_verify_locations(cafile=str(credentials / 'ca.pem'))
    context.load_cert_chain(str(credentials / 'cert.pem'), str(credentials / 'key.pem'))
    return context
