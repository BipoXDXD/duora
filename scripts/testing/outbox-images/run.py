"""Hosted-only, synthetic full-image outbox rehearsal. Never print container logs."""
import hashlib
import json
import os
from pathlib import Path
import platform
import secrets
import subprocess
import sys
import tempfile
import time

ROOT = Path(__file__).resolve().parents[3]
LAB = ROOT / '.mail-lab'
PINS = Path(__file__).with_name('image-pins.json')
REPORT = LAB / 'artifacts' / 'full-image.json'
SOURCE = 'https://github.com/BipoXDXD/duora'
REVISIONS = {
    'legacy': 'bbdf0da978e4c700872a278e3af0d55592db2f2d',
    'compatible': 'f3c52b229ac1af1a7d56f17f9e8124a8afe5da23',
    'current': 'd7ed56bfb81a8ba25066820101f8c8f842eb2e32',
}
POSTGRES = ('postgres:18.6-trixie@sha256:'
            '74935e72241653ca55e0414067e6d8763aceb8a810eb51b452253ec3dcfc4336')


class SafetyError(RuntimeError):
    pass


class StepError(RuntimeError):
    pass


def require_hosted_linux():
    if (os.environ.get('GITHUB_ACTIONS') != 'true'
            or os.environ.get('RUNNER_ENVIRONMENT') != 'github-hosted'
            or sys.platform != 'linux'
            or platform.machine() not in ('x86_64', 'amd64')):
        raise SafetyError('Requires a GitHub-hosted Linux amd64 runner')


def require(condition, label):
    if not condition:
        raise AssertionError(label)


def checked(args, *, data=None, env=None, timeout=180):
    completed = subprocess.run(args, cwd=ROOT, input=data, text=True, env=env,
                               capture_output=True, timeout=timeout, check=False)
    if completed.returncode:
        raise StepError(f'{Path(args[0]).name} exited {completed.returncode}')
    return completed.stdout.strip()


class Resources:
    def __init__(self, command=checked):
        self.command = command
        self.network = None
        self.containers = []

    def add(self, name):
        # Register before creation: cleanup also handles a partially successful Docker call.
        self.containers.append(name)

    def cleanup(self):
        failures = []
        for name in reversed(self.containers):
            try:
                self.command(['docker', 'rm', '-fv', name])
            except Exception as error:
                failures.append(type(error).__name__)
        if self.network:
            try:
                self.command(['docker', 'network', 'rm', self.network])
            except Exception as error:
                failures.append(type(error).__name__)
        return failures


def require_delivered(delivery_id, state, receipt):
    require(state['status'] == 'Delivered', 'known_id_not_delivered')
    require(state['payload_cleared'] is True, 'known_id_payload_not_cleared')
    require(receipt is not None and len(receipt) > 0, 'known_id_receipt_missing')
    return hashlib.sha256(receipt).hexdigest()


class Rehearsal:
    def __init__(self):
        self.label = 'duora-outbox-image-' + secrets.token_hex(6)
        self.resources = Resources()
        self.passwords = {role: secrets.token_hex(24) for role in
                          ('postgres', 'migrator', 'app', 'backup')}
        self.stage = 'initialization'
        self.report = {'success': False, 'production_accessed': False,
                       'external_smtp_proven': False, 'phases': [], 'images': [],
                       'known_ids': [], 'cleanup': {'complete': False}}
        self.known = {}
        self.containers = {}
        self.container_image_ids = {}
        self.port = None
        self.temp = None

    def name(self, role):
        return self.label + '-' + role

    def source_check(self):
        self.stage = 'source_provenance'
        for role, path in (('current', LAB / 'source'),
                           ('legacy', LAB / 'source/legacy'),
                           ('compatible', LAB / 'source/compatible')):
            observed = checked(['git', '-C', str(path), 'rev-parse', 'HEAD'])
            require(observed == REVISIONS[role], 'source_revision_mismatch')
        relative = 'backend/src/Duora.Infrastructure/Email/AccountEmailOutbox.cs'
        legacy = (LAB / 'source/legacy' / relative).read_text()
        compatible = (LAB / 'source/compatible' / relative).read_text()
        require('RecoveryCodeUsedNotice' not in legacy, 'legacy_source_has_new_kind')
        require('RecoveryCodeUsedNotice' in compatible, 'compatible_source_lacks_new_kind')
        self.report['source_revisions'] = REVISIONS.copy()

    def images_check(self):
        self.stage = 'image_provenance'
        pins = json.loads(PINS.read_text())
        require(len(pins) == 4, 'pin_count')
        self.pins = {}
        for pin in pins:
            key = (pin['role'], pin['component'])
            require(key not in self.pins, 'duplicate_pin')
            require(pin['role'] in ('legacy', 'compatible') and
                    pin['component'] in ('web', 'worker'), 'pin_role')
            require(pin['source'] == REVISIONS[pin['role']], 'pin_source')
            require(pin['reference'].endswith('@' + pin['native_manifest_digest']) and
                    pin['tag_manifest_digest'] == pin['native_manifest_digest'], 'pin_manifest')
            require(pin['os'] == 'linux' and pin['architecture'] == 'amd64' and
                    pin['user'] == '1654:1654', 'pin_platform_user')
            require(pin['entrypoint'] == ['dotnet', f'Duora.{pin["component"].title()}.dll'],
                    'pin_entrypoint')
            checked(['docker', 'pull', '--platform', 'linux/amd64', pin['reference']], timeout=600)
            image = json.loads(checked(['docker', 'image', 'inspect', pin['reference']]))[0]
            config = image['Config']
            labels = config.get('Labels') or {}
            require(image['Id'] == pin['config_digest'], 'image_config_digest')
            require(pin['reference'] in image['RepoDigests'], 'image_repo_digest')
            require(image['Os'] == pin['os'] and image['Architecture'] == pin['architecture'],
                    'image_platform')
            require(config['User'] == pin['user'] and
                    config['Entrypoint'] == pin['entrypoint'], 'image_execution_config')
            require(labels.get('org.opencontainers.image.revision') == pin['source'] and
                    labels.get('org.opencontainers.image.source') == SOURCE,
                    'image_source_labels')
            self.pins[key] = pin
            self.report['images'].append({'role': pin['role'], 'component': pin['component'],
                                          'manifest_digest': pin['native_manifest_digest'],
                                          'image_id': image['Id'], 'source': pin['source'],
                                          'architecture': image['Architecture'],
                                          'user': config['User'], 'entrypoint': config['Entrypoint']})
        require(len(self.pins) == 4, 'missing_pin')

    def setup_database(self):
        self.stage = 'disposable_database'
        self.temp = tempfile.TemporaryDirectory(prefix='duora-full-image-')
        self.path = Path(self.temp.name)
        self.path.chmod(0o755)
        for role, password in self.passwords.items():
            file = self.path / role
            file.write_text(password + '\n')
            file.chmod(0o444)
        for directory in ('pickup', 'negative-pickup'):
            pickup = self.path / directory
            pickup.mkdir(mode=0o777)
            pickup.chmod(0o777)
        network = self.name('network')
        self.resources.network = network
        checked(['docker', 'network', 'create', '--internal', '--driver', 'bridge',
                 '--label', self.label, network])
        postgres = self.name('postgres')
        self.resources.add(postgres)
        source_script = LAB / 'source/infra/images/postgres/initdb/10-duora-roles.sh'
        checked(['docker', 'run', '-d', '--name', postgres, '--label', self.label,
                 '--network', network, '--network-alias', 'postgres',
                 '-p', '127.0.0.1::5432',
                 '-e', 'POSTGRES_PASSWORD_FILE=/test-secrets/postgres',
                 '-e', 'DUORA_MIGRATOR_PASSWORD_FILE=/test-secrets/migrator',
                 '-e', 'DUORA_APP_PASSWORD_FILE=/test-secrets/app',
                 '-e', 'DUORA_BACKUP_PASSWORD_FILE=/test-secrets/backup',
                 '--mount', f'type=bind,src={self.path},dst=/test-secrets,readonly',
                 '--mount', f'type=bind,src={source_script},dst=/docker-entrypoint-initdb.d/10-duora-roles.sh,readonly',
                 POSTGRES])
        self.containers['postgres'] = postgres
        for _ in range(90):
            result = subprocess.run(['docker', 'exec', postgres, 'pg_isready', '-h', '127.0.0.1',
                                     '-U', 'postgres', '-d', 'duora'], capture_output=True,
                                    text=True, timeout=10, check=False)
            if result.returncode == 0:
                break
            time.sleep(1)
        else:
            raise AssertionError('postgres_not_ready')
        published = checked(['docker', 'port', postgres, '5432/tcp'])
        require(published.startswith('127.0.0.1:'), 'postgres_not_loopback_only')
        self.port = published.rsplit(':', 1)[1]
        self.probe('migrate', role='migrator')
        self.sql((LAB / 'source/infra/images/postgres/reconcile-privileges.sql').read_text())
        self.sql('CREATE DATABASE duora_negative TEMPLATE duora OWNER duora_migrator;', 'postgres')
        self.sql('REVOKE ALL ON DATABASE duora_negative FROM PUBLIC; '
                 'GRANT CONNECT ON DATABASE duora_negative TO duora_app;', 'postgres')

    def sql(self, statement, database='duora'):
        return checked(['docker', 'exec', '-i', self.containers['postgres'], 'psql', '-X',
                        '-v', 'ON_ERROR_STOP=1', '-U', 'postgres', '-d', database, '-At'],
                       data=statement)

    def probe(self, mode, *args, role='app', database='duora'):
        env = os.environ.copy()
        env['DUORA_TEST_DB'] = (f'Host=127.0.0.1;Port={self.port};Database={database};'
                                f'Username=duora_{role};Password={self.passwords[role]};Pooling=false')
        env['DUORA_TEST_PICKUP'] = str(self.path / 'pickup')
        binary = LAB / 'validation/current/bin/Release/net10.0/Duora.IntegrationTests.dll'
        result = checked(['dotnet', str(binary), mode, *args], env=env)
        return json.loads(result.splitlines()[-1])

    def app_env(self, database):
        return {
            'ConnectionStrings__Duora': (f'Host=postgres;Database={database};Username=duora_app;'
                                         f'Password={self.passwords["app"]};Pooling=false'),
            'Duora__PublicOrigin': 'https://duora-mail-test.invalid',
            'Duora__SupportContact': 'support@example.invalid',
            'Email__Provider': 'PickupDirectory',
            'Email__PickupDirectory': '/pickup',
            'Outbox__PollInterval': '00:00:01',
            'Media__Storage__Provider': 'FileSystem',
            'Media__Storage__FileSystemRoot': '/tmp/media',
            'DataProtection__RequireMountedVolume': 'false',
            'DataProtection__RequireCertificate': 'false',
            'DataProtection__KeysDirectory': '/tmp/keys',
            'DOTNET_ENVIRONMENT': 'Development',
            'ASPNETCORE_ENVIRONMENT': 'Development',
        }

    def start(self, component, role, database='duora'):
        self.stage = f'start_{component}_{role}'
        name = self.name(component + '-' + role + ('-negative' if database != 'duora' else ''))
        pin = self.pins[(role, component)]
        pickup = self.path / ('negative-pickup' if database != 'duora' else 'pickup')
        args = ['docker', 'run', '-d', '--name', name, '--label', self.label,
                '--network', self.resources.network, '--read-only', '--cap-drop=ALL',
                '--security-opt=no-new-privileges', '--user', '1654:1654',
                '--tmpfs', '/tmp:rw,nosuid,nodev,size=64m,mode=1777',
                '--mount', f'type=bind,src={pickup},dst=/pickup']
        for key, value in self.app_env(database).items():
            args.extend(['-e', f'{key}={value}'])
        args.append(pin['reference'])
        self.resources.add(name)
        checked(args)
        self.containers[component] = name
        self.container_image_ids[name] = pin['config_digest']
        self.wait_host(name, component)
        self.report['phases'].append({'phase': self.stage, 'component': component, 'role': role,
                                      'database': 'negative' if database != 'duora' else 'positive',
                                      'image_id': pin['config_digest'], 'host_started': True})

    def wait_host(self, name, component):
        marker = 'Now listening on:' if component == 'web' else 'Application started.'
        for _ in range(45):
            self.require_running(name)
            observed = subprocess.run(['docker', 'logs', name], cwd=ROOT, text=True,
                                      capture_output=True, timeout=10, check=False)
            if observed.returncode:
                raise StepError('docker logs unavailable')
            logs = observed.stdout + observed.stderr  # Read in memory only; never print or upload.
            if marker in logs:
                return
            time.sleep(1)
        raise AssertionError('host_start_marker_missing')

    def require_running(self, name):
        state = json.loads(checked(['docker', 'inspect', name]))[0]
        require(state['State']['Running'] is True, 'container_not_running')
        require(state['Image'] == self.container_image_ids[name], 'container_image_mismatch')

    def stop(self, component):
        name = self.containers.pop(component)
        checked(['docker', 'stop', '-t', '15', name], timeout=35)
        checked(['docker', 'rm', '-fv', name])
        self.resources.containers.remove(name)
        del self.container_image_ids[name]

    def receipt(self, delivery_id, database='duora'):
        pickup = self.path / ('negative-pickup' if database != 'duora' else 'pickup')
        path = pickup / (delivery_id.replace('-', '') + '.eml')
        return path.read_bytes() if path.is_file() else None

    def produce(self, kind, database='duora'):
        recorded = self.probe('produce', kind, database=database)
        delivery_id = recorded['delivery_id']
        require(delivery_id not in self.known, 'duplicate_delivery_id')
        self.known[delivery_id] = database
        self.report['known_ids'].append({'delivery_id': delivery_id, 'kind': kind,
                                         'database': 'negative' if database != 'duora' else 'positive'})
        return delivery_id

    def await_state(self, delivery_id, expected, database='duora', seconds=90):
        end = time.monotonic() + seconds
        while time.monotonic() < end:
            state = self.probe('inspect', delivery_id, database=database)
            if state['status'] == expected:
                return state
            if state['status'] in ('Delivered', 'Failed'):
                raise AssertionError('unexpected_terminal_status')
            time.sleep(1)
        raise AssertionError('delivery_timeout')

    def prove_delivery(self, delivery_id, phase, database='duora'):
        self.stage = phase
        state = self.await_state(delivery_id, 'Delivered', database)
        digest = require_delivered(delivery_id, state, self.receipt(delivery_id, database))
        self.report['phases'].append({'phase': phase, 'delivery_id': delivery_id,
                                      'status': state['status'], 'attempts': state['attempts'],
                                      'payload_cleared': True, 'receipt_sha256': digest})
        return state

    def require_drain(self):
        self.stage = 'known_id_drain'
        guard = self.probe('guard')
        require(guard['rollback_allowed'] is True and guard['pending_new_kind'] == 0,
                'rollback_guard_not_clear')
        for delivery_id, database in self.known.items():
            if database != 'duora':
                continue
            state = self.probe('inspect', delivery_id)
            digest = require_delivered(delivery_id, state, self.receipt(delivery_id))
            require(len(digest) == 64, 'receipt_digest')
        self.report['phases'].append({'phase': 'known_id_drain', 'known_count': len(self.known),
                                      'rollback_allowed': True, 'all_delivered_with_receipts': True})

    def run(self):
        self.source_check()
        self.images_check()
        self.setup_database()
        self.start('worker', 'compatible')
        self.start('web', 'legacy')
        self.require_running(self.containers['web'])
        first = self.produce('RecoveryCodeUsedNotice')
        self.prove_delivery(first, 'mixed_release_new_kind')
        self.require_running(self.containers['web'])

        self.stop('web')
        self.start('web', 'compatible')
        second = self.produce('RecoveryCodeUsedNotice')
        self.prove_delivery(second, 'forward_web_new_kind')
        self.require_running(self.containers['web'])

        self.stop('web')
        self.start('web', 'legacy')  # Web rollback must precede any Worker rollback.
        self.stop('worker')
        self.stage = 'rollback_pending_block'
        held = self.produce('RecoveryCodeUsedNotice')
        guard = self.probe('guard')
        require(guard['rollback_allowed'] is False and guard['pending_new_kind'] > 0,
                'pending_did_not_block_rollback')
        self.report['phases'].append({'phase': 'rollback_pending_block', 'delivery_id': held,
                                      'status': 'Pending', 'rollback_allowed': False})
        self.stage = 'rollback_inflight_block'
        claimed = self.probe('claim')
        require(claimed['claimed'] == 1, 'claim_not_acquired')
        state = self.probe('inspect', held)
        guard = self.probe('guard')
        require(state['status'] == 'InFlight' and guard['rollback_allowed'] is False,
                'inflight_did_not_block_rollback')
        self.report['phases'].append({'phase': 'rollback_inflight_block', 'delivery_id': held,
                                      'status': state['status'], 'rollback_allowed': False})
        self.start('worker', 'compatible')
        recovered = self.prove_delivery(held, 'compatible_worker_lease_recovery')
        require(recovered['attempts'] >= 2, 'lease_not_recovered')
        self.require_running(self.containers['web'])
        self.require_drain()

        self.stop('worker')
        self.start('worker', 'legacy')
        old = self.produce('PasswordChangedNotice')
        self.prove_delivery(old, 'old_worker_after_drain')
        self.require_running(self.containers['web'])

        self.stop('worker')
        self.start('worker', 'legacy', 'duora_negative')
        self.stage = 'negative_old_worker'
        rejected = self.produce('RecoveryCodeUsedNotice', 'duora_negative')
        failed = self.await_state(rejected, 'Failed', 'duora_negative')
        require(failed['error_type'] == 'InvalidPayload' and failed['payload_cleared'] is True
                and self.receipt(rejected, 'duora_negative') is None,
                'negative_control_not_rejected_without_receipt')
        self.report['phases'].append({'phase': 'negative_old_worker', 'delivery_id': rejected,
                                      'status': 'Failed', 'error_type': 'InvalidPayload',
                                      'payload_cleared': True, 'receipt_sha256': None})
        self.report['success'] = True


def main():
    require_hosted_linux()  # First action; never invoke an external process on local/self-hosted machines.
    rehearsal = Rehearsal()
    try:
        rehearsal.run()
    except Exception as error:
        rehearsal.report['failure'] = {'stage': rehearsal.stage,
                                       'exception_type': type(error).__name__}
    finally:
        failures = rehearsal.resources.cleanup()
        rehearsal.report['cleanup'] = {'complete': not failures,
                                        'failure_types': failures}
        if failures:
            rehearsal.report['success'] = False
        if rehearsal.temp:
            rehearsal.temp.cleanup()
        REPORT.parent.mkdir(parents=True, exist_ok=True)
        REPORT.write_text(json.dumps(rehearsal.report, indent=2) + '\n')
    print(json.dumps({'success': rehearsal.report['success'], 'stage': rehearsal.stage,
                      'cleanup_complete': rehearsal.report['cleanup']['complete']}))
    return 0 if rehearsal.report['success'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
