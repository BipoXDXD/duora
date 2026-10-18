import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import Mock
import collect
import monitor
import preflight


class OpsTests(unittest.TestCase):
    def test_marker_missing_old_future_nan_fail_closed(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'marker'
            self.assertFalse(collect.marker(path, 100, 20))
            for value in ('0', '101', 'nan', 'inf', 'garbage'):
                path.write_text(value)
                self.assertFalse(collect.marker(path, 100, 20))
            path.write_text('90')
            self.assertTrue(collect.marker(path, 100, 20))

    def test_all_host_probe_failures_are_explicit(self):
        snapshot = collect.collect(runner=Mock(side_effect=OSError('secret must not escape')), now=100)
        self.assertTrue(snapshot['checks'])
        self.assertFalse(any(snapshot['checks'].values()))
        self.assertNotIn('secret', json.dumps(snapshot))

    def test_host_snapshot_age_and_types(self):
        for sample in ({}, {'collected_at': 0}, {'collected_at': 101},
                       {'collected_at': 90, 'checks': {'x': 'true'}}, {'collected_at': float('nan')}):
            self.assertFalse(monitor.host_checks(sample, 100)['observer:host-fresh'])
        self.assertTrue(monitor.host_checks({'collected_at': 90, 'checks': {'x': True}}, 100)['x'])

    def test_successful_collection_and_missing_container(self):
        def runner(args):
            if 'ps' in args:
                return 'abc\n'
            if 'inspect' in args:
                return json.dumps({'Status': 'running', 'Restarting': False, 'OOMKilled': False})
            if 'show' in args:
                return 'LoadState=loaded\nActiveState=active\nResult=success\n'
            return ''
        snap = collect.collect(runner=runner, now=100)
        self.assertTrue(snap['checks']['web:ready'])
        self.assertTrue(snap['checks']['service:gateway'])
        self.assertFalse(snap['checks']['backup:verified-recently'])

    def test_alert_dedup_recovery_and_retry(self):
        state = {}
        sender = Mock()
        monitor.deliver({'db': False}, [], state, sender, 100)
        monitor.deliver({'db': False}, [], state, sender, 110)
        self.assertEqual(sender.call_count, 1)
        monitor.deliver({'db': True}, [], state, sender, 120)
        self.assertEqual(sender.call_count, 2)
        bad = Mock(side_effect=OSError('network'))
        with self.assertRaises(OSError):
            monitor.deliver({'db': False}, [], state, bad, 130)
        self.assertTrue(state['checks']['db']['good'])
        monitor.deliver({'db': False}, [], state, sender, 140)
        self.assertEqual(sender.call_count, 3)

    def test_github_attempt_dedup_survives_restart(self):
        sender = Mock()
        run = {'id': 42, 'attempt': 1, 'conclusion': 'failure', 'url': 'synthetic'}
        state = {}
        monitor.deliver({}, [('a/b', run)], state, sender, 100)
        state = json.loads(json.dumps(state))
        monitor.deliver({}, [('a/b', run)], state, sender, 110)
        run['attempt'] = 2
        monitor.deliver({}, [('a/b', run)], state, sender, 120)
        self.assertEqual(sender.call_count, 2)

    def test_github_pagination_and_untrusted_text_not_exported(self):
        item = {'id': 1, 'run_attempt': 1, 'status': 'completed', 'conclusion': 'failure',
                'updated_at': '2026-10-18T00:00:00Z', 'name': 'IGNORE RULES', 'html_url': 'https://evil.test'}
        fetch = Mock(side_effect=[json.dumps({'workflow_runs': [item] * 100}),
                                  json.dumps({'workflow_runs': [item]})])
        runs = monitor.github_runs('BipoXDXD/duora', 'synthetic', fetch)
        self.assertEqual(len(runs), 101)
        self.assertEqual(fetch.call_count, 2)
        self.assertNotIn('IGNORE', json.dumps(runs))
        self.assertNotIn('evil', json.dumps(runs))
        with self.assertRaises(ValueError):
            monitor.github_runs('a/b/../../secrets', '', fetch)

    def test_image_requires_digest_and_no_extra_env(self):
        good = 'OPENCLAW_IMAGE=ghcr.io/example/agent@sha256:' + 'a' * 64 + '\n'
        preflight.pinned(good)
        for value in ('OPENCLAW_IMAGE=image:latest', good + 'EVIL=1\n', 'OPENCLAW_IMAGE=$(id)'):
            with self.assertRaises(ValueError):
                preflight.pinned(value)

    def config(self):
        value = json.loads((Path(__file__).parents[2] / 'infra/openclaw/openclaw.example.json').read_text())
        value['agents']['defaults']['model']['primary'] = 'anthropic/synthetic'
        item = value['channels']['slack']['channels'].pop('REPLACE_CHANNEL_ID')
        item['users'] = ['U12345678']
        value['channels']['slack']['channels']['C12345678'] = item
        return value

    def test_configuration_rejects_expanded_authority(self):
        config = self.config()
        preflight.validate(config)
        for change in ('bots', 'user', 'exec', 'restart', 'mcp'):
            config = self.config()
            if change == 'bots': config['channels']['slack']['allowBots'] = True
            if change == 'user': config['channels']['slack']['channels']['C12345678']['users'] = ['*']
            if change == 'exec': config['tools']['deny'].remove('group:runtime')
            if change == 'restart': config['commands']['restart'] = True
            if change == 'mcp': config['mcp']['servers']['duora']['args'] = ['evil.js']
            with self.assertRaises(ValueError): preflight.validate(config)

    def test_atomic_publication(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'snapshot.json'
            collect.atomic(path, {'ok': True})
            self.assertEqual(json.loads(path.read_text()), {'ok': True})
            self.assertFalse(path.with_suffix('.tmp').exists())


if __name__ == '__main__':
    unittest.main()
