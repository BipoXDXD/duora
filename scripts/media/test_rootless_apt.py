"""Real APT regression with signed synthetic metadata; never installs host packages.

Run on both native Ubuntu runners. Exercises the options and commands read from
the workflows, against a loopback repository and an unrelated HTTP 403 source.
"""
from __future__ import annotations

from email.utils import formatdate
import hashlib
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer
import os
from pathlib import Path
import re
import subprocess
import tempfile
import textwrap
import threading
import unittest


ROOT = Path(__file__).resolve().parents[2]
WORKFLOWS = ("media-isolation.yml", "media-release.yml")
PACKAGE = "duora-synthetic-apt-probe"


def workflow_commands(name: str) -> str:
    workflow = (ROOT / ".github/workflows" / name).read_text()
    match = re.search(r"(?ms)^          apt_options=\(.*?(?=^          sudo useradd)", workflow)
    if match is None:
        raise AssertionError(f"{name}: missing scoped prerequisite update/install")
    return textwrap.dedent(match.group())


class RootlessAptTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temp = tempfile.TemporaryDirectory(prefix="duora-apt-regression-")
        cls.addClassCleanup(cls.temp.cleanup)
        cls.root = Path(cls.temp.name)
        cls.arch = subprocess.check_output(["dpkg", "--print-architecture"], text=True).strip()
        cls.gpg = cls.root / "gnupg"
        cls.gpg.mkdir(mode=0o700)
        subprocess.run(["gpg", "--homedir", str(cls.gpg), "--batch", "--passphrase", "",
                        "--quick-gen-key", "Duora synthetic APT regression", "ed25519", "sign", "0"],
                       check=True, capture_output=True, timeout=30)
        cls.key = cls.root / "synthetic.asc"
        cls.key.write_bytes(subprocess.check_output(
            ["gpg", "--homedir", str(cls.gpg), "--armor", "--export"]))
        cls.repo = cls.root / "repo/dists/noble"
        packages_dir = cls.repo / f"main/binary-{cls.arch}"
        packages_dir.mkdir(parents=True)
        packages = (f"Package: {PACKAGE}\nVersion: 1.0\nArchitecture: {cls.arch}\n"
                    "Maintainer: Synthetic CI\nDescription: Never installed\n"
                    "Filename: pool/probe.deb\nSize: 1\nSHA256: " + "0" * 64 + "\n\n").encode()
        (packages_dir / "Packages").write_bytes(packages)
        relative = f"main/binary-{cls.arch}/Packages"
        release = ("Origin: Duora synthetic CI\nLabel: Duora synthetic CI\nSuite: noble\n"
                   f"Codename: noble\nDate: {formatdate(usegmt=True)}\n"
                   f"Architectures: {cls.arch}\nComponents: main\nSHA256:\n"
                   f" {hashlib.sha256(packages).hexdigest()} {len(packages)} {relative}\n")
        (cls.repo / "Release").write_text(release)
        subprocess.run(["gpg", "--homedir", str(cls.gpg), "--batch", "--yes", "--clearsign",
                        "--output", str(cls.repo / "InRelease"), str(cls.repo / "Release")],
                       check=True, capture_output=True, timeout=30)
        cls.signed = (cls.repo / "InRelease").read_text()
        cls.requests: list[str] = []
        requests = cls.requests
        directory = str(cls.root)

        class Handler(SimpleHTTPRequestHandler):
            def __init__(self, *args, **kwargs):
                super().__init__(*args, directory=directory, **kwargs)

            def do_GET(self):
                requests.append(self.path)
                if self.path.startswith("/required-transient/"):
                    self.send_error(503, "Synthetic temporary failure")
                elif self.path.startswith(("/unrelated/", "/required-unavailable/")):
                    self.send_error(403, "Synthetic repository unavailable")
                else:
                    super().do_GET()

            def log_message(self, *args):
                pass

        cls.server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        cls.addClassCleanup(cls.server.server_close)
        cls.thread = threading.Thread(target=cls.server.serve_forever, daemon=True)
        cls.thread.start()
        cls.addClassCleanup(cls.server.shutdown)
        cls.url = f"http://127.0.0.1:{cls.server.server_port}"

    def run_apt(self, commands: str, *, source: str = "repo", tampered: bool = False):
        self.requests.clear()
        (self.repo / "InRelease").write_text(
            self.signed.replace("Suite: noble", "Suite: tampered") if tampered else self.signed)
        with tempfile.TemporaryDirectory(dir=self.root) as directory:
            fixture = Path(directory)
            scoped = fixture / "scoped"
            (scoped / "sources").mkdir(parents=True)
            (scoped / "lists").mkdir()
            (scoped / "sources/probe.list").write_text(
                f"deb [arch={self.arch} signed-by={self.key}] {self.url}/{source} noble main\n")
            (fixture / "sources.list").write_text(
                f"deb [signed-by={self.key}] {self.url}/unrelated noble main\n")
            (fixture / "parts").mkdir()
            (fixture / "parts/unrelated.list").write_text(
                f"deb [signed-by={self.key}] {self.url}/unrelated/parts noble main\n")
            (fixture / "lists").mkdir()
            (fixture / "status").touch()
            # Exercise real APT, with global state redirected to the fixture.
            # Only install is simulated; update and signature verification are real.
            harness = r'''
                fixture_options=(-o "Dir::Etc::sourcelist=$fixture/sources.list"
                  -o "Dir::Etc::sourceparts=$fixture/parts"
                  -o "Dir::State::lists=$fixture/lists"
                  -o "Dir::State::status=$fixture/status"
                  -o "Dir::Cache::pkgcache=" -o "Dir::Cache::srcpkgcache="
                  -o "Acquire::Retries=0" -o "Debug::NoLocking=1")
                sudo() {
                  test "$1" = apt-get
                  shift
                  printf 'apt-call:%s\n' "$*"
                  if [[ " $* " == *" install "* ]]; then
                    /usr/bin/apt-get "${fixture_options[@]}" --simulate "$@"
                  else
                    /usr/bin/apt-get "${fixture_options[@]}" "$@"
                  fi
                }
            '''
            # Substitute only the package list: the actual update/install options
            # and fail-fast shell execution remain those in each workflow.
            commands = commands.replace(
                "uidmap dbus-user-session slirp4netns docker-ce-rootless-extras", PACKAGE)
            return subprocess.run(["bash", "-euo", "pipefail", "-c",
                                   textwrap.dedent(harness) + commands],
                                  env={**os.environ, "fixture": str(fixture), "apt_root": str(scoped)},
                                  capture_output=True, text=True, timeout=30)

    def test_unscoped_baseline_reproduces_403_before_install(self):
        result = self.run_apt(f"sudo apt-get update -qq\nsudo apt-get install -y {PACKAGE}\n")
        self.assertEqual(100, result.returncode, result.stdout + result.stderr)
        self.assertIn("403", result.stderr)
        self.assertTrue(any(path.startswith("/unrelated/") for path in self.requests))
        self.assertNotIn(" install ", result.stdout)

    def test_scoped_update_and_install_exclude_unrelated_runner_source(self):
        for workflow in WORKFLOWS:
            with self.subTest(workflow=workflow):
                result = self.run_apt(workflow_commands(workflow))
                self.assertEqual(0, result.returncode, result.stdout + result.stderr)
                self.assertIn(f"Inst {PACKAGE} (1.0", result.stdout)
                self.assertFalse(any(path.startswith("/unrelated/") for path in self.requests))

    def test_required_repository_403_is_fatal_and_prevents_install(self):
        for workflow in WORKFLOWS:
            with self.subTest(workflow=workflow):
                result = self.run_apt(workflow_commands(workflow), source="required-unavailable")
                self.assertEqual(100, result.returncode, result.stdout + result.stderr)
                self.assertIn("403", result.stderr)
                self.assertNotIn(" install ", result.stdout)

    def test_required_repository_transient_error_is_fatal_and_prevents_install(self):
        for workflow in WORKFLOWS:
            with self.subTest(workflow=workflow):
                result = self.run_apt(workflow_commands(workflow), source="required-transient")
                self.assertEqual(100, result.returncode, result.stdout + result.stderr)
                self.assertIn("503", result.stderr)
                self.assertNotIn(" install ", result.stdout)

    def test_invalid_signature_is_fatal_and_prevents_install(self):
        for workflow in WORKFLOWS:
            with self.subTest(workflow=workflow):
                result = self.run_apt(workflow_commands(workflow), tampered=True)
                self.assertEqual(100, result.returncode, result.stdout + result.stderr)
                self.assertIn("BADSIG", result.stderr)
                self.assertNotIn(" install ", result.stdout)


if __name__ == "__main__":
    unittest.main()
