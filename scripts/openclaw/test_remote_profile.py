import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import Mock, patch
import capacity
import collect
import monitor


class RemoteProfileTests(unittest.TestCase):
    def test_source_excludes_gateway_and_gateway_observer_excludes_product(self):
        runner = Mock(side_effect=OSError())
        value = collect.collect(runner=runner, now=100, include_gateway=False)
        self.assertNotIn('service:gateway', value['checks'])
        self.assertIn('service:postgres', value['checks'])
        self.assertNotIn('service=gateway', str(runner.call_args_list))
        runner.reset_mock()
        gateway = collect.gateway_snapshot(runner=runner, now=100)
        self.assertEqual(gateway['checks'], {'service:gateway': False})
        self.assertNotIn('service=postgres', str(runner.call_args_list))

    def test_capacity_separate_budgets_and_no_colocation_bypass(self):
        capacity.validate_memory('MemTotal: 3538944 kB\nMemAvailable: 1572864 kB\n', profile='remote-gateway')
        with self.assertRaises(ValueError):
            capacity.validate_memory('MemTotal: 7812500 kB\nMemAvailable: 7000000 kB\n', profile='source')
        capacity.validate_memory('MemTotal: 8716288 kB\nMemAvailable: 1572864 kB\n', profile='source')
        for profile in ('remote', '', 'unknown'):
            with self.assertRaises(ValueError):
                capacity.validate_memory('', profile=profile)
        with self.assertRaises(ValueError):
            capacity.no_product_containers(Mock(return_value='container-id\n'))
        capacity.no_product_containers(Mock(return_value=''))

    def test_monitor_remote_merge_does_not_rejuvenate_product_timestamp(self):
        from test_telemetry import sample
        with tempfile.TemporaryDirectory() as directory:
            product, gateway = Path(directory) / 'product', Path(directory) / 'gateway'
            product.write_text(json.dumps(sample(100)))
            gateway.write_text(json.dumps({'collected_at': 210, 'checks': {'service:gateway': True}}))
            value = monitor.remote_checks(product, gateway, 210)
            self.assertTrue(value['service:gateway'])
            self.assertTrue(value['observer:host-fresh'])
            value = monitor.remote_checks(product, gateway, 221)
            self.assertFalse(value['observer:host-fresh'])
            self.assertNotIn('service:web', value)
            self.assertTrue(value['service:gateway'])
            product.write_text(json.dumps({'collected_at': 220, 'checks': {'exec:evil': True}}))
            self.assertFalse(monitor.remote_checks(product, gateway, 221)['observer:host-fresh'])
            gateway.write_text('{}')
            self.assertFalse(monitor.remote_checks(product, gateway, 221)['service:gateway'])

    def test_remote_selector_is_explicit_and_root_owned(self):
        import stat
        from types import SimpleNamespace
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'profile.json'
            self.assertEqual(capacity.deployment_profile(path), 'colocated')
            path.write_text('{"profile":"remote-gateway"}')
            info = SimpleNamespace(st_uid=0, st_mode=stat.S_IFREG | 0o644)
            with patch.object(Path, 'lstat', return_value=info):
                self.assertEqual(capacity.deployment_profile(path), 'remote-gateway')
                path.write_text('{"profile":"source"}')
                with self.assertRaises(ValueError):
                    capacity.deployment_profile(path)
                info.st_mode = stat.S_IFREG | 0o666
                with self.assertRaises(ValueError):
                    capacity.deployment_profile(path)
