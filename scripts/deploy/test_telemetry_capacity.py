"""Cross-stack regression and gateway suite entrypoint for the existing ops job."""
import configparser
from pathlib import Path
import runpy
import subprocess
import sys
import unittest

import yaml
from test_capacity_budget import memory_bytes

ROOT = Path(__file__).resolve().parents[2]


def unit(path: str, section: str) -> configparser.SectionProxy:
    parser = configparser.ConfigParser(strict=False, interpolation=None)
    parser.read(ROOT / path)
    return parser[section]


class TelemetryBudgetTests(unittest.TestCase):
    def test_budget_matches_every_resource_definition(self) -> None:
        budget = runpy.run_path(str(ROOT / 'scripts/telemetry/capacity.py'))
        prod = yaml.safe_load((ROOT / 'infra/compose/compose.prod.yml').read_text())['services']
        mib = 1024**2
        permanent = sum(memory_bytes(s['deploy']['resources']['limits']['memory'])
                        for s in prod.values() if not s.get('profiles'))
        self.assertEqual(permanent, budget['PERMANENT_MIB'] * mib)
        self.assertEqual(memory_bytes(prod['migrator']['deploy']['resources']['limits']['memory']),
                         budget['MIGRATOR_MIB'] * mib)
        observers = [unit(f'infra/telemetry/systemd/duora-{name}.service', 'Service')
                     for name in ('monitor', 'observer')]
        self.assertEqual(sum(memory_bytes(s['MemoryMax']) for s in observers), budget['OBSERVERS_MIB'] * mib)
        for service in observers:
            self.assertEqual(service['MemorySwapMax'], '0')
        media = unit('infra/ansible/roles/backup/files/duora-media.slice', 'Slice')
        self.assertEqual(memory_bytes(media['MemoryMax']), budget['MEDIA_MIB'] * mib)
        self.assertEqual(media['MemorySwapMax'], '0')
        template = unit('infra/ansible/roles/backup/templates/duora-media-backup@.service.j2', 'Service')
        self.assertEqual(template['Slice'], 'duora-media.slice')
        self.assertIn('duora-media.slice', (ROOT / 'infra/ansible/roles/backup/tasks/main.yml').read_text())
        self.assertEqual(budget['REQUIRED_MIB'], 10112)
        self.assertGreater(budget['REQUIRED_MIB'] * mib, 8_000_000_000)
        # The external service retains its full reserved budget even outside this repository.
        self.assertEqual(budget['GATEWAY_MIB'], 1536)

    def test_runtime_adapter_is_outside_product_scope(self) -> None:
        for relative in ('infra/telemetry/compose.yml', 'infra/telemetry/workspace',
                         'scripts/telemetry/install.sh', 'scripts/telemetry/activate.py'):
            self.assertFalse((ROOT / relative).exists())

    def test_remote_profile_budgets_match_isolated_units(self) -> None:
        budget = runpy.run_path(str(ROOT / 'scripts/telemetry/capacity.py'))
        source = unit('infra/telemetry/remote/source/duora-telemetry-collect.service', 'Service')
        sender = unit('infra/telemetry/remote/source/duora-telemetry-send.service', 'Service')
        receiver = unit('infra/telemetry/remote/gateway/duora-telemetry-receiver.service', 'Service')
        self.assertEqual(memory_bytes(source['MemoryMax']), 128 * 1024**2)
        self.assertEqual(memory_bytes(sender['MemoryMax']), 64 * 1024**2)
        self.assertEqual(memory_bytes(receiver['MemoryMax']), 128 * 1024**2)
        self.assertEqual(budget['SOURCE_REQUIRED_MIB'], 8512)
        self.assertEqual(budget['REMOTE_REQUIRED_MIB'], 3456)
        self.assertEqual(source['RestrictAddressFamilies'], 'AF_UNIX')
        for service in (source, sender, receiver):
            self.assertEqual(service['MemorySwapMax'], '0')
            self.assertEqual(service['NoNewPrivileges'], 'yes')
        for service in (sender, receiver, unit('infra/telemetry/systemd/duora-monitor.service', 'Service')):
            # Fresh remote hosts intentionally lack product/agent paths. Existing paths still stay hidden.
            self.assertTrue(all(path.startswith('-/') for path in service['InaccessiblePaths'].split()))
        self.assertEqual(receiver['User'], 'duora-receiver')
        self.assertEqual(sender['User'], 'duora-shipper')
        self.assertEqual(sender['CapabilityBoundingSet'], '')
        self.assertIn('/var/run/docker.sock', sender['InaccessiblePaths'])
        self.assertIn('/var/run/docker.sock', receiver['InaccessiblePaths'])
        self.assertIn('--runtime', sender['ExecStartPre'])
        for path in ROOT.glob('infra/telemetry/remote/**/*.service'):
            self.assertNotIn('Environment=SSLKEYLOGFILE', path.read_text())
        self.assertIn('--mode gateway', (ROOT / 'infra/telemetry/remote/gateway/observer.conf').read_text())

    def test_telemetry_python_regressions(self) -> None:
        subprocess.run([sys.executable, '-m', 'unittest', 'discover', '-s',
                        str(ROOT / 'scripts/telemetry'), '-p', 'test_*.py', '-v'], check=True)

    def test_telemetry_types(self) -> None:
        subprocess.run([sys.executable, '-m', 'pyright', '--pythonpath', sys.executable,
                        str(ROOT / 'scripts/telemetry')], check=True)

