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


class gatewayBudgetTests(unittest.TestCase):
    def test_budget_matches_every_resource_definition(self) -> None:
        budget = runpy.run_path(str(ROOT / 'scripts/telemetry/capacity.py'))
        prod = yaml.safe_load((ROOT / 'infra/compose/compose.prod.yml').read_text())['services']
        agent = yaml.safe_load((ROOT / 'infra/telemetry/compose.yml').read_text())['services']
        self.assertEqual(set(agent), {'gateway'})
        gateway = agent['gateway']
        mib = 1024**2
        permanent = sum(memory_bytes(s['deploy']['resources']['limits']['memory'])
                        for s in prod.values() if not s.get('profiles'))
        self.assertEqual(permanent, budget['PERMANENT_MIB'] * mib)
        self.assertEqual(memory_bytes(prod['migrator']['deploy']['resources']['limits']['memory']),
                         budget['MIGRATOR_MIB'] * mib)
        self.assertEqual(memory_bytes(gateway['mem_limit']), budget['GATEWAY_MIB'] * mib)
        self.assertEqual(gateway['memswap_limit'], gateway['mem_limit'])
        self.assertEqual(gateway.get('scale', 1), 1)
        self.assertEqual(gateway.get('deploy', {}).get('replicas', 1), 1)
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
        self.assertLess(gateway['cpu_shares'], prod['backup']['cpu_shares'])
        self.assertLessEqual(gateway['cpus'], 0.5)
        self.assertGreater(gateway['oom_score_adj'], 0)

    def test_gateway_remains_isolated(self) -> None:
        gateway = yaml.safe_load((ROOT / 'infra/telemetry/compose.yml').read_text())['services']['gateway']
        self.assertEqual(gateway['user'], '22001:22001')
        self.assertTrue(gateway['read_only'])
        self.assertEqual(gateway['cap_drop'], ['ALL'])
        self.assertIn('no-new-privileges:true', gateway['security_opt'])
        self.assertEqual(gateway['restart'], 'no')
        self.assertFalse(gateway.get('ports'))
        self.assertFalse(gateway.get('privileged'))
        self.assertEqual(gateway['networks'], ['agent'])
        for mount in gateway['volumes']:
            source = mount.split(':')[0]
            self.assertNotIn('docker.sock', source)
            self.assertNotIn(source, ('/etc/duora', '/var/lib/duora', '/', '/etc', '/var/lib'))
        installer = (ROOT / 'scripts/telemetry/install.sh').read_text()
        self.assertIn('install -m 0640 -o root -g duora-monitor', installer)

    def test_install_gate_precedes_host_mutations(self) -> None:
        installer = (ROOT / 'scripts/telemetry/install.sh').read_text()
        self.assertLess(installer.index('capacity.py'), installer.index('useradd'))
        self.assertLess(installer.index('capacity.py'), installer.index('fallocate'))
        service = unit('infra/telemetry/systemd/duora-agent.service', 'Service')
        self.assertIn('preflight.py', (ROOT / 'infra/telemetry/systemd/duora-agent.service').read_text())
        self.assertIn('--pull never', service['ExecStart'])

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
        installer = (ROOT / 'scripts/telemetry/install-remote.sh').read_text()
        self.assertNotRegex(installer, r'systemctl (enable|start|restart)')
        self.assertIn('--mode gateway', (ROOT / 'infra/telemetry/remote/gateway/observer.conf').read_text())

    def test_telemetry_python_regressions(self) -> None:
        subprocess.run([sys.executable, '-m', 'unittest', 'discover', '-s',
                        str(ROOT / 'scripts/telemetry'), '-p', 'test_*.py', '-v'], check=True)

    def test_telemetry_types(self) -> None:
        subprocess.run([sys.executable, '-m', 'pyright', '--pythonpath', sys.executable,
                        str(ROOT / 'scripts/telemetry')], check=True)

    def test_mcp_stdio_regressions(self) -> None:
        subprocess.run(['node', '--test', str(ROOT / 'scripts/telemetry/test-mcp.mjs')], check=True)
