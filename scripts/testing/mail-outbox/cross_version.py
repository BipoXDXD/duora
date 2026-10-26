"""Runs only on an ephemeral GitHub runner, with synthetic PostgreSQL roles and mail."""
import hashlib
import json
import os
from pathlib import Path
import secrets
import subprocess
import sys
import tempfile
import time

if (os.environ.get("GITHUB_ACTIONS") != "true"
        or os.environ.get("RUNNER_ENVIRONMENT") != "github-hosted"
        or sys.platform != "linux"):
    raise SystemExit("Requires an ephemeral GitHub-hosted Linux runner; no local execution")

ROOT = Path(__file__).resolve().parents[1]
REPORT = ROOT / 'artifacts' / 'cross-version.json'
REPORT.parent.mkdir(exist_ok=True)


def run(args, *, env=None, data=None):
    result = subprocess.run(args, cwd=ROOT, env=env, input=data, text=True, capture_output=True, timeout=180)
    if result.returncode:
        # Only synthetic test inputs are used here; no Gmail credentials enter this harness.
        raise RuntimeError(f'{args[0]} failed: {result.stderr[-1800:]} {result.stdout[-1800:]}')
    return result.stdout.strip()


def require(condition, reason):
    if not condition:
        raise AssertionError(reason)


report = {'production_accessed': False, 'external_smtp_proven': False, 'cases': []}
container = 'duora-mail-validation-' + secrets.token_hex(5)
created = False
try:
    with tempfile.TemporaryDirectory(prefix='duora-cross-version-') as temporary:
        temp = Path(temporary)
        temp.chmod(0o755)  # Only synthetic DB credentials; postgres must traverse this bind mount.
        passwords = {role: secrets.token_hex(24) for role in ('migrator', 'app', 'backup', 'postgres')}
        for role, password in passwords.items():
            path = temp / role
            path.write_text(password + '\n')
            path.chmod(0o444)  # Synthetic disposable DB secrets, readable by postgres inside the container.
        command = ['docker', 'run', '-d', '--name', container, '--label', 'duora.isolated-mail-validation=true',
                   '-p', '127.0.0.1::5432', '-e', 'POSTGRES_PASSWORD_FILE=/test-secrets/postgres',
                   '-e', 'DUORA_MIGRATOR_PASSWORD_FILE=/test-secrets/migrator',
                   '-e', 'DUORA_APP_PASSWORD_FILE=/test-secrets/app',
                   '-e', 'DUORA_BACKUP_PASSWORD_FILE=/test-secrets/backup',
                   '-v', f'{temp}:/test-secrets:ro',
                   '-v', f'{ROOT}/source/infra/images/postgres/initdb/10-duora-roles.sh:/docker-entrypoint-initdb.d/10-duora-roles.sh:ro',
                   'postgres:18.6']
        run(command)
        created = True
        for _ in range(60):
            ready = subprocess.run(['docker', 'exec', container, 'pg_isready', '-h', '127.0.0.1', '-U', 'postgres', '-d', 'duora'],
                                   capture_output=True, timeout=5).returncode == 0
            if ready:
                break
            time.sleep(1)
        if not ready:
            print(run(['docker', 'logs', container]))
        require(ready, 'Isolated PostgreSQL was not ready')
        port = run(['docker', 'port', container, '5432/tcp']).split(':')[-1]
        pickup = temp / 'pickup'
        pickup.mkdir()

        def probe(version, mode, *args, role='app', database='duora'):
            env = os.environ.copy()
            env['DUORA_TEST_DB'] = (f'Host=127.0.0.1;Port={port};Database={database};Username=duora_{role};'
                                    f'Password={passwords[role]};Pooling=false')
            env['DUORA_TEST_PICKUP'] = str(pickup)
            binary = ROOT / 'validation' / version / 'bin/Release/net10.0/Duora.IntegrationTests.dll'
            output = run(['dotnet', str(binary), mode, *args], env=env)
            return json.loads(output.splitlines()[-1])

        def sql(statement, database='duora'):
            return run(['docker', 'exec', '-i', container, 'psql', '-X', '-v', 'ON_ERROR_STOP=1',
                        '-U', 'postgres', '-d', database, '-At'], data=statement)

        probe('current', 'migrate', role='migrator')
        sql((ROOT / 'source/infra/images/postgres/reconcile-privileges.sql').read_text())

        def delivery_case(name, producer, consumer, kind, expected='Delivered', database='duora'):
            recorded = probe(producer, 'produce', kind, database=database)
            consumed = probe(consumer, 'consume', database=database)
            state = probe('current', 'inspect', recorded['delivery_id'], database=database)
            require(consumed['claimed'] == 1, name + ': expected one real claim')
            require(state['status'] == expected, name + ': wrong terminal status')
            require(state['payload_cleared'], name + ': terminal payload not cleared')
            receipt = pickup / (recorded['delivery_id'].replace('-', '') + '.eml')
            if expected == 'Delivered':
                require(receipt.is_file(), name + ': no pickup receipt')
                receipt_hash = hashlib.sha256(receipt.read_bytes()).hexdigest()
            else:
                require(not receipt.exists(), name + ': invalid notice unexpectedly sent')
                require(state['error_type'] == 'InvalidPayload', name + ': wrong rejection')
                receipt_hash = None
            report['cases'].append({'name': name, 'producer': producer, 'consumer': consumer,
                                    **recorded, **consumed, **state, 'receipt_sha256': receipt_hash})

        delivery_case('old producer to current reader', 'legacy', 'current', 'PasswordChangedNotice')
        delivery_case('139 producer to current reader', 'reader', 'current', 'RecoveryCodeUsedNotice')
        delivery_case('current producer to 139 reader', 'current', 'reader', 'RecoveryCodeUsedNotice')
        held = probe('current', 'produce', 'RecoveryCodeUsedNotice')
        require(not probe('current', 'guard')['rollback_allowed'], 'Rollback must block while Pending')
        require(probe('current', 'claim')['claimed'] == 1, 'Could not simulate a crashed lease owner')
        require(probe('current', 'inspect', held['delivery_id'])['status'] == 'InFlight', 'No persisted lease')
        require(not probe('current', 'guard')['rollback_allowed'], 'Rollback must block while InFlight')
        time.sleep(2.3)
        recovered = probe('reader', 'consume')
        state = probe('current', 'inspect', held['delivery_id'])
        receipt = pickup / (held['delivery_id'].replace('-', '') + '.eml')
        require(state['status'] == 'Delivered' and state['attempts'] == 2 and receipt.is_file(), 'Lease recovery failed')
        require(probe('current', 'guard')['rollback_allowed'], 'Drain did not finish')
        report['cases'].append({'name': 'block rollback, recover abandoned lease, prove drain', **held, **state,
                                **recovered, 'receipt_sha256': hashlib.sha256(receipt.read_bytes()).hexdigest()})
        delivery_case('old consumer after proven drain', 'legacy', 'legacy', 'PasswordChangedNotice')
        # Separate database: the deliberate incompatible-reader loss never affects the positive rehearsal.
        sql('CREATE DATABASE duora_negative TEMPLATE duora OWNER duora_migrator;', database='postgres')
        sql('REVOKE ALL ON DATABASE duora_negative FROM PUBLIC; GRANT CONNECT ON DATABASE duora_negative TO duora_app;',
            database='postgres')
        delivery_case('negative control: pre139 reader rejects new kind', 'current', 'legacy',
                      'RecoveryCodeUsedNotice', expected='Failed', database='duora_negative')
        ids = [case['delivery_id'] for case in report['cases']]
        require(len(ids) == len(set(ids)), 'Delivery IDs unexpectedly repeated')
        report['success'] = True
finally:
    if created:
        run(['docker', 'rm', '-fv', container])
    report['container_removed'] = created
    REPORT.write_text(json.dumps(report, indent=2) + '\n')
print(json.dumps(report, indent=2))
