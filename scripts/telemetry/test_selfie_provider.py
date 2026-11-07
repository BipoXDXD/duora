"""Alerta de fornecedor da selfie: sonda sem rota nova, histerese no monitor e dead-man preservado."""
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch
import collect
import monitor
import telemetry

PROVIDER = 'selfie:provider'
PROBE = 'selfie:probe'
HEARTBEAT = 'https://heartbeat.invalid/ping'
SLACK = 'https://hooks.slack.com/services/synthetic'


def host_runner(answer: str | Exception = 'f\n', postgres_ids='pg\n'):
    def runner(args):
        if 'ps' in args:
            return postgres_ids if 'label=com.docker.compose.service=postgres' in args else 'abc\n'
        if 'inspect' in args:
            return json.dumps({'Status': 'running', 'Restarting': False, 'OOMKilled': False})
        if 'show' in args:
            return 'LoadState=loaded\nActiveState=active\nResult=success\n'
        if 'psql' in args:
            if isinstance(answer, Exception):
                raise answer
            return answer
        return ''
    return runner


class CollectorTests(unittest.TestCase):
    def test_failing_provider_is_a_false_check_and_leaves_the_edge_alone(self):
        checks = collect.collect(runner=host_runner('t\n'), now=100)['checks']
        self.assertIs(checks[PROVIDER], False)
        self.assertIs(checks[PROBE], True)
        self.assertTrue(checks['service:edge'])
        self.assertTrue(checks['web:ready'])
        self.assertTrue(checks['service:postgres'])

    def test_no_failing_attempt_is_a_true_check(self):
        checks = collect.collect(runner=host_runner('f\n'), now=100)['checks']
        self.assertIs(checks[PROVIDER], True)
        self.assertIs(checks[PROBE], True)

    def test_failed_probe_is_no_evidence_about_the_provider(self):
        for answer in (subprocess.TimeoutExpired('psql', 8), subprocess.CalledProcessError(2, 'psql'),
                       OSError('docker'), '', 'tf\n', 'ERROR: CANARY-probe\n', 't\nf\n'):
            with self.subTest(answer=answer):
                checks = collect.collect(runner=host_runner(answer), now=100)['checks']
                self.assertIs(checks[PROBE], False)
                self.assertNotIn(PROVIDER, checks)
                self.assertTrue(checks['service:edge'])
                self.assertTrue(checks['service:postgres'])
                self.assertNotIn('CANARY', json.dumps(checks))

    def test_missing_or_duplicate_postgres_container_fails_the_probe_without_querying(self):
        for ids in ('', 'one\ntwo\n'):
            with self.subTest(ids=ids):
                seen = []
                runner = host_runner('t\n', postgres_ids=ids)

                def spy(args, inner=runner, log=seen):
                    log.append(args)
                    return inner(args)
                checks = collect.collect(runner=spy, now=100)['checks']
                self.assertIs(checks[PROBE], False)
                self.assertNotIn(PROVIDER, checks)
                self.assertFalse([args for args in seen if 'psql' in args])

    def test_query_runs_inside_the_container_read_only_and_bounded(self):
        seen = []
        runner = host_runner()

        def spy(args):
            seen.append(args)
            return runner(args)
        collect.collect(runner=spy, now=100)
        query = next(args for args in seen if 'psql' in args)
        self.assertEqual(query[:4], ['/usr/bin/docker', 'exec', 'pg', 'psql'])
        conninfo = query[query.index('--dbname') + 1]
        for setting in ('dbname=duora', 'user=postgres', 'connect_timeout=', 'default_transaction_read_only=on',
                        'statement_timeout=', 'lock_timeout='):
            self.assertIn(setting, conninfo)
        self.assertEqual(query[query.index('--command') + 1], collect.SELFIE_PROVIDER_QUERY)

    def test_query_never_reads_who_the_attempt_belongs_to(self):
        for column in ('recipient_key', 'selfie_verifications', 'identity.', 'payload'):
            self.assertNotIn(column, collect.SELFIE_PROVIDER_QUERY)

    def test_gateway_snapshot_stays_without_the_product_probe(self):
        snapshot = collect.gateway_snapshot(runner=host_runner('t\n'), now=100)
        self.assertEqual(set(snapshot['checks']), {'service:gateway'})


class RemoteContractTests(unittest.TestCase):
    def sample(self, **extra):
        return {'collected_at': 100, 'checks': {**dict.fromkeys(telemetry.CHECKS, True), **extra}}

    def test_receiver_and_sender_of_different_versions_do_not_reject_each_other(self):
        for extra in ({}, {PROBE: False}, {PROVIDER: False, PROBE: True}):
            with self.subTest(extra=extra):
                value = self.sample(**extra)
                self.assertEqual(telemetry.snapshot(value, 110)['checks'], value['checks'])

    def test_only_the_selfie_checks_are_optional(self):
        missing = self.sample()
        del missing['checks']['web:ready']
        for value in (missing, self.sample(**{'selfie:other': True})):
            with self.subTest(value=sorted(value['checks'])[:1]), self.assertRaises(ValueError):
                telemetry.snapshot(value, 110)

    def test_selfie_checks_are_still_strict_booleans(self):
        for key in (PROVIDER, PROBE):
            for bad in (1, 'false', None):
                with self.subTest(key=key, bad=bad), self.assertRaises(ValueError):
                    telemetry.snapshot(self.sample(**{key: bad}), 110)

    def test_source_collection_fits_the_contract(self):
        value = collect.collect(runner=host_runner('t\n'), now=100, include_gateway=False)
        value['checks']['backup:verified-recently'] = True
        self.assertIs(telemetry.snapshot(value, 110)['checks'][PROVIDER], False)


class HysteresisTests(unittest.TestCase):
    def setUp(self):
        self.state = {}

    def alert(self, failing, at):
        return not monitor.gate_selfie_provider({PROVIDER: not failing}, self.state, at)[PROVIDER]

    def test_one_failed_sample_does_not_open(self):
        self.assertFalse(self.alert(True, 1000))
        self.assertFalse(self.alert(False, 1015))

    def test_opens_only_after_two_minutes_of_failure(self):
        for at in range(1000, 1120, 15):
            self.assertFalse(self.alert(True, at))
        self.assertFalse(self.alert(True, 1119))
        self.assertTrue(self.alert(True, 1120))

    def test_a_short_gap_does_not_restart_the_count(self):
        self.alert(True, 1000)
        self.assertFalse(self.alert(False, 1030))
        self.assertFalse(self.alert(True, 1060))
        self.assertTrue(self.alert(True, 1120))

    def test_a_long_gap_restarts_the_count(self):
        self.alert(True, 1000)
        self.assertFalse(self.alert(False, 1040))
        self.assertFalse(self.alert(True, 1061))
        self.assertFalse(self.alert(True, 1120))
        self.assertFalse(self.alert(True, 1180))
        self.assertTrue(self.alert(True, 1181))

    def test_stays_open_while_no_attempt_proves_recovery(self):
        for at in range(1000, 1135, 15):
            self.alert(True, at)
        last = 1120
        self.assertTrue(self.alert(False, last + 899))

    def test_closes_after_fifteen_minutes_without_failure(self):
        for at in (1000, 1060, 1120):
            self.alert(True, at)
        self.assertFalse(self.alert(False, 1120 + 900))

    def test_a_new_failure_postpones_the_recovery(self):
        for at in (1000, 1060, 1120):
            self.alert(True, at)
        self.assertTrue(self.alert(True, 1500))
        self.assertTrue(self.alert(False, 1500 + 899))
        self.assertFalse(self.alert(False, 1500 + 900))

    def test_after_closing_it_needs_two_more_minutes_to_reopen(self):
        for at in (1000, 1060, 1120):
            self.alert(True, at)
        self.alert(False, 2020)
        self.assertFalse(self.alert(True, 2021))
        self.assertFalse(self.alert(True, 2081))
        self.assertFalse(self.alert(True, 2140))
        self.assertTrue(self.alert(True, 2141))

    def test_absent_check_is_no_evidence_and_is_not_invented(self):
        self.assertEqual(monitor.gate_selfie_provider({'service:web': True}, self.state, 1000), {'service:web': True})
        self.assertEqual(self.state, {})
        for at in (1000, 1060, 1120):
            self.alert(True, at)
        checks = monitor.gate_selfie_provider({'observer:host-fresh': False}, self.state, 5000)
        self.assertEqual(checks, {'observer:host-fresh': False})
        self.assertTrue(self.alert(True, 5015))

    def test_other_checks_pass_through_untouched(self):
        checks = {PROVIDER: True, 'service:web': False}
        self.assertEqual(monitor.gate_selfie_provider(checks, self.state, 1000), checks)

    def test_memory_survives_the_state_file(self):
        self.alert(True, 1000)
        self.alert(True, 1060)
        self.state = json.loads(json.dumps(self.state))
        self.assertTrue(self.alert(True, 1120))


class MessageTests(unittest.TestCase):
    def test_alert_says_what_is_wrong_and_where_to_look(self):
        sent = []
        monitor.deliver({PROVIDER: False}, [], {}, sent.append, 100)
        self.assertEqual(len(sent), 1)
        self.assertTrue(sent[0].startswith(f'Duora ALERTA: {PROVIDER}'))
        self.assertIn('docs/operations/telemetry.md', sent[0])

    def test_recovery_does_not_promise_more_than_it_saw(self):
        sent = []
        state = {}
        monitor.deliver({PROVIDER: False}, [], state, sent.append, 100)
        monitor.deliver({PROVIDER: True}, [], state, sent.append, 200)
        self.assertTrue(sent[1].startswith(f'Duora RECUPERADO: {PROVIDER}'))
        self.assertIn('15 min', sent[1])

    def test_other_checks_keep_the_existing_text(self):
        sent = []
        monitor.deliver({'service:web': False}, [], {}, sent.append, 100)
        self.assertEqual(sent, ['Duora ALERTA: service:web'])

    def test_probe_alert_is_not_confused_with_the_provider(self):
        sent = []
        monitor.deliver({PROBE: False}, [], {}, sent.append, 100)
        self.assertTrue(sent[0].startswith(f'Duora ALERTA: {PROBE}'))
        self.assertIn('docs/operations/telemetry.md', sent[0])


class Stop(Exception):
    pass


class MonitorLoopTests(unittest.TestCase):
    """Roda o `main` real, com relógio, disco e rede falsos: é o laço que decide o heartbeat."""

    def setUp(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.root = Path(directory.name)
        self.now = 1_000_000.0
        self.calls = []
        self.write('/etc/duora-observer/monitor.json', json.dumps(
            {'repositories': ['BipoXDXD/duora'], 'heartbeat_url': HEARTBEAT}))
        self.write('/creds/github-token', 'synthetic')
        self.write('/creds/slack-webhook', SLACK)

    def mapped(self, path):
        return Path(path) if str(path).startswith(str(self.root)) else self.root / str(path).lstrip('/')

    def write(self, path, text):
        target = self.mapped(path)
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(text)

    def collected(self, checks):
        if checks is None:
            self.mapped('/var/lib/duora-observer/host.json').unlink(missing_ok=True)
        else:
            self.write('/var/lib/duora-observer/host.json', json.dumps({'collected_at': self.now, 'checks': checks}))

    def request(self, url, token=None, payload=None):
        self.calls.append((url, payload))
        return b'ok' if payload is not None else b'{"workflow_runs": []}'

    def run_loop(self, samples):
        """Cada amostra é um ciclo de 15 s do monitor; None é o coletor sem snapshot."""
        pending = iter(samples)
        self.collected(next(pending))

        def sleep(seconds):
            self.now += seconds
            try:
                self.collected(next(pending))
            except StopIteration:
                raise Stop from None

        def clock():
            return self.now

        def write_state(path, value, mode=0o644):
            target = self.mapped(path)
            target.parent.mkdir(parents=True, exist_ok=True)
            collect.atomic(target, value, mode)

        with (patch.object(monitor, 'Path', self.mapped), patch.object(monitor, 'request', self.request),
              patch.object(monitor, 'github_runs', lambda repo, token: []),
              patch.object(monitor, 'atomic', write_state), patch.object(monitor.time, 'time', clock),
              patch.object(monitor.time, 'sleep', sleep),
              patch.dict(monitor.os.environ, {'CREDENTIALS_DIRECTORY': '/creds'}, clear=False)):
            monitor.os.environ.pop('DUORA_MONITOR_PROFILE', None)
            with self.assertRaises(Stop):
                monitor.main()

    def heartbeats(self):
        return len([url for url, _ in self.calls if url == HEARTBEAT])

    def slack(self):
        return [payload['text'] for url, payload in self.calls if url == SLACK]

    def test_provider_outage_does_not_silence_the_heartbeat_and_alerts_once(self):
        self.run_loop([{'service:web': True, PROVIDER: False, PROBE: True}] * 21)
        self.assertEqual(self.heartbeats(), 21)
        self.assertEqual(len([text for text in self.slack() if PROVIDER in text]), 1)

    def test_short_outage_never_reaches_slack(self):
        good = {'service:web': True, PROVIDER: True, PROBE: True}
        self.run_loop([good, {**good, PROVIDER: False}, {**good, PROVIDER: False}, good, good])
        self.assertEqual(self.slack(), [])
        self.assertEqual(self.heartbeats(), 5)

    def test_real_failures_still_silence_the_heartbeat(self):
        for bad in ({'service:web': False, PROVIDER: True, PROBE: True},
                    {'service:web': True, PROVIDER: True, PROBE: False}):
            with self.subTest(bad=bad):
                self.calls.clear()
                self.run_loop([bad] * 3)
                self.assertEqual(self.heartbeats(), 0)

    def test_lost_snapshot_silences_the_heartbeat_even_mid_outage(self):
        bad = {'service:web': True, PROVIDER: False, PROBE: True}
        self.run_loop([bad, bad, None, None])
        self.assertEqual(self.heartbeats(), 2)

    def test_alert_state_survives_a_monitor_restart(self):
        bad = {'service:web': True, PROVIDER: False, PROBE: True}
        self.run_loop([bad] * 10)
        self.run_loop([bad] * 10)
        self.assertEqual(len([text for text in self.slack() if PROVIDER in text]), 1)


if __name__ == '__main__':
    unittest.main()
