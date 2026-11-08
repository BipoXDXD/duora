"""Alerta de fornecedor da selfie: sonda, histerese, evento no outbox e estado próprio."""
import json
import os
from pathlib import Path
import stat
import subprocess
import tempfile
import unittest
from unittest.mock import patch

import selfie_provider_alert as alert

ROOT = Path(__file__).resolve().parents[2]
DOWN = 'selfie_provider_down'
RECOVERED = 'selfie_provider_recovered'
BLIND = 'selfie_probe_blind'
RESTORED = 'selfie_probe_restored'
STALLED = 'selfie_queue_stalled'
RESUMED = 'selfie_queue_resumed'
CONNECTION = ("dbname=duora user=postgres connect_timeout=3 application_name=duora-selfie-alert "
              "options='-c default_transaction_read_only=on -c statement_timeout=3000 -c lock_timeout=1000'")
TEMPORARY_ERROR = subprocess.TimeoutExpired('psql', 8)


def host(answer: str | Exception = 'f|f\n', ids='pg\n'):
    def runner(args):
        if 'ps' in args:
            return ids
        if isinstance(answer, Exception):
            raise answer
        return answer
    return runner


class ProbeTests(unittest.TestCase):
    def test_yes_and_no_are_the_only_readings(self):
        self.assertEqual(alert.read_queue(host('t|f\n')), (True, False))
        self.assertEqual(alert.read_queue(host('f|f\n')), (False, False))
        self.assertEqual(alert.read_queue(host('f|t\n')), (False, True))
        self.assertEqual(alert.read_queue(host('t|t\n')), (True, True))

    def test_failed_probe_is_no_evidence_and_keeps_the_text_out_of_the_log(self):
        for answer in (TEMPORARY_ERROR, subprocess.CalledProcessError(2, 'psql'), OSError('docker'),
                       '', 'tf\n', 'ERROR: CANARY-probe\n', 't\nf\n', 't\n', 'f\n', 't|x\n', 't|f|t\n'):
            with self.subTest(answer=answer), self.assertLogs('duora-selfie-alert', 'ERROR') as logs:
                self.assertIsNone(alert.read_queue(host(answer)))
                self.assertEqual(logs.output, ['ERROR:duora-selfie-alert:selfie_probe_failed'])

    def test_missing_or_duplicate_postgres_container_never_queries(self):
        for ids in ('', 'one\ntwo\n'):
            calls = []

            def spy(args, inner=host('t|f\n', ids), log=calls):
                log.append(args)
                return inner(args)
            with self.subTest(ids=ids), self.assertLogs('duora-selfie-alert', 'ERROR'):
                self.assertIsNone(alert.read_queue(spy))
                self.assertEqual(len(calls), 1)

    def test_argv_connection_and_filters_are_exactly_these(self):
        calls = []

        def spy(args):
            calls.append(args)
            return host()(args)
        alert.read_queue(spy)
        self.assertEqual(alert.CONNECTION, CONNECTION)
        self.assertEqual(calls, [
            ['/usr/bin/docker', 'ps', '-aq', '--filter', 'label=com.docker.compose.project=duora',
             '--filter', 'label=com.docker.compose.service=postgres'],
            ['/usr/bin/docker', 'exec', 'pg', 'psql', '--no-psqlrc', '--no-password', '--quiet', '--tuples-only',
             '--no-align', '--dbname', CONNECTION, '--command', alert.QUEUE_QUERY]])

    def test_query_never_reads_who_the_attempt_belongs_to(self):
        for column in ('recipient_key', 'selfie_verifications', 'identity.', 'payload'):
            self.assertNotIn(column, alert.QUEUE_QUERY)


class RunnerTests(unittest.TestCase):
    def test_each_command_is_bounded_without_inheriting_the_environment(self):
        answer = subprocess.CompletedProcess([], 0, stdout='t\n')
        with patch.object(alert.subprocess, 'run', return_value=answer) as run:
            self.assertEqual(alert.run(['/usr/bin/docker', 'ps']), 't\n')
        run.assert_called_once_with(['/usr/bin/docker', 'ps'], text=True, capture_output=True, timeout=8, check=True,
                                    env={'PATH': '/usr/sbin:/usr/bin:/sbin:/bin', 'LC_ALL': 'C'})


class HysteresisTests(unittest.TestCase):
    def setUp(self):
        self.memory = alert.fresh_memory()
        self.seen = []

    def step(self, failing, at):
        self.memory, events = alert.advance(self.memory, failing, at)
        self.seen += [name for name, _ in events]
        return [name for name, _ in events]

    def opened(self):
        for at in (1000, 1060, 1120):
            self.step(True, at)
        self.assertEqual(self.seen, [DOWN])

    def test_one_failed_sample_does_not_open(self):
        self.assertEqual(self.step(True, 1000), [])
        self.assertEqual(self.step(False, 1030), [])

    def test_opens_only_after_two_minutes_of_failure(self):
        for at in (1000, 1030, 1060, 1090, 1119):
            self.assertEqual(self.step(True, at), [])
        self.assertEqual(self.step(True, 1120), [DOWN])

    def test_opens_once(self):
        self.opened()
        self.assertEqual(self.step(True, 1150), [])

    def test_real_timer_intervals_do_not_restart_the_count(self):
        for gap in (30.4, 47):
            with self.subTest(gap=gap):
                self.memory = alert.fresh_memory()
                self.seen = []
                at = 1000.0
                while not self.seen:
                    self.step(True, at)
                    at += gap
                self.assertLessEqual(at - gap - 1000, 120 + gap)

    def test_alternating_failures_still_open(self):
        self.step(True, 1000)
        self.step(False, 1047)
        self.step(True, 1094)
        self.step(False, 1141)
        self.assertEqual(self.step(True, 1188), [DOWN])

    def test_a_reading_lost_mid_outage_does_not_restart_the_count(self):
        self.step(True, 1000)
        self.step(None, 1047)
        self.assertEqual(self.step(True, 1094), [])
        self.step(None, 1141)
        self.assertEqual(self.step(True, 1188), [DOWN])

    def test_a_gap_of_exactly_the_limit_keeps_the_count(self):
        self.step(True, 1000)
        self.assertEqual(self.step(True, 1100), [])
        self.assertEqual(self.step(True, 1120), [DOWN])

    def test_a_longer_gap_restarts_the_count(self):
        self.step(True, 1000)
        self.step(True, 1101)
        self.assertEqual(self.step(True, 1200), [])
        self.assertEqual(self.step(True, 1221), [DOWN])

    def test_stays_open_until_fifteen_minutes_without_failure(self):
        self.opened()
        self.assertEqual(self.step(False, 1120 + 899), [])
        self.assertEqual(self.step(False, 1120 + 900), [RECOVERED])
        self.assertEqual(self.step(False, 1120 + 930), [])

    def test_a_new_failure_postpones_the_recovery(self):
        self.opened()
        self.step(True, 1500)
        self.assertEqual(self.step(False, 1500 + 899), [])
        self.assertEqual(self.step(False, 1500 + 900), [RECOVERED])

    def test_after_closing_it_needs_two_more_minutes_to_reopen(self):
        self.opened()
        self.step(False, 2020)
        self.step(True, 2021)
        self.assertEqual(self.step(True, 2100), [])
        self.assertEqual(self.step(True, 2140), [])
        self.assertEqual(self.step(True, 2141), [DOWN])

    def test_a_broken_probe_neither_opens_nor_closes(self):
        self.step(True, 1000)
        for at in (1030, 1200, 1500):
            self.assertEqual(self.step(None, at), [])
        self.assertFalse(self.memory['open'])
        self.memory = alert.fresh_memory()
        self.seen = []
        self.opened()
        for at in (2100, 2130):
            self.assertEqual(self.step(None, at), [])
        self.assertTrue(self.memory['open'])

    def test_blindness_is_announced_once_after_ten_minutes(self):
        self.step(None, 1000)
        self.assertEqual(self.step(None, 1599), [])
        self.assertEqual(self.step(None, 1600), [BLIND])
        self.assertEqual(self.step(None, 1630), [])

    def test_a_short_blindness_is_forgotten_silently(self):
        self.step(None, 1000)
        self.step(False, 1300)
        self.assertEqual(self.step(None, 1330), [])
        self.assertEqual(self.step(None, 1929), [])
        self.assertEqual(self.step(None, 1930), [BLIND])

    def test_the_probe_coming_back_is_announced_and_counts_as_a_reading(self):
        self.step(None, 1000)
        self.step(None, 1600)
        self.assertEqual(self.step(False, 1630), [RESTORED])
        self.assertFalse(self.memory['blind'])

    def test_recovery_after_blindness_is_anchored_on_the_last_failure_seen(self):
        self.opened()
        self.step(None, 1200)
        self.step(None, 1800)
        self.memory, events = alert.advance(self.memory, False, 2100)
        self.assertEqual(events, [(RESTORED, 1200), (RECOVERED, 1120)])

    def test_the_same_transition_keeps_its_anchor_on_a_rerun(self):
        for at in (1000, 1030, 1060, 1090):
            self.step(True, at)
        _, first = alert.advance(self.memory, True, 1120)
        _, second = alert.advance(self.memory, True, 1150)
        self.assertEqual(first, second)


class MalformedMemoryTests(unittest.TestCase):
    GOOD = {'since': None, 'last': None, 'open': False, 'blind_since': None, 'blind': False, 'stall_since': None,
            'stall_open': False}
    MALFORMED = ('texto', 7, [], None, {}, {'since': None, 'last': None},
                 {**GOOD, 'open': 'sim'}, {**GOOD, 'blind': 1}, {**GOOD, 'since': 'a', 'last': 1},
                 {**GOOD, 'since': 1, 'last': 'b'}, {**GOOD, 'since': True, 'last': 1},
                 {**GOOD, 'since': None, 'last': 1}, {**GOOD, 'since': 1, 'last': None}, {**GOOD, 'open': True},
                 {**GOOD, 'since': 1e400, 'last': 1, 'open': True}, {**GOOD, 'blind': True}, {**GOOD, 'x': 1},
                 {**GOOD, 'stall_open': 1}, {**GOOD, 'stall_since': 'a'}, {**GOOD, 'stall_since': True},
                 {**GOOD, 'stall_since': 1e400, 'stall_open': True}, {**GOOD, 'stall_open': True})

    def test_invalid_memory_counts_as_absent_with_a_log(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'state.json'
            for memory in self.MALFORMED:
                with self.subTest(memory=memory), self.assertLogs('duora-selfie-alert', 'ERROR') as logs:
                    path.write_text(json.dumps(memory))
                    self.assertEqual(alert.load_memory(path), alert.fresh_memory())
                    self.assertEqual(logs.output, ['ERROR:duora-selfie-alert:selfie_alert_state_discarded'])

    def test_unreadable_json_counts_as_absent_with_a_log(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'state.json'
            path.write_text('{')
            with self.assertLogs('duora-selfie-alert', 'ERROR'):
                self.assertEqual(alert.load_memory(path), alert.fresh_memory())

    def test_missing_file_is_a_first_run_without_a_log(self):
        with tempfile.TemporaryDirectory() as directory, self.assertNoLogs('duora-selfie-alert', 'ERROR'):
            self.assertEqual(alert.load_memory(Path(directory) / 'state.json'), alert.fresh_memory())

    def test_valid_memory_is_kept_without_a_log(self):
        memory = {'since': 880, 'last': 1000, 'open': True, 'blind_since': 1000, 'blind': True, 'stall_since': 900,
                  'stall_open': True}
        with tempfile.TemporaryDirectory() as directory, self.assertNoLogs('duora-selfie-alert', 'ERROR'):
            path = Path(directory) / 'state.json'
            path.write_text(json.dumps(memory))
            self.assertEqual(alert.load_memory(path), memory)


class StalledQueueTests(unittest.TestCase):
    def setUp(self):
        self.memory = alert.fresh_memory()

    def step(self, stalled, at):
        self.memory, events = alert.advance_stall(self.memory, stalled, at)
        return [name for name, _ in events]

    def test_opens_only_after_two_minutes_of_stalled_readings(self):
        self.assertEqual(self.step(True, 1000), [])
        self.assertEqual(self.step(True, 1119), [])
        self.assertEqual(self.step(True, 1120), [STALLED])

    def test_opens_once(self):
        for at in (1000, 1120):
            self.step(True, at)
        self.assertEqual(self.step(True, 1240), [])

    def test_one_healthy_reading_restarts_the_count(self):
        self.step(True, 1000)
        self.step(False, 1060)
        self.assertEqual(self.step(True, 1120), [])
        self.assertEqual(self.step(True, 1239), [])
        self.assertEqual(self.step(True, 1240), [STALLED])

    def test_a_broken_probe_neither_restarts_the_count_nor_closes(self):
        self.step(True, 1000)
        self.assertEqual(self.step(None, 1060), [])
        self.assertEqual(self.step(True, 1120), [STALLED])
        self.assertEqual(self.step(None, 1180), [])
        self.assertTrue(self.memory['stall_open'])

    def test_the_first_healthy_reading_closes_with_the_same_anchor(self):
        self.step(True, 1000)
        self.step(True, 1120)
        self.memory, events = alert.advance_stall(self.memory, False, 1150)
        self.assertEqual(events, [(RESUMED, 1000.0)])
        self.assertEqual(self.memory, alert.fresh_memory())

    def test_nothing_is_said_while_the_queue_is_healthy(self):
        self.assertEqual(self.step(False, 1000), [])
        self.assertEqual(self.memory, alert.fresh_memory())


class OutboxTests(unittest.TestCase):
    """Lê o outbox como o coordenador descreveu o mail.py, que não está neste repositório."""

    def setUp(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.root = Path(directory.name)
        self.outbox = self.root / 'outbox'
        self.outbox.mkdir()
        self.state = self.root / 'state.json'

    def pending(self):
        events = [json.loads(path.read_text()) for path in sorted(self.outbox.glob('*.json'))]
        return [event for event in events if event.get('delivery') != 'smtp_accepted']

    def cycle(self, answer, now):
        return alert.run_once(host(answer), self.state, self.outbox, now)

    def open_alert(self):
        for now in (1000, 1060, 1120):
            self.cycle('t|f\n', now)

    def test_nothing_is_written_while_there_is_nothing_to_say(self):
        for now in (1000, 1030):
            self.assertEqual(self.cycle('f|f\n', now), 0)
        self.assertEqual(list(self.outbox.iterdir()), [])

    def almost_open(self):
        for now in (1000, 1030, 1060, 1090):
            self.cycle('t|f\n', now)

    def test_opening_writes_one_event_the_mail_sender_can_use(self):
        self.open_alert()
        self.cycle('t|f\n', 1150)
        event = self.pending()
        self.assertEqual(len(event), 1)
        self.assertEqual(set(event[0]), {'event', 'reason', 'deployment_uuid', 'observed_at'})
        self.assertEqual(event[0]['deployment_uuid'], f'selfie-provider-{DOWN}-1000')
        self.assertEqual(event[0]['event'], DOWN)
        self.assertEqual(event[0]['observed_at'], '1970-01-01T00:18:40+00:00')
        self.assertIn('NAO E DEPLOY', event[0]['reason'])
        self.assertIn('docs/operations/selfie-provider-alert.md', event[0]['reason'])

    def test_stalled_queue_writes_its_own_event_beside_the_provider_alert(self):
        for now in (1000, 1060, 1120):
            self.assertEqual(self.cycle('t|t\n', now), 0)
        self.assertEqual(sorted(event['event'] for event in self.pending()), [DOWN, STALLED])
        self.cycle('f|f\n', 1150)
        self.assertEqual(sorted(event['event'] for event in self.pending()), [DOWN, RESUMED, STALLED])

    def test_a_stalled_queue_alone_does_not_touch_the_provider_alert(self):
        for now in (1000, 1060, 1120):
            self.cycle('f|t\n', now)
        self.assertEqual([event['event'] for event in self.pending()], [STALLED])

    def stall_almost_open(self):
        for now in (1000, 1060):
            self.cycle('f|t\n', now)

    def test_a_broken_probe_does_not_close_an_open_stall(self):
        self.stall_almost_open()
        self.cycle('f|t\n', 1120)
        self.assertEqual(self.cycle(TEMPORARY_ERROR, 1150), 1)
        self.assertEqual([event['event'] for event in self.pending()], [STALLED])
        self.assertTrue(json.loads(self.state.read_text())['stall_open'])

    def test_stall_publication_failure_keeps_the_transition_for_the_next_run(self):
        self.stall_almost_open()
        with patch.object(alert.os, 'link', side_effect=OSError('disk')), self.assertRaises(OSError):
            self.cycle('f|t\n', 1120)
        self.assertEqual(list(self.outbox.iterdir()), [])
        self.cycle('f|t\n', 1150)
        self.assertEqual([event['event'] for event in self.pending()], [STALLED])

    def test_crash_after_publishing_the_stall_does_not_repeat_it(self):
        self.stall_almost_open()
        with patch.object(alert, 'save_memory', side_effect=OSError('disk')), self.assertRaises(OSError):
            self.cycle('f|t\n', 1120)
        self.cycle('f|t\n', 1150)
        events = self.pending()
        self.assertEqual(len(events), 1)
        self.assertEqual(events[0]['deployment_uuid'], f'selfie-provider-{STALLED}-1000')

    def test_every_reason_says_it_is_not_a_deploy_and_points_to_the_guide(self):
        self.assertEqual(set(alert.REASONS), {DOWN, RECOVERED, BLIND, RESTORED, STALLED, RESUMED})
        for reason in alert.REASONS.values():
            self.assertIn('Coolify', reason)
            self.assertTrue((ROOT / reason.rsplit(' ', 1)[1]).is_file())
            self.assertTrue(reason.isascii())

    def test_recovery_does_not_claim_a_quiet_period_it_did_not_watch(self):
        self.assertNotIn('Nenhuma falha', alert.REASONS[RECOVERED])
        self.assertIn('leituras podem ter parado', alert.REASONS[RECOVERED])

    def test_event_is_private_and_reaches_the_disk_before_it_is_visible(self):
        self.almost_open()
        synced = []
        real_fsync, real_link = alert.os.fsync, alert.os.link

        def fsync(fd):
            synced.append('directory' if stat.S_ISDIR(os.fstat(fd).st_mode) else 'file')
            real_fsync(fd)

        def link(source, target):
            synced.append('link')
            real_link(source, target)
        with patch.object(alert.os, 'fsync', fsync), patch.object(alert.os, 'link', link):
            self.cycle('t|f\n', 1120)
        self.assertEqual(synced, ['file', 'link', 'directory'])
        self.assertEqual(stat.S_IMODE(next(self.outbox.glob('*.json')).stat().st_mode), 0o600)

    def test_a_temporary_file_already_there_is_never_reused(self):
        stray = self.outbox / f'.selfie-provider-{DOWN}-1000.{os.getpid()}.tmp'
        stray.write_text('alheio')
        with self.assertRaises(FileExistsError):
            alert.emit(self.outbox, DOWN, 1000, 1120)
        self.assertEqual(stray.read_text(), 'alheio')
        self.assertEqual(self.pending(), [])

    def test_recovery_writes_a_second_event(self):
        self.open_alert()
        self.cycle('f|f\n', 1120 + 900)
        self.assertEqual([event['event'] for event in self.pending()], [DOWN, RECOVERED])

    def test_failed_probe_fails_the_unit_and_writes_nothing_until_blind(self):
        self.assertEqual(self.cycle(TEMPORARY_ERROR, 1000), 1)
        self.assertEqual(self.pending(), [])
        self.assertEqual(self.cycle(TEMPORARY_ERROR, 1600), 1)
        self.assertEqual([event['event'] for event in self.pending()], [BLIND])

    def test_missing_outbox_fails_the_unit_even_with_nothing_to_say(self):
        self.outbox.rmdir()
        with self.assertLogs('duora-selfie-alert', 'ERROR') as logs:
            self.assertEqual(self.cycle('f|f\n', 1000), 1)
        self.assertEqual(logs.output, ['ERROR:duora-selfie-alert:selfie_alert_outbox_missing'])
        self.assertTrue(self.state.exists())

    def test_memory_survives_between_runs(self):
        self.almost_open()
        self.assertEqual(self.pending(), [])
        self.cycle('t|f\n', 1120)
        self.assertEqual(len(self.pending()), 1)

    def test_no_temporary_file_is_left_or_visible_as_an_event(self):
        self.open_alert()
        self.assertEqual([path.suffix for path in self.outbox.iterdir()], ['.json'])

    def test_mail_sender_never_sees_an_event_before_it_is_complete(self):
        real_link = alert.os.link
        visible = []

        def link(source, target):
            visible.extend(self.outbox.glob('*.json'))
            real_link(source, target)
        self.almost_open()
        with patch.object(alert.os, 'link', link):
            self.cycle('t|f\n', 1120)
        self.assertEqual(visible, [])
        self.assertEqual(len(self.pending()), 1)

    def test_publication_failure_keeps_the_transition_for_the_next_run(self):
        self.almost_open()
        with patch.object(alert.os, 'link', side_effect=OSError('disk')), self.assertRaises(OSError):
            self.cycle('t|f\n', 1120)
        self.assertEqual(list(self.outbox.iterdir()), [])
        self.cycle('t|f\n', 1150)
        self.assertEqual([event['event'] for event in self.pending()], [DOWN])

    def test_publication_failure_at_the_worst_interval_is_retried_by_the_next_run(self):
        for now in (1000, 1047, 1094):
            self.cycle('t|f\n', now)
        with patch.object(alert.os, 'link', side_effect=OSError('disk')), self.assertRaises(OSError):
            self.cycle('t|f\n', 1141)
        self.cycle('t|f\n', 1188)
        self.assertEqual([event['event'] for event in self.pending()], [DOWN])

    def test_crash_after_publishing_does_not_repeat_the_event(self):
        self.almost_open()
        with patch.object(alert, 'save_memory', side_effect=OSError('disk')), self.assertRaises(OSError):
            self.cycle('t|f\n', 1120)
        self.cycle('t|f\n', 1150)
        self.assertEqual(len(self.pending()), 1)

    def test_an_event_already_sent_is_never_overwritten(self):
        self.almost_open()
        with patch.object(alert, 'save_memory', side_effect=OSError('disk')), self.assertRaises(OSError):
            self.cycle('t|f\n', 1120)
        path = next(self.outbox.glob('*.json'))
        path.write_text(json.dumps({**json.loads(path.read_text()), 'delivery': 'smtp_accepted'}))
        self.cycle('t|f\n', 1150)
        self.assertEqual(self.pending(), [])

    def test_a_malformed_state_file_does_not_stop_the_run(self):
        self.state.write_text('{"open": true}')
        with self.assertLogs('duora-selfie-alert', 'ERROR'):
            self.assertEqual(self.cycle('f|f\n', 1000), 0)
        self.assertEqual(json.loads(self.state.read_text()), alert.fresh_memory())

    def test_main_never_prints_exception_text(self):
        with (patch.object(alert, 'run_once', side_effect=OSError('CANARY-texto')),
              self.assertLogs('duora-selfie-alert', 'ERROR') as logs):
            self.assertEqual(alert.main(), 1)
        self.assertEqual(logs.output, ['ERROR:duora-selfie-alert:selfie_alert_run_failed cause=OSError'])


class UnitTests(unittest.TestCase):
    def test_timer_counts_from_the_end_of_the_run(self):
        timer = (ROOT / 'infra/selfie-provider-alert/duora-selfie-provider-alert.timer').read_text()
        self.assertIn('OnUnitInactiveSec=30s\n', timer)
        self.assertLessEqual(2 * (30 + 2 * 8 + 1), alert.RESTART_AFTER)

    def test_unit_writes_only_its_state_and_the_outbox(self):
        unit = (ROOT / 'infra/selfie-provider-alert/duora-selfie-provider-alert.service').read_text()
        self.assertIn('ProtectSystem=strict', unit)
        self.assertIn(f'ReadWritePaths={alert.STATE.parent} -{alert.OUTBOX}\n', unit)
        self.assertIn('ExecStart=/usr/bin/python3 /opt/duora-selfie-alert/selfie_provider_alert.py\n', unit)


if __name__ == '__main__':
    unittest.main()
