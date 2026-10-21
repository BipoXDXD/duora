"""Health metadata must remain an exact, unprivileged probe of the real runtime."""
import copy
import tempfile
import unittest
from unittest.mock import patch
import observer_start

PROBE = ("wget -q -T 5 -O /dev/null http://duora-web:8080/health/ready && "
         "wget -q -T 5 --header='Host: meetduora.com' -O /dev/null http://duora-edge-1/")
HEALTH = {'test': ['CMD-SHELL', PROBE], 'interval': '15s', 'timeout': '12s',
          'start_period': '30s', 'retries': 3}


def fixture():
    app = 'testduora'
    image = 'ghcr.io/bipoxdxd/duora-edge@sha256:' + 'a' * 64
    networks = {app: {'name': app, 'external': True, 'ipam': {}},
                'duora_edge': {'name': 'duora_edge', 'external': True, 'ipam': {}}}
    config = {'application_uuid': app, 'compose': '/approved/compose.json', 'observer_networks': networks,
              'observer_healthcheck': copy.deepcopy(HEALTH)}
    service = {'image': image, 'entrypoint': ['/bin/sh', '-c', 'exec sleep 2147483647'],
               'user': '10001:10001', 'read_only': True, 'cap_drop': ['ALL'],
               'security_opt': ['no-new-privileges:true'], 'restart': 'unless-stopped',
               'pids_limit': 16, 'mem_limit': '16777216', 'labels': {'traefik.enable': 'false'},
               'networks': {key: None for key in networks}, 'healthcheck': copy.deepcopy(HEALTH)}
    runtime = {'name': app, 'services': {'deployment-observer': service}, 'networks': copy.deepcopy(networks)}
    return runtime, config, {'images': {'edge': image}}


class ObserverHealthTests(unittest.TestCase):
    def test_publication_waits_for_real_observer_health(self):
        runtime, config, _ = fixture()
        with tempfile.TemporaryDirectory() as folder:
            with patch.object(observer_start.tempfile, 'TemporaryDirectory') as directory, \
                    patch.object(observer_start.subprocess, 'run') as run:
                directory.return_value.__enter__.return_value = folder
                observer_start.deploy_and_start(runtime, config, 'approved-manifest.json')
        command = run.call_args_list[-1].args[0]
        self.assertIn('--wait', command)
        self.assertEqual('90', command[command.index('--wait-timeout') + 1])

    def test_accepts_exact_health_probe_of_backend_and_edge(self):
        runtime, config, manifest = fixture()
        self.assertEqual(runtime, observer_start.validate_observer(runtime, config, manifest))

    def test_refuses_cosmetic_health_and_arbitrary_commands(self):
        for command in (['CMD', 'true'], ['CMD-SHELL', PROBE + '; id'], ['CMD-SHELL', 'sleep 1']):
            with self.subTest(command=command):
                runtime, config, manifest = fixture()
                runtime['services']['deployment-observer']['healthcheck']['test'] = command
                config['observer_healthcheck']['test'] = command
                with self.assertRaises(ValueError):
                    observer_start.validate_observer(runtime, config, manifest)

    def test_refuses_missing_or_disabled_health(self):
        for health in (None, {'disable': True}):
            runtime, config, manifest = fixture()
            runtime['services']['deployment-observer']['healthcheck'] = health
            with self.assertRaises(ValueError):
                observer_start.validate_observer(runtime, config, manifest)

    def test_refuses_health_without_private_network(self):
        runtime, config, manifest = fixture()
        del runtime['networks']['duora_edge']
        del config['observer_networks']['duora_edge']
        del runtime['services']['deployment-observer']['networks']['duora_edge']
        with self.assertRaises(ValueError):
            observer_start.validate_observer(runtime, config, manifest)

    def test_refuses_foreign_network_even_when_pinned(self):
        runtime, config, manifest = fixture()
        for networks in (runtime['networks'], config['observer_networks']):
            networks['duora_edge']['name'] = 'foreign_data'
        with self.assertRaises(ValueError):
            observer_start.validate_observer(runtime, config, manifest)

    def test_refuses_mounts_root_or_network_aliases(self):
        for patch in ({'volumes': ['/var/run/docker.sock:/var/run/docker.sock']},
                      {'user': '0:0'}, {'networks': {'testduora': None, 'duora_edge': {'aliases': ['web']}}}):
            runtime, config, manifest = fixture()
            runtime['services']['deployment-observer'].update(patch)
            with self.assertRaises(ValueError):
                observer_start.validate_observer(runtime, config, manifest)


if __name__ == '__main__':
    unittest.main()
