"""Pure source-plan checks, discovered by the existing ops CI job."""
import copy
import json
from pathlib import Path
import unittest
import sys

MEDIA = Path(__file__).resolve().parents[1] / "media"
FIXTURES = MEDIA / "fixtures" / "native174"
sys.path.insert(0, str(MEDIA))
import native_host_command_current as native


class CurrentNativeCommandTests(unittest.TestCase):
    def fixture(self):
        # Synthetic formatter inputs only, never observed/admitted host identities.
        nonce = 'c' * 32
        host = {'schema': 1, 'current_product_commit': native.SOURCE,
                'machine_identity_sha256': native.EXPECTED_MACHINE,
                'worker_uid': 10001, 'worker_gid': 10001, 'launcher_uid': 10002,
                'control_gid': 10002, 'control_socket': '/run/duora-qualification/source-fixture/socket',
                'journal_directory': '/var/lib/duora-qualification/source-fixture-journal',
                'kit_root': '/opt/duora-qualification/instrumentation/40321926935-1',
                'results_root': '/var/lib/duora-qualification/results/40321926935-1/' + nonce,
                'cgroup_parent': 'duora174' + nonce + '.slice',
                'receipts': {name: 'a' * 64 for name in native.RECEIPTS}}
        host['receipts']['current_preservation'] = native.PRESERVATION_REF
        return host, nonce

    def test_checked_in_inactive_binding_cannot_render_any_host_plan(self):
        path = FIXTURES / 'host-bindings.inactive.json'
        with self.assertRaises(ValueError):
            native.compose(json.loads(path.read_text()), 'c' * 32)

    def test_standalone_kit_control_and_results_are_the_only_mounts(self):
        host, nonce = self.fixture()
        before = copy.deepcopy(host)
        plan = native.compose(host, nonce)
        command = plan['docker_create_argv']
        self.assertFalse(plan['execution_authorized'])
        self.assertFalse(plan['profile_admitted'])
        self.assertFalse(plan['host_profile_or_corpus_green_claimed'])
        self.assertEqual(before, host)
        self.assertIn(native.ASSEMBLY, command)
        self.assertIn(native.CASE_CLASS, command)
        self.assertNotIn('/kit/backend/tests/Duora.IntegrationTests/bin/Debug/net10.0/Duora.IntegrationTests.dll', command)
        mounts = [command[i + 1] for i, value in enumerate(command) if value == '--mount']
        self.assertEqual(3, len(mounts))
        self.assertTrue(mounts[0].endswith(',readonly') and mounts[1].endswith(',readonly'))
        self.assertTrue(all('docker.sock' not in value and '/run/user/' not in value for value in mounts))
        self.assertEqual('none', command[command.index('--network') + 1])
        self.assertEqual('never', command[command.index('--pull') + 1])
        self.assertEqual('ALL', command[command.index('--cap-drop') + 1])
        self.assertIn('--read-only', command)
        self.assertIn('no-new-privileges:true', command)
        self.assertFalse(any('GITHUB_ACTIONS' in value for value in command))
        self.assertIn(native.CARRIER, command)

    def test_client_candidate_caps_remain_the_existing_bounds(self):
        host, nonce = self.fixture()
        command = native.compose(host, nonce)['docker_create_argv']
        for flag, value in [('--memory', '1073741824'), ('--memory-swap', '1073741824'),
                            ('--cpus', '0.5'), ('--pids-limit', '256')]:
            self.assertEqual(value, command[command.index(flag) + 1])
        self.assertEqual('duora174' + nonce + '.slice', command[command.index('--cgroup-parent') + 1])

    def test_profile_digest_is_exactly_the_existing_launcher_encoding(self):
        host, nonce = self.fixture()
        plan = native.compose(host, nonce)
        profile = plan['candidate_launcher_profile']
        producer = json.loads((FIXTURES / 'producer-profile.reference.json').read_text())
        self.assertEqual('ccb43a11141559750855656bcfa506cac7034222bd366e2d9052dc66bd641ad4',
                         native.profile_digest(producer))
        changed = copy.deepcopy(host);changed['control_gid'] += 1
        self.assertNotEqual(plan['candidate_profile_sha256'],
                            native.compose(changed, nonce)['candidate_profile_sha256'])
        self.assertEqual(native.DECODER, profile['image'])
        self.assertEqual('/run/user/10002/docker.sock', profile['runtime_socket'])
        self.assertEqual(10001, profile['worker_uid'])

    def test_unknown_values_or_another_product_or_machine_are_refused(self):
        for key, value in [('worker_uid', None), ('launcher_uid', 0), ('worker_gid', True),
                           ('machine_identity_sha256', 'b' * 64),
                           ('current_product_commit', '0' * 40)]:
            host, nonce = self.fixture();host[key] = value
            with self.subTest(key=key), self.assertRaises(ValueError):
                native.compose(host, nonce)

    def test_qualification_observation_or_returned_passage_is_not_admission(self):
        for value in native.NON_ADMISSION:
            host, nonce = self.fixture();host['receipts']['native174_admission'] = value
            with self.subTest(receipt=value), self.assertRaises(ValueError):
                native.compose(host, nonce)

    def test_mount_option_injection_runtime_socket_and_journal_exposure_are_refused(self):
        for key, value in [('kit_root', '/data/photos/40321926935-1'),
                           ('control_socket', '/run/user/10002/docker.sock'),
                           ('control_socket', '/run/duora-qualification/x,bind-propagation=rshared/socket'),
                           ('results_root', '/var/lib/duora-qualification/results/../x')]:
            host, nonce = self.fixture();host[key] = value
            with self.subTest(path=value), self.assertRaises(ValueError):
                native.compose(host, nonce)
        host, nonce = self.fixture();host['journal_directory'] = host['results_root'] + '/journal'
        with self.assertRaises(ValueError):
            native.compose(host, nonce)

    def test_input_binding_is_closed_and_nonce_is_owned(self):
        host, nonce = self.fixture();host['arbitrary_environment'] = {}
        with self.assertRaises(ValueError):
            native.compose(host, nonce)
        host, nonce = self.fixture();host['cgroup_parent'] = 'system.slice'
        with self.assertRaises(ValueError):
            native.compose(host, nonce)


if __name__ == '__main__':
    unittest.main()
