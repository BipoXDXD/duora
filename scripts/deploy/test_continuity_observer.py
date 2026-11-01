"""Real pipe/process regressions for the disposable database observer (#326)."""
import json
import selectors
import sys
import time
import unittest
from unittest.mock import patch

import continuity_lab as lab
from continuity_observer import DatabaseReadinessObserver, ObserverError


# Protocol peer only, not a PostgreSQL replacement: native image qualification uses real pg_isready separately.
PEER = r'''
import sys, time
mode = sys.argv[1]
if mode == 'slow-start': time.sleep(2.1)
if mode == 'stalled-start': time.sleep(20)
if mode == 'dead-start': sys.exit(4)
print('DUORA_DB_OBSERVER_V1', flush=True)
previous = ''
for request in sys.stdin:
    nonce = request.strip()
    if mode == 'stalled': time.sleep(20)
    if mode == 'dead': sys.exit(0)
    if mode == 'oversized': print('SENSITIVE-CANARY-' * 30, flush=True); continue
    if mode == 'partial': sys.stdout.write('P '); sys.stdout.flush(); sys.exit(0)
    if mode == 'non-ascii': sys.stdout.buffer.write(b'\xff\n'); sys.stdout.flush(); continue
    if mode == 'wrong-nonce': nonce = ('1' if nonce[0] != '1' else '2') + nonce[1:]
    if mode == 'replayed' and previous: nonce = previous
    code = 2 if mode == 'down' else 0
    if mode == 'unknown-code': code = 99
    print('P', nonce, code, flush=True)
    previous = request.strip()
if mode == 'bad-shutdown': time.sleep(20)
'''


class DatabaseObserverTests(unittest.TestCase):
    def observer(self, mode: str = 'ready') -> DatabaseReadinessObserver:
        return DatabaseReadinessObserver([sys.executable, '-u', '-c', PEER, mode])

    def test_slow_transport_start_is_admitted_once_within_the_existing_coverage_window(self) -> None:
        start = time.time()
        with self.observer('slow-start') as observer:
            values = [observer.sample(), observer.sample()]
            end = time.time()
            lab.validate_observations(values, start=start, end=end, errors=[], events_alive=True)
            self.assertTrue(all(v['database_ready'] for v in values))
            self.assertGreater(observer.diagnostics['startupSeconds'], 2)
            self.assertEqual(2, observer.diagnostics['sampleCount'])
            self.assertLess(observer.diagnostics['maximumProbeSeconds'], 2)
        self.assertIsNotNone(observer.process.poll())

    def test_each_observation_requires_a_fresh_matching_reply_and_is_timestamped_after_receipt(self) -> None:
        with self.observer() as observer:
            before = time.time()
            first, second = observer.sample(), observer.sample()
            self.assertLessEqual(before, first['at'])
            self.assertLessEqual(first['at'], second['at'])
            self.assertLessEqual(second['at'], time.time())
            self.assertEqual(2, observer.diagnostics['sampleCount'])
            self.assertEqual('persistent-docker-exec', observer.diagnostics['transport'])

    def test_negative_readiness_is_never_accepted_as_observer_latency(self) -> None:
        with self.observer('down') as observer:
            values = [observer.sample(), observer.sample()]
            self.assertFalse(any(v['database_ready'] for v in values))
            with self.assertRaises(RuntimeError):
                lab.validate_observations(values, start=values[0]['at'], end=values[-1]['at'], errors=[], events_alive=True)

    def test_a_stalled_probe_is_fatal_and_the_child_is_reaped(self) -> None:
        observer = self.observer('stalled')
        observer.start()
        try:
            with self.assertRaisesRegex(ObserverError, 'probe-timeout'):
                observer.sample()
            self.assertEqual('probe-timeout', observer.diagnostics['lastError'])
            self.assertEqual(0, observer.diagnostics['sampleCount'])
        finally:
            observer.abort()
        self.assertIsNotNone(observer.process.poll())

    def test_a_reply_received_after_the_probe_deadline_cannot_pass_even_if_the_pipe_is_readable(self) -> None:
        class DescheduledSelector(selectors.DefaultSelector):
            def select(self, timeout=None):
                ready = super().select(timeout)
                if ready:
                    time.sleep(2.05)  # Host scheduling delay after readiness notification, before reading the pipe.
                return ready

        observer = self.observer()
        observer.start()
        try:
            with patch('continuity_observer.selectors.DefaultSelector', DescheduledSelector):
                with self.assertRaisesRegex(ObserverError, 'probe-timeout'):
                    observer.sample()
            self.assertEqual(0, observer.diagnostics['sampleCount'])
        finally:
            observer.abort()

    def test_dead_partial_oversized_foreign_and_invalid_replies_fail_closed_without_echoing_output(self) -> None:
        for mode in ('dead', 'partial', 'oversized', 'wrong-nonce', 'non-ascii', 'unknown-code'):
            with self.subTest(mode=mode):
                observer = self.observer(mode)
                observer.start()
                try:
                    with self.assertRaises(ObserverError) as caught:
                        observer.sample()
                    self.assertNotIn('SENSITIVE-CANARY', str(caught.exception))
                    self.assertNotIn('SENSITIVE-CANARY', json.dumps(observer.diagnostics))
                    self.assertEqual(0, observer.diagnostics['sampleCount'])
                finally:
                    observer.abort()

    def test_a_previous_reply_cannot_satisfy_a_new_request(self) -> None:
        observer = self.observer('replayed')
        observer.start()
        try:
            observer.sample()
            with self.assertRaisesRegex(ObserverError, 'invalid-frame'):
                observer.sample()
            self.assertEqual(1, observer.diagnostics['sampleCount'])
        finally:
            observer.abort()

    def test_failure_before_admission_is_reported_and_reaped(self) -> None:
        observer = self.observer('dead-start')
        with self.assertRaises(ObserverError):
            observer.start()
        self.assertIsNotNone(observer.process.poll())

    def test_transport_admission_has_a_finite_deadline_and_cleans_up(self) -> None:
        observer = self.observer('stalled-start')
        with self.assertRaisesRegex(ObserverError, 'startup-timeout'):
            observer.start()
        self.assertIsNotNone(observer.process.poll())

    def test_a_channel_that_does_not_finish_cannot_turn_a_failed_shutdown_into_a_valid_proof(self) -> None:
        observer = self.observer('bad-shutdown')
        observer.start()
        observer.sample()
        with self.assertRaisesRegex(ObserverError, 'shutdown-failed'):
            observer.close()
        self.assertIsNotNone(observer.process.poll())

    def test_starting_a_process_failure_does_not_expose_arguments(self) -> None:
        observer = DatabaseReadinessObserver(['/no/such/SENSITIVE-CANARY'])
        with self.assertRaises(ObserverError) as caught:
            observer.start()
        self.assertNotIn('SENSITIVE-CANARY', str(caught.exception))

    def test_an_observer_that_dies_after_its_last_reply_cannot_close_successfully(self) -> None:
        peer = "import sys; print('DUORA_DB_OBSERVER_V1',flush=True); n=input(); print('P',n,0,flush=True)"
        observer = DatabaseReadinessObserver([sys.executable, '-u', '-c', peer])
        observer.start()
        observer.sample()
        observer.process.wait(timeout=3)
        with self.assertRaises(ObserverError):
            observer.close()
        observer.abort()

    def test_an_exception_in_the_observation_window_always_cleans_up_the_child(self) -> None:
        observer = self.observer()
        with self.assertRaisesRegex(RuntimeError, 'synthetic operation failed'):
            with observer:
                observer.sample()
                raise RuntimeError('synthetic operation failed')
        self.assertIsNotNone(observer.process.poll())


if __name__ == '__main__':
    unittest.main()
