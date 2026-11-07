#!/usr/bin/env python3
"""Root-owned, scheduled collector. No input from Slack/processamento externo; no secret/log payload export."""
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
SELFIE_PROVIDER = 'selfie:provider'
SELFIE_PROBE = 'selfie:probe'
# Peer no socket do próprio container, como duora-deploy: nenhuma credencial nem rota nova.
SELFIE_CONNECTION = ("dbname=duora user=postgres connect_timeout=3 application_name=duora-collect "
                     "options='-c default_transaction_read_only=on -c statement_timeout=3000 -c lock_timeout=1000'")
# Uma tentativa falha agora se a última chamada ao fornecedor falhou. `available_at` e o lease provam que o worker
# gravou isso há pouco: com o worker parado o marcador envelhece, e quem alerta é service:worker. Lê só a fila, por
# dois exists para usar os índices parciais de cada status, e nunca de quem é a tentativa.
SELFIE_PROVIDER_QUERY = """
select exists (select 1 from ops.outbox_deliveries
               where consumer = 'selfie-provider' and status = 'Pending'
                 and available_at >= now() - interval '60 seconds' and last_error = 'ProviderUnavailable')
    or exists (select 1 from ops.outbox_deliveries
               where consumer = 'selfie-provider' and status = 'InFlight'
                 and lease_expires_at >= now() and last_error = 'ProviderUnavailable')
"""


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


def selfie_provider_failing(runner):
    ids = runner(['/usr/bin/docker', 'ps', '-aq', '--filter', 'label=com.docker.compose.project=duora', '--filter',
                  'label=com.docker.compose.service=postgres']).split()
    if len(ids) != 1:
        raise ValueError('missing or duplicate')
    answer = runner(['/usr/bin/docker', 'exec', ids[0], 'psql', '--no-psqlrc', '--no-password', '--quiet',
                     '--tuples-only', '--no-align', '--dbname', SELFIE_CONNECTION,
                     '--command', SELFIE_PROVIDER_QUERY]).strip()
    if answer not in ('t', 'f'):
        raise ValueError('unexpected probe answer')
    return answer == 't'


def selfie_checks(runner):
    """Sonda quebrada não é fornecedor caído: sem a resposta, selfie:provider fica de fora em vez de mentir."""
    try:
        return {SELFIE_PROBE: True, SELFIE_PROVIDER: not selfie_provider_failing(runner)}
    except (OSError, ValueError, subprocess.SubprocessError):
        return {SELFIE_PROBE: False}


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
                        '--timeout=5', 'http://duora-web:8080/health/ready'])
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
    checks.update(selfie_checks(runner))
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
