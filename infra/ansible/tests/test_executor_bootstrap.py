"""Run the real executor tasks with temporary destinations in a disposable Linux root environment."""

import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[3]
SOURCE = ROOT / "scripts/deploy"
TASKS = ROOT / "infra/ansible/roles/app_dirs/tasks/executor.yml"
MEMBERS = ("duora_deploy.py", "reconcile_payload.py", "runtime_guard.py", "postgres_swap.py")


def digest(content: bytes) -> str:
    return hashlib.sha256(content).hexdigest()


class ExecutorBootstrapTests(unittest.TestCase):
    def setUp(self) -> None:
        temporary = tempfile.TemporaryDirectory(prefix="duora-executor-test-")
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.entry = self.root / "sbin/duora-deploy"
        self.entry.parent.mkdir()
        self.state = self.root / "state"
        self.state.mkdir(mode=0o700)
        self.install_dir = self.root / "duora"
        self.releases = self.root / "qualified/releases"
        self.receipt = self.state / "executor-install-receipt.json"
        self.baseline = self.state / "executor-runtime-baseline.json"

    def provision(self, *, check: bool = False, source: Path = SOURCE) -> subprocess.CompletedProcess[str]:
        playbook = self.root / "test.json"
        playbook.write_text(json.dumps([{
            "name": "Exercitar apenas o executor em destinos temporários",
            "hosts": "localhost",
            "gather_facts": False,
            "tasks": [{"name": "Carregar as tarefas do role", "ansible.builtin.import_tasks": str(TASKS)}],
        }]), encoding="utf-8")
        variables = {
            "ansible_python_interpreter": sys.executable,
            "app_dirs_executor_entry": str(self.entry),
            "app_dirs_executor_releases": str(self.releases),
            "app_dirs_executor_source": str(source),
            "duora_state_dir": str(self.state),
            "duora_install_dir": str(self.install_dir),
        }
        return subprocess.run(
            ["ansible-playbook", "-c", "local", "-i", "localhost,", str(playbook), "-e", json.dumps(variables),
             *(["--check"] if check else [])],
            cwd=self.root, capture_output=True, text=True, timeout=90, check=False,
        )

    def assertProvisioned(self, result: subprocess.CompletedProcess[str]) -> None:
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def assertRefused(self, result: subprocess.CompletedProcess[str], reason: str) -> None:
        self.assertNotEqual(result.returncode, 0, result.stdout)
        self.assertIn(reason, result.stdout + result.stderr)

    def assertSnapshot(self, snapshot: Path, source: Path = SOURCE) -> None:
        self.assertEqual(set(snapshot.iterdir()), {snapshot / filename for filename in MEMBERS})
        for filename in MEMBERS:
            installed = snapshot / filename
            self.assertEqual(installed.read_bytes(), (source / filename).read_bytes())
            self.assertEqual(installed.stat().st_uid, 0)
            self.assertEqual(installed.stat().st_mode & 0o777, 0o755 if filename == "duora_deploy.py" else 0o600)

    def test_fresh_check_mode_and_repeat_keep_a_single_complete_snapshot(self) -> None:
        preview = self.provision(check=True)
        self.assertProvisioned(preview)
        self.assertRegex(preview.stdout, r"changed=[1-9]")
        self.assertFalse(self.entry.exists())
        self.assertFalse(self.install_dir.exists())
        self.assertProvisioned(self.provision())
        self.assertTrue(self.entry.is_symlink())
        target = self.entry.readlink()
        self.assertSnapshot(target.parent)
        self.assertEqual(target.parent.stat().st_mode & 0o777, 0o700)
        python312 = shutil.which("python3.12")
        if python312 is None:
            self.fail("O teste de bootstrap exige python3.12 no PATH do Linux descartável")
        help_result = subprocess.run([python312, "-I", str(self.entry), "--help"],
                                     cwd=self.root, capture_output=True, text=True, timeout=15, check=False)
        self.assertProvisioned(help_result)
        self.assertIn("usage:", help_result.stdout)
        repeated = self.provision()
        self.assertProvisioned(repeated)
        self.assertRegex(repeated.stdout, r"changed=0\b")
        self.assertEqual(self.entry.readlink(), target)
        self.assertSnapshot(target.parent)

        newer = self.root / "newer"
        newer.mkdir()
        for filename in MEMBERS:
            (newer / filename).write_bytes((SOURCE / filename).read_bytes() + b"\n# New revision\n")
        later = self.provision(source=newer)
        self.assertProvisioned(later)
        self.assertRegex(later.stdout, r"changed=0\b")
        self.assertEqual(tuple(target.parent.parent.iterdir()), (target.parent,))
        self.assertEqual(self.entry.readlink(), target)
        self.assertSnapshot(target.parent)

    def test_retry_completes_a_partial_snapshot_before_creating_the_entry(self) -> None:
        identity = digest("".join(digest((SOURCE / name).read_bytes()) for name in MEMBERS).encode())
        snapshot = self.install_dir / "executor-bootstrap" / identity
        snapshot.mkdir(parents=True)
        (snapshot / "duora_deploy.py").write_text("partial", encoding="utf-8")
        incomplete = self.root / "incomplete"
        incomplete.mkdir()
        for filename in MEMBERS[:-1]:
            shutil.copyfile(SOURCE / filename, incomplete / filename)
        failure = self.provision(source=incomplete)
        self.assertRefused(failure, "postgres_swap.py")
        self.assertFalse(self.entry.is_symlink())
        self.assertProvisioned(self.provision())
        self.assertEqual(self.entry.readlink().parent, snapshot)
        self.assertSnapshot(snapshot)

    def create_sealed(self) -> Path:
        source_commit = "b" * 40
        target = self.releases / source_commit / "duora_deploy.py"
        self.releases.parent.mkdir(mode=0o755)
        self.releases.mkdir(mode=0o700)
        target.parent.mkdir(parents=True, mode=0o700)
        files = {}
        for filename in (*MEMBERS, "install_executor.py"):
            file = target.parent / filename
            file.write_text("sealed " + filename, encoding="utf-8")
            mode = 0o755 if file == target else 0o600
            file.chmod(mode)
            files[filename] = {"mode": mode, "sha256": digest(file.read_bytes())}
        self.entry.symlink_to(target)
        self.baseline.write_bytes(b"sealed baseline")
        self.receipt.write_text(json.dumps({"schema": 1, "status": "installed", "sourceCommit": source_commit,
                                            "baselineSha256": digest(self.baseline.read_bytes()),
                                            "executorSha256": files[target.name]["sha256"],
                                            "files": files}), encoding="utf-8")
        self.receipt.chmod(0o600)
        self.baseline.chmod(0o600)
        return target

    def test_sealed_installation_keeps_link_bytes_owners_and_modes(self) -> None:
        target = self.create_sealed()
        files = [self.receipt, self.baseline, *target.parent.iterdir()]
        before = {path: (path.read_bytes(), path.stat().st_uid, path.stat().st_gid, path.stat().st_mode)
                  for path in files}
        for check in (True, False):
            result = self.provision(check=check)
            self.assertProvisioned(result)
            self.assertRegex(result.stdout, r"changed=0\b")
            self.assertEqual(self.entry.readlink(), target)
            self.assertEqual({path: (path.read_bytes(), path.stat().st_uid, path.stat().st_gid, path.stat().st_mode)
                              for path in files}, before)
            self.assertEqual(self.releases.parent.stat().st_mode & 0o777, 0o755)
            self.assertFalse(self.install_dir.exists())

    def test_corrupt_seal_fails_without_creating_a_bootstrap(self) -> None:
        target = self.create_sealed()
        self.baseline.write_bytes(b"unexpected baseline")
        result = self.provision()
        self.assertRefused(result, "descrever a mesma versão instalada")
        self.assertEqual(self.entry.readlink(), target)
        self.assertEqual(self.baseline.read_bytes(), b"unexpected baseline")
        self.assertFalse(self.install_dir.exists())

    def test_missing_or_changed_sealed_module_fails_without_repairing_it(self) -> None:
        target = self.create_sealed()
        module = target.with_name("runtime_guard.py")
        for content in (None, b"foreign contents"):
            with self.subTest(content=content):
                if content is None:
                    module.unlink()
                else:
                    module.write_bytes(content)
                    module.chmod(0o600)
                self.assertRefused(self.provision(), "Um arquivo da versão selada diverge do recibo ou está ausente")
                self.assertEqual(self.entry.readlink(), target)
                self.assertEqual(module.read_bytes() if module.exists() else None, content)
                self.assertFalse(self.install_dir.exists())

    def test_foreign_dangling_link_is_preserved_and_refused(self) -> None:
        dangling = self.root / "foreign/duora_deploy.py"
        self.entry.symlink_to(dangling)
        self.assertRefused(self.provision(), "aponta para fora do bootstrap")
        self.assertEqual(self.entry.readlink(), dangling)
        self.assertFalse(dangling.exists())
        self.assertFalse(self.install_dir.exists())

    def test_sealed_release_directory_cannot_be_redirected_by_a_symlink(self) -> None:
        target = self.create_sealed()
        foreign = self.root / "relocated"
        self.releases.rename(foreign)
        self.releases.symlink_to(foreign)
        self.assertRefused(self.provision(), "sob diretórios protegidos e reais de root")
        self.assertEqual(self.entry.readlink(), target)
        self.assertEqual(self.releases.readlink(), foreign)
        self.assertFalse(self.install_dir.exists())

    def test_sealed_ancestor_writable_by_a_group_is_refused(self) -> None:
        target = self.create_sealed()
        self.releases.parent.chmod(0o775)
        self.assertRefused(self.provision(), "sob diretórios protegidos e reais de root")
        self.assertEqual(self.entry.readlink(), target)
        self.assertEqual(self.releases.parent.stat().st_mode & 0o777, 0o775)


if __name__ == "__main__":
    unittest.main()
