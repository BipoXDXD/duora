from pathlib import Path
from tempfile import TemporaryDirectory
from unittest import TestCase

from check_picomatch_backport import TARGET, VEX_SOURCE, write_json
from verify_trivy_openvex_block import CRITICAL, FIXTURE, verify_reports


class TrivyOpenVexBlockTests(TestCase):
    def verify(self, *, other=None, fixed=True):
        fixture = {
            'Target': FIXTURE, 'Vulnerabilities': [
                {'VulnerabilityID': CRITICAL, 'PkgName': 'PyYAML', 'Severity': 'CRITICAL'}
            ],
        }
        backport = {'Target': TARGET, 'ExperimentalModifiedFindings': [{
            'Source': VEX_SOURCE, 'Status': 'fixed', 'Finding': {'VulnerabilityID': 'CVE-2026-112366'},
        }] if fixed else []}
        with TemporaryDirectory() as path:
            reports = Path(path)
            (reports / 'policy-exit-code.txt').write_text('1\n', encoding='utf-8')
            write_json(reports / 'trivy-raw.json', {'Results': [fixture]})
            results = [fixture, backport]
            if other:
                results.append(other)
            write_json(reports / 'trivy-policy.json', {'Results': results})
            verify_reports(reports)

    def test_accepts_the_critical_fixture_as_the_only_blocker(self):
        self.verify()

    def test_rejects_another_possible_cause_of_exit_one(self):
        for field, finding in (
            ('Vulnerabilities', {'VulnerabilityID': 'CVE-2099-12345', 'Severity': 'HIGH'}),
            ('Misconfigurations', {'ID': 'SYNTH001', 'Severity': 'HIGH', 'Status': 'FAIL'}),
        ):
            with self.subTest(field=field), self.assertRaisesRegex(ValueError, 'must be the only blocker'):
                self.verify(other={'Target': 'other', field: [finding]})

    def test_rejects_a_scan_without_applied_openvex(self):
        with self.assertRaisesRegex(ValueError, 'did not apply'):
            self.verify(fixed=False)
