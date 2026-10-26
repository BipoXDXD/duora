"""Offline safety and evidence regressions; no Docker or .NET is invoked."""
import importlib.util
import json
import os
from pathlib import Path
import sys
from types import SimpleNamespace
import unittest
from unittest.mock import patch

SCRIPT = Path(__file__).with_name('run.py')
spec = importlib.util.spec_from_file_location('outbox_image_run', SCRIPT)
lab = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = lab
spec.loader.exec_module(lab)


class SafetyTests(unittest.TestCase):
    def test_postgres_address_requires_single_private_ipv4_network_and_no_published_ports(self):
        def inspected(address, *, networks=None, bindings=None, published=None):
            return {'NetworkSettings': {'Networks': networks or {'lab': {'IPAddress': address}},
                                        'Ports': published or {}},
                    'HostConfig': {'PortBindings': bindings or {}}}
        for address in ('10.0.0.2', '172.18.0.3', '192.168.4.5'):
            self.assertEqual(address, lab.private_postgres_address(inspected(address), 'lab'))
        for address in ('8.8.8.8', '127.0.0.1', '0.0.0.0', '169.254.3.2', '::1', 'bad'):
            with self.subTest(address=address), self.assertRaises(AssertionError):
                lab.private_postgres_address(inspected(address), 'lab')
        for invalid in (inspected('172.18.0.3', networks={'other': {'IPAddress': '172.18.0.3'}}),
                        inspected('172.18.0.3', networks={'lab': {'IPAddress': '172.18.0.3'},
                                                         'other': {'IPAddress': '172.19.0.4'}}),
                        inspected('172.18.0.3', bindings={'5432/tcp': [{'HostPort': '5432'}]}),
                        inspected('172.18.0.3', published={'5432/tcp': [{'HostPort': '5432'}]})):
            with self.assertRaises(AssertionError):
                lab.private_postgres_address(invalid, 'lab')

    def test_checked_failure_reports_only_static_docker_operation_and_exit_code(self):
        with patch.object(lab.subprocess, 'run', return_value=SimpleNamespace(
                returncode=1, stdout='', stderr='Password=top-secret-123')):
            with self.assertRaises(lab.StepError) as raised:
                lab.checked(['docker', 'network', 'create', '--label', 'top-secret-123'])
        self.assertEqual('docker network create exited 1', str(raised.exception))
        self.assertEqual('docker network create exited 1', lab.safe_check_label(raised.exception))

    def test_diagnostics_emit_only_fixed_names_and_never_log_values(self):
        logs = ('OptionsValidationException: Duora:PublicOrigin was invalid for '
                'private@example.invalid; Password=top-secret-123. '
                'NpgsqlException: Email:PickupDirectory=/private/path. '
                'FakeOptionsValidationException should not match.')
        classified = lab.classify_app_logs(logs)
        self.assertEqual({'exception_types': ['NpgsqlException', 'OptionsValidationException'],
                          'config_keys': ['Duora:PublicOrigin', 'Email:PickupDirectory']}, classified)
        serialized = json.dumps(classified)
        self.assertNotIn('private@example.invalid', serialized)
        self.assertNotIn('top-secret-123', serialized)
        self.assertNotIn('/private/path', serialized)
        self.assertNotIn('FakeOptionsValidationException', serialized)
        self.assertEqual('known_id_not_delivered',
                         lab.safe_check_label(AssertionError('known_id_not_delivered')))
        self.assertIsNone(lab.safe_check_label(AssertionError('top-secret-123')))
        self.assertEqual('docker run exited 17', lab.safe_check_label(lab.StepError('docker run exited 17')))
        self.assertIsNone(lab.safe_check_label(lab.StepError('docker top-secret-123')))
        self.assertEqual('OtherFailure', lab.safe_exception_type(type('SecretName', (Exception,), {})()))

    def test_failure_diagnostics_reads_only_registered_app_container(self):
        rehearsal = lab.Rehearsal()
        rehearsal.resources.containers = ['postgres', 'worker']
        rehearsal.app_containers = {'worker'}
        with patch.object(lab.subprocess, 'run', return_value=SimpleNamespace(
                returncode=0, stdout='InvalidOperationException: Duora:SupportContact private@example.invalid',
                stderr='')) as process:
            result = rehearsal.failure_diagnostics()
        self.assertEqual({'exception_types': ['InvalidOperationException'],
                          'config_keys': ['Duora:SupportContact']}, result)
        process.assert_called_once()
        self.assertEqual(['docker', 'logs', '--tail', '120', 'worker'], process.call_args.args[0])

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
