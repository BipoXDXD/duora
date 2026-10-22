import copy
import importlib.util
import json
import os
import pathlib
import subprocess
import tempfile
import unittest
from unittest.mock import patch

ROOT = pathlib.Path(__file__).parent
spec = importlib.util.spec_from_file_location('wrapper', ROOT / 'observer.py')
wrapper = importlib.util.module_from_spec(spec)
spec.loader.exec_module(wrapper)


class ObserverSafetyTests(unittest.TestCase):
    def setUp(self):
        self.config = {'application_uuid': 'abc123', 'compose': '/opt/duora/compose.json',
                       'observer_healthcheck': copy.deepcopy(wrapper.HEALTHCHECK)}
        self.manifest = {'images': {'edge': 'ghcr.io/bipoxdxd/duora-edge@sha256:' + 'a' * 64}}
        self.runtime = {
            'name': 'abc123',
            'networks': {'default': {'name': 'abc123_default', 'ipam': {}}},
            'services': {'deployment-observer': {
                'image': self.manifest['images']['edge'], 'entrypoint': wrapper.ENTRYPOINT.copy(),
                'command': None, 'user': '10001:10001', 'read_only': True, 'cap_drop': ['ALL'],
                'security_opt': ['no-new-privileges:true'], 'restart': 'unless-stopped',
                'mem_limit': '16777216', 'pids_limit': 16,
                'labels': {'traefik.enable': 'false'}, 'networks': {'default': None},
                'healthcheck': copy.deepcopy(wrapper.HEALTHCHECK)}}}
        self.runtime['networks']['duora_edge'] = {'name': 'duora_edge', 'external': True, 'ipam': {}}
        self.runtime['services']['deployment-observer']['networks']['duora_edge'] = None
        self.config['observer_networks'] = copy.deepcopy(self.runtime['networks'])

    def validate(self, runtime=None):
        return wrapper.validate_observer(runtime or self.runtime, self.config, self.manifest)

    def test_expected_observer(self):
        self.assertEqual(self.validate(), self.runtime)

    def test_reject_privileges_mounts_hooks_and_other_extra_fields(self):
        extras = {'privileged': True, 'cap_add': ['SYS_ADMIN'], 'devices': ['/dev/sda'],
                  'pid': 'host', 'network_mode': 'host', 'volumes': ['/etc:/host'],
                  'secrets': ['host-secret'], 'configs': ['host-file'], 'build': '.',
                  'post_start': [{'command': 'touch /host/file'}], 'pre_stop': [{'command': 'id'}],
                  'ports': ['8080:80'], 'env_file': ['/etc/private'], 'use_api_socket': True,
                  'develop': {}, 'healthcheck': {'test': ['CMD', 'sh', '-c', 'id']}}
        for key, value in extras.items():
            with self.subTest(key=key):
                runtime = copy.deepcopy(self.runtime)
                runtime['services']['deployment-observer'][key] = value
                with self.assertRaises(ValueError):
                    self.validate(runtime)

    def test_reject_changed_required_fields(self):
        changes = {'image': 'alpine:latest', 'entrypoint': ['sh'], 'command': ['id'], 'user': '0',
                   'read_only': False, 'cap_drop': [], 'security_opt': [], 'restart': 'always',
                   'mem_limit': '67108864', 'pids_limit': 0, 'environment': {'TOKEN': 'secret'},
                   'container_name': 'duora-web', 'labels': {'traefik.enable': 'true'},
                   'networks': {'default': {'aliases': ['web']}}}
        for key, value in changes.items():
            with self.subTest(key=key):
                runtime = copy.deepcopy(self.runtime)
                runtime['services']['deployment-observer'][key] = value
                with self.assertRaises(ValueError):
                    self.validate(runtime)

    def test_reject_different_digest_and_unpinned_manifest(self):
        self.runtime['services']['deployment-observer']['image'] = self.manifest['images']['edge'][:-1] + 'b'
        with self.assertRaises(ValueError):
            self.validate()
        self.manifest['images']['edge'] = 'alpine:latest'
        self.runtime['services']['deployment-observer']['image'] = 'alpine:latest'
        with self.assertRaises(ValueError):
            self.validate()

    def test_reject_extra_services_resources_and_networks(self):
        for key in ['volumes', 'secrets', 'configs', 'include', 'x-extra']:
            with self.subTest(key=key), self.assertRaises(ValueError):
                self.validate({**self.runtime, key: {}})
        runtime = copy.deepcopy(self.runtime)
        runtime['services']['other'] = {}
        with self.assertRaises(ValueError):
            self.validate(runtime)
        runtime = copy.deepcopy(self.runtime)
        runtime['networks']['host'] = {'external': True}
        with self.assertRaises(ValueError):
            self.validate(runtime)

    def test_reviewed_coolify_metadata_only(self):
        labels = {'traefik.enable': 'false', 'coolify.applicationId': '123', 'coolify.managed': 'true'}
        self.runtime['services']['deployment-observer']['labels'] = labels
        with self.assertRaises(ValueError):
            self.validate()
        self.config['observer_labels'] = labels.copy()
        self.validate()
        self.runtime['services']['deployment-observer']['labels']['coolify.applicationId'] = '456'
        with self.assertRaises(ValueError):
            self.validate()

    def test_reviewed_external_app_network(self):
        self.runtime['networks'] = {'abc123': {'name': 'abc123', 'external': True},
                                    'duora_edge': {'name': 'duora_edge', 'external': True, 'ipam': {}}}
        self.runtime['services']['deployment-observer']['networks'] = {'abc123': None, 'duora_edge': None}
        with self.assertRaises(ValueError):
            self.validate()
        self.config['observer_networks'] = copy.deepcopy(self.runtime['networks'])
        self.validate()

    def test_exact_reviewed_environment_and_identity(self):
        name = 'deployment-observer-abc123'
        self.config['observer_container_name'] = name
        self.runtime['services']['deployment-observer']['container_name'] = name
        environment = {'DUORA_OBSERVER_IMAGE': self.manifest['images']['edge'],
                       'COOLIFY_RESOURCE_UUID': 'abc123', 'COOLIFY_CONTAINER_NAME': name,
                       'COOLIFY_BRANCH': '"release/observer"',
                       'SERVICE_NAME_DEPLOYMENT_OBSERVER': 'deployment-observer',
                       'COOLIFY_URL': '', 'COOLIFY_FQDN': '', 'SOURCE_COMMIT': 'b' * 40, 'HOST': '0.0.0.0'}
        self.runtime['services']['deployment-observer']['environment'] = environment.copy()
        with self.assertRaises(ValueError):
            self.validate()
        self.config['observer_environment'] = environment.copy()
        self.validate()
        for key, value in [('LD_PRELOAD', '/evil.so'), ('DOCKER_HOST', 'tcp://evil'),
                           ('COOLIFY_UNKNOWN', 'anything'), ('SERVICE_NAME_OTHER', 'other'),
                           ('COOLIFY_RESOURCE_UUID', 'other-app'), ('DUORA_OBSERVER_IMAGE', 'alpine:latest'),
                           ('COOLIFY_CONTAINER_NAME', 'duora-web'), ('COOLIFY_URL', 'https://public.example'),
                           ('SOURCE_COMMIT', 'short'), ('HOST', '127.0.0.1'), ('HOST', 'attacker.example'),
                           ('HOST', ''), ('COOLIFY_BRANCH', '${INJECTION}')]:
            with self.subTest(key=key):
                changed = {**environment, key: value}
                self.config['observer_environment'] = changed.copy()
                self.runtime['services']['deployment-observer']['environment'] = changed.copy()
                with self.assertRaises(ValueError):
                    self.validate()
        self.config['observer_environment'] = environment.copy()
        self.runtime['services']['deployment-observer']['environment'] = {**environment, 'COOLIFY_BRANCH': 'different'}
        with self.assertRaises(ValueError):
            self.validate()

    def test_updated_version_matches_installed_coolify_only(self):
        self.config['observer_labels'] = {'traefik.enable': 'false', 'coolify.version': '4.4.7'}
        self.runtime['services']['deployment-observer']['labels'] = {
            'traefik.enable': 'false', 'coolify.version': '4.4.8'}
        self.assertEqual(wrapper.validate_observer(self.runtime, self.config, self.manifest,
                                                  installed_version='4.4.8'), self.runtime)
        for installed in [None, '4.4.7', '5.0.0', '4.4.8-dev', 'latest']:
            with self.subTest(installed=installed), self.assertRaises(ValueError):
                wrapper.validate_observer(self.runtime, self.config, self.manifest,
                                          installed_version=installed)

    def test_coolify_downgrade_is_not_an_upgrade(self):
        self.config['observer_labels'] = {'traefik.enable': 'false', 'coolify.version': '4.4.7'}
        self.runtime['services']['deployment-observer']['labels'] = {
            'traefik.enable': 'false', 'coolify.version': '4.3.0'}
        with self.assertRaises(ValueError):
            wrapper.validate_observer(self.runtime, self.config, self.manifest,
                                      installed_version='4.3.0')

    def test_installed_version_requires_official_stable_running_image(self):
        valid = {'Config': {'Image': 'ghcr.io/coollabsio/coolify:4.4.8'},
                 'State': {'Running': True}}
        with patch.object(wrapper.subprocess, 'check_output', return_value=json.dumps([valid])):
            self.assertEqual(wrapper.installed_coolify_version(), '4.4.8')
        for image in ['ghcr.io/coollabsio/coolify:latest', 'attacker/coolify:4.4.8',
                      'ghcr.io/coollabsio/coolify:4.4.8-dev', 'ghcr.io/coollabsio/coolify:04.4.8']:
            record = {**valid, 'Config': {'Image': image}}
            with self.subTest(image=image), patch.object(wrapper.subprocess, 'check_output',
                    return_value=json.dumps([record])), self.assertRaises(ValueError):
                wrapper.installed_coolify_version()
        with patch.object(wrapper.subprocess, 'check_output', return_value=json.dumps([
                {**valid, 'State': {'Running': False}}])), self.assertRaises(ValueError):
            wrapper.installed_coolify_version()

    def test_version_update_does_not_allow_any_other_drift(self):
        labels = {'traefik.enable': 'false', 'coolify.version': '4.4.7',
                  'coolify.applicationUuid': 'abc123'}
        self.config['observer_labels'] = labels
        for key, value in [('traefik.enable', 'true'), ('coolify.applicationUuid', 'other'),
                           ('coolify.unknown', 'anything')]:
            self.runtime['services']['deployment-observer']['labels'] = {
                **labels, 'coolify.version': '4.4.8', key: value}
            with self.subTest(key=key), self.assertRaises(ValueError):
                wrapper.validate_observer(self.runtime, self.config, self.manifest,
                                          installed_version='4.4.8')

    def test_normalization_rejects_unpinned_image_before_subprocess(self):
        with patch.object(wrapper.subprocess, 'check_output') as run:
            with self.assertRaises(ValueError):
                wrapper.normalize_observer(pathlib.Path('/unused'), pathlib.Path('/unused'), 'abc123', 'alpine:latest')
            run.assert_not_called()

    def test_normalization_uses_clean_environment_and_stdin(self):
        with tempfile.TemporaryDirectory() as temp:
            observer = pathlib.Path(temp) / 'docker-compose.yaml'
            observer.write_text('services: {}')
            with patch.dict(os.environ, {'DOCKER_HOST': 'tcp://attacker', 'COMPOSE_FILE': 'evil.yml',
                                         'DUORA_TOKEN': 'secret', 'PATH': '/evil'}), \
                    patch.object(wrapper.subprocess, 'check_output', return_value=json.dumps(self.runtime).encode()) as call:
                self.assertEqual(wrapper.normalize_observer(observer, pathlib.Path(temp), 'abc123', self.manifest['images']['edge']), self.runtime)
                args, kwargs = call.call_args
                self.assertEqual(kwargs['env'], {**wrapper.SAFE_ENV, 'DUORA_OBSERVER_IMAGE': self.manifest['images']['edge']})
                self.assertEqual(kwargs['input'], b'services: {}')
                self.assertNotIn('DOCKER_HOST', kwargs['env'])
                self.assertIn('/dev/null', args[0])
                self.assertEqual(args[0][args[0].index('-f') + 1], '-')

    def test_frozen_exact_compose_and_executor_before_observer(self):
        self.validate()
        with tempfile.TemporaryDirectory() as temp:
            real_tempdir = tempfile.TemporaryDirectory
            seen = []
            def safe_tempdir(**kwargs):
                self.assertEqual(kwargs['dir'], '/run')
                return real_tempdir(prefix=kwargs['prefix'], dir=temp)
            def run(command, **kwargs):
                self.assertEqual(kwargs['env'], wrapper.SAFE_ENV)
                self.assertTrue(kwargs['check'])
                seen.append(command)
                if command[0] == '/usr/bin/docker':
                    frozen = pathlib.Path(command[command.index('-f') + 1])
                    self.assertEqual(json.loads(frozen.read_text()), self.runtime)
                    self.assertEqual(frozen.stat().st_mode & 0o777, 0o600)
                    self.assertEqual(frozen.parent.stat().st_mode & 0o777, 0o700)
                    self.assertNotEqual(frozen.name, 'docker-compose.yaml')
            with patch.object(wrapper.tempfile, 'TemporaryDirectory', side_effect=safe_tempdir), \
                    patch.object(wrapper.subprocess, 'run', side_effect=run):
                wrapper.deploy_and_start(self.runtime, self.config, pathlib.Path('/opt/release.json'))
            self.assertEqual(len(seen), 2)
            self.assertEqual(seen[0][0], '/usr/local/sbin/duora-deploy')
            self.assertNotIn('--accept-missing-backup-check', seen[0])
            self.assertEqual(list(pathlib.Path(temp).iterdir()), [])

    def test_failed_executor_never_starts_observer(self):
        with tempfile.TemporaryDirectory() as temp:
            real_tempdir = tempfile.TemporaryDirectory
            with patch.object(wrapper.tempfile, 'TemporaryDirectory',
                              side_effect=lambda **kw: real_tempdir(dir=temp)), \
                    patch.object(wrapper.subprocess, 'run', side_effect=subprocess.CalledProcessError(1, 'deploy')) as run:
                with self.assertRaises(subprocess.CalledProcessError):
                    wrapper.deploy_and_start(self.runtime, self.config, pathlib.Path('/opt/release.json'))
                self.assertEqual(run.call_count, 1)
            self.assertEqual(list(pathlib.Path(temp).iterdir()), [])


if __name__ == '__main__':
    unittest.main()
