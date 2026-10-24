"""Offline safety and evidence regressions; no Docker or .NET is invoked."""
import importlib.util
import os
from pathlib import Path
import sys
import unittest
from unittest.mock import patch

SCRIPT = Path(__file__).with_name('run.py')
spec = importlib.util.spec_from_file_location('outbox_image_run', SCRIPT)
lab = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = lab
spec.loader.exec_module(lab)


class SafetyTests(unittest.TestCase):
    def test_local_and_self_hosted_refused_before_process(self):
        for values in ({}, {'GITHUB_ACTIONS': 'true', 'RUNNER_ENVIRONMENT': 'self-hosted'}):
            with self.subTest(values=values), patch.dict(os.environ, values, clear=True), \
                    patch.object(lab.sys, 'platform', 'linux'), \
                    patch.object(lab.platform, 'machine', return_value='x86_64'), \
                    patch.object(lab.subprocess, 'run') as process:
                with self.assertRaises(lab.SafetyError):
                    lab.require_hosted_linux()
                process.assert_not_called()

    def test_success_requires_every_known_receipt(self):
        delivery = '00000000-0000-0000-0000-000000000001'
        valid = {'status': 'Delivered', 'payload_cleared': True}
        for state, receipt in (({'status': 'Pending', 'payload_cleared': False}, b'x'),
                               ({'status': 'InFlight', 'payload_cleared': False}, b'x'),
                               ({'status': 'Failed', 'error_type': 'InvalidPayload', 'payload_cleared': True}, None),
                               (valid, None),
                               ({'status': 'Delivered', 'payload_cleared': False}, b'x')):
            with self.subTest(state=state, receipt=receipt):
                with self.assertRaises(AssertionError):
                    lab.require_delivered(delivery, state, receipt)
        self.assertEqual(64, len(lab.require_delivered(delivery, valid, b'example')))

    def test_failed_negative_cannot_be_treated_as_drain_even_when_guard_clears(self):
        delivery = '00000000-0000-0000-0000-000000000001'
        rehearsal = lab.Rehearsal()
        rehearsal.known = {delivery: 'duora'}
        rehearsal.probe = lambda mode, *_args: (
            {'rollback_allowed': True, 'pending_new_kind': 0} if mode == 'guard'
            else {'status': 'Failed', 'error_type': 'InvalidPayload', 'payload_cleared': True})
        rehearsal.receipt = lambda _id: None
        with self.assertRaises(AssertionError):
            rehearsal.require_drain()
        self.assertEqual([], rehearsal.report['phases'])

    def test_cleanup_survives_failure_and_removes_all_resources(self):
        calls = []
        def fake(args, **_kwargs):
            calls.append(args)
            if args[1:3] == ['rm', '-fv'] and args[-1] == 'worker':
                raise RuntimeError('failed removal')
        resources = lab.Resources(fake)
        resources.network = 'network'
        resources.containers = ['postgres', 'worker', 'web']
        failures = resources.cleanup()
        self.assertEqual(1, len(failures))
        self.assertIn(['docker', 'rm', '-fv', 'web'], calls)
        self.assertIn(['docker', 'network', 'rm', 'network'], calls)


if __name__ == '__main__':
    unittest.main()
