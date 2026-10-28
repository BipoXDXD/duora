"""Pure parts of the host corpus run (#174): kit verification, evidence parsing and docker arguments.

Nothing here starts Docker, processes or network; the host run itself happens only on the qualified host.
"""
from __future__ import annotations

import hashlib
import io
import json
import stat
import sys
import tempfile
import unittest
import zipfile
from pathlib import Path
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts/media"))
import host_corpus as hc
from launcher import Profile

WORKFLOW = (ROOT / ".github/workflows/media-isolation.yml").read_text()
IMAGE_ID = "sha256:" + "c" * 64
PROFILE = Profile.from_dict({"schema": 1, "image": IMAGE_ID, "runtime_socket": "/run/user/988/docker.sock",
                             "control_socket": "/run/duora-media/control.sock",
                             "journal_directory": "/var/lib/duora-media-launcher/journal",
                             "worker_uid": 1654, "control_gid": 987}, 988)
CASES = sorted(hc.CORPUS_CASES)


def baseline_line(case: str) -> str:
    sample = {"Case": case, "Seed": 1, "Sample": {"Outcome": "Sanitized", "PeakWorkingSetBytes": 1000}}
    return ("\x1b[90m    Duora.IntegrationTests.Media.SanitizerResourceEnvelopeTests.X [OUTPUT] "
            + hc.BASELINE_MARKER + json.dumps(sample) + "\x1b[0m")


def corpus_log(cases=CASES, samples=2, summary="Total: 3, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0"):
    lines = [baseline_line(case) for case in cases for _ in range(samples)]
    if summary:
        lines.append("\x1b[37m   Duora.MediaCorpusTests  " + summary + ", Time: 76.7s\x1b[0m")
    return "\n".join(lines) + "\n"


GOOD_PROBE = """memory.max=536870912
memory.swap.max=0
cpu.max=100000 100000
pids.max=64
uid=1654:1654
Seccomp=2
NoNewPrivs=1
CapEff=0000000000000000
interfaces=lo,
dns=failed
tcp=failed
rootfs=readonly
env=DOTNET_EnableDiagnostics,HOME,HOSTNAME,PATH,
probe=done
"""


class ArgumentTests(unittest.TestCase):
    def test_corpus_cases_are_the_hosted_runner_harness_cases(self):
        # Imported only here: the harness itself must never run on the application host.
        from test_sanitizer_isolation import NATIVE_CORPUS_CASES
        self.assertEqual(NATIVE_CORPUS_CASES, set(hc.CORPUS_CASES))
        self.assertEqual(18, len(hc.CORPUS_CASES))

    def test_sdk_image_is_the_one_pinned_by_the_isolation_workflow(self):
        self.assertIn(f"image='{hc.SDK_IMAGE}'", WORKFLOW)

    def test_client_limits_match_the_hosted_runner_corpus_step(self):
        for fragment in ["--memory 1073741824 --memory-swap 1073741824", "--cpus 0.5 --pids-limit 256",
                         "size=536870912,mode=1777", "--network none --read-only --user 1654:1654 --cap-drop ALL"]:
            with self.subTest(fragment=fragment):
                self.assertIn(fragment, WORKFLOW)
        args = hc.client_args("duora-corpus-x", Path("/var/tmp/k/kit"), IMAGE_ID)
        joined = " ".join(args)
        for fragment in ["--memory 1073741824 --memory-swap 1073741824", "--cpus 0.5 --pids-limit 256",
                         "--network none --read-only --user 1654:1654 --cap-drop ALL",
                         "--security-opt no-new-privileges", "size=536870912,mode=1777",
                         "type=bind,src=/var/tmp/k/kit,dst=/workspace,readonly", "--pull never"]:
            with self.subTest(fragment=fragment):
                self.assertIn(fragment, joined)
        for forbidden in ["docker.sock", "--privileged", "--network host", "/run/secrets", "--env-file", "-v"]:
            with self.subTest(forbidden=forbidden):
                self.assertNotIn(forbidden, args)
                self.assertNotIn(forbidden, joined.split())
        self.assertEqual(IMAGE_ID, args[args.index("/usr/share/dotnet/dotnet") + 1])

    def test_sandbox_probe_reuses_every_launcher_flag_and_replaces_only_the_entrypoint(self):
        job = "d" * 32
        original = PROFILE.create_args(job)
        probe = hc.sandbox_args(PROFILE, job, "echo hi")
        cut = original.index("--entrypoint")
        self.assertEqual(original[:cut], probe[:cut])
        self.assertEqual(["--entrypoint", "/bin/sh", IMAGE_ID, "-c", "echo hi"], probe[cut:])
        for flag in ["--network", "none", "--memory", "536870912", "--pids-limit", "64", "--read-only"]:
            self.assertIn(flag, probe)

    def test_sandbox_probe_refuses_a_changed_launcher_command(self):
        changed = PROFILE.create_args("d" * 32)[:-1] + ["Other.dll"]
        with patch.object(Profile, "create_args", return_value=changed), self.assertRaises(hc.HostCheckError):
            hc.sandbox_args(PROFILE, "d" * 32, "echo hi")


class KitTests(unittest.TestCase):
    ARCH = "amd64"

    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.files = {path: path.encode() * 3 for path in sorted(hc.REQUIRED_KIT_FILES)}

    def manifest(self, files=None, architecture="amd64"):
        files = self.files if files is None else files
        return {"schema": 1, "kind": "standalone-instrumentation", "repository": "BipoXDXD/duora",
                "ci": {"id": 1, "attempt": 1, "job": "backend", "architecture": architecture},
                "source": {"commit": "a" * 40, "tree": "b" * 40},
                "files": [{"path": path, "bytes": len(data), "sha256": hashlib.sha256(data).hexdigest()}
                          for path, data in files.items()]}

    def archive(self, members=None, manifest=None, symlink=None):
        members = self.files if members is None else members
        path = self.root / "kit.zip"
        with zipfile.ZipFile(path, "w") as bundle:
            bundle.writestr("instrumentation.json", json.dumps(manifest or self.manifest()))
            bundle.writestr("backend/", b"")
            for name, data in members.items():
                bundle.writestr(name, data)
            if symlink:
                info = zipfile.ZipInfo(symlink)
                info.external_attr = (stat.S_IFLNK | 0o777) << 16
                bundle.writestr(info, "/etc/passwd")
        return path, hashlib.sha256(path.read_bytes()).hexdigest()

    def test_valid_kit_is_accepted_and_extracted_with_readable_permissions(self):
        path, digest = self.archive()
        manifest = hc.verify_kit(path, digest, self.ARCH)
        destination = self.root / "out"
        hc.extract_kit(path, manifest, destination)
        for name, data in self.files.items():
            target = destination / name
            self.assertEqual(data, target.read_bytes())
            self.assertEqual(0o644, stat.S_IMODE(target.stat().st_mode))
        self.assertEqual(0o755, stat.S_IMODE((destination / "backend").stat().st_mode))
        self.assertFalse((destination / "instrumentation.json").exists())

    def test_hash_mismatch_or_malformed_hash_is_refused(self):
        path, digest = self.archive()
        for wrong in ["0" * 64, digest.upper(), digest[:-1]]:
            with self.subTest(wrong=wrong), self.assertRaises(hc.HostCheckError):
                hc.verify_kit(path, wrong, self.ARCH)

    def test_unsafe_members_are_refused(self):
        for name in ["../escape", "/etc/cron.d/x", "a\\b"]:
            with self.subTest(name=name):
                path, digest = self.archive({**self.files, name: b"x"})
                with self.assertRaises(hc.HostCheckError):
                    hc.verify_kit(path, digest, self.ARCH)

    def test_symbolic_link_member_is_refused(self):
        path, digest = self.archive(symlink="global.link")
        with self.assertRaises(hc.HostCheckError):
            hc.verify_kit(path, digest, self.ARCH)

    def test_members_must_equal_the_manifest(self):
        extra = {**self.files, "backend/extra.dll": b"x"}
        missing = dict(list(self.files.items())[1:])
        for members in [extra, missing]:
            with self.subTest(count=len(members)):
                path, digest = self.archive(members, self.manifest())
                with self.assertRaises(hc.HostCheckError):
                    hc.verify_kit(path, digest, self.ARCH)

    def test_size_mismatch_is_refused(self):
        manifest = self.manifest()
        manifest["files"][0]["bytes"] += 1
        path, digest = self.archive(manifest=manifest)
        with self.assertRaises(hc.HostCheckError):
            hc.verify_kit(path, digest, self.ARCH)

    def test_other_architecture_is_refused(self):
        path, digest = self.archive(manifest=self.manifest(architecture="arm64"))
        with self.assertRaises(hc.HostCheckError):
            hc.verify_kit(path, digest, self.ARCH)

    def test_kit_without_corpus_or_with_integration_assembly_is_refused(self):
        without = {name: data for name, data in self.files.items() if name != hc.CORPUS_ASSEMBLY}
        integration = {**self.files, "backend/tests/x/Duora.IntegrationTests.dll": b"x"}
        for files in [without, integration]:
            with self.subTest(count=len(files)):
                path, digest = self.archive(files, self.manifest(files))
                with self.assertRaises(hc.HostCheckError):
                    hc.verify_kit(path, digest, self.ARCH)

    def test_extraction_rehashes_every_member(self):
        manifest = self.manifest()
        manifest["files"][0]["sha256"] = "0" * 64
        path, digest = self.archive(manifest=manifest)
        verified = hc.verify_kit(path, digest, self.ARCH)
        with self.assertRaises(hc.HostCheckError):
            hc.extract_kit(path, verified, self.root / "out")


class CorpusLogTests(unittest.TestCase):
    def test_eighteen_cases_with_two_samples_and_clean_summary_pass(self):
        parsed = hc.parse_corpus_log(corpus_log())
        self.assertEqual(18, len(parsed["cases"]))
        self.assertEqual({"total": 3, "errors": 0, "failed": 0, "skipped": 0, "notRun": 0}, parsed["summary"])
        self.assertEqual(1000, parsed["peakWorkingSetBytes"])
        self.assertEqual([], hc.assess_corpus(parsed))

    def test_missing_or_unknown_case_fails(self):
        for cases in [CASES[1:], CASES + ["png-unknown"]]:
            with self.subTest(count=len(cases)):
                self.assertTrue(hc.assess_corpus(hc.parse_corpus_log(corpus_log(cases))))

    def test_wrong_sample_count_fails(self):
        for samples in [1, 3]:
            with self.subTest(samples=samples):
                self.assertTrue(hc.assess_corpus(hc.parse_corpus_log(corpus_log(samples=samples))))

    def test_missing_or_unclean_summary_fails(self):
        for summary in ["", "Total: 3, Errors: 0, Failed: 1, Skipped: 0, Not Run: 0",
                        "Total: 3, Errors: 1, Failed: 0, Skipped: 0, Not Run: 0",
                        "Total: 3, Errors: 0, Failed: 0, Skipped: 1, Not Run: 0",
                        "Total: 3, Errors: 0, Failed: 0, Skipped: 0, Not Run: 1",
                        "Total: 0, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0"]:
            with self.subTest(summary=summary):
                self.assertTrue(hc.assess_corpus(hc.parse_corpus_log(corpus_log(summary=summary))))


class ProbeTests(unittest.TestCase):
    def test_expected_sandbox_probe_passes(self):
        self.assertEqual([], hc.assess_probe(hc.parse_probe(GOOD_PROBE)))

    def test_each_isolation_or_limit_deviation_fails(self):
        for old, new in [("memory.max=536870912", "memory.max=max"), ("memory.swap.max=0", "memory.swap.max=max"),
                         ("cpu.max=100000 100000", "cpu.max=max 100000"), ("pids.max=64", "pids.max=max"),
                         ("uid=1654:1654", "uid=0:0"), ("Seccomp=2", "Seccomp=0"), ("NoNewPrivs=1", "NoNewPrivs=0"),
                         ("CapEff=0000000000000000", "CapEff=00000000a80425fb"), ("interfaces=lo,", "interfaces=eth0,lo,"),
                         ("dns=failed", "dns=resolved"), ("tcp=failed", "tcp=connected"),
                         ("rootfs=readonly", "rootfs=writable"), ("probe=done\n", "")]:
            with self.subTest(new=new):
                self.assertTrue(hc.assess_probe(hc.parse_probe(GOOD_PROBE.replace(old, new))))

    def test_visible_secret_paths_or_secret_like_environment_fail(self):
        for extra in ["present=/run/secrets", "present=/var/run/docker.sock"]:
            with self.subTest(extra=extra):
                self.assertTrue(hc.assess_probe(hc.parse_probe(GOOD_PROBE + extra + "\n")))
        # One name per line: adjacent quoted names read as a credential assignment to the secret scanner.
        for name in ["DB_PASSWORD",
                     "B2_APPLICATION_KEY",
                     "ConnectionStrings__Duora",
                     "DOCKER_HOST",
                     "GITHUB_TOKEN"]:
            with self.subTest(name=name):
                text = GOOD_PROBE.replace("env=DOTNET", f"env={name},DOTNET")
                self.assertTrue(hc.assess_probe(hc.parse_probe(text)))

    def test_duplicated_value_is_not_accepted_as_the_expected_one(self):
        self.assertTrue(hc.assess_probe(hc.parse_probe(GOOD_PROBE + "pids.max=64\n")))


MOUNTINFO = """\
600 500 0:60 / / ro,relatime master:1 - overlay overlay ro,lowerdir=/var/lib/duora-media-runtime/.local/share/docker/overlay2/l/A
601 600 0:61 / /proc rw,nosuid,nodev,noexec,relatime - proc proc rw
602 600 8:1 /var/tmp/duora-media-corpus-1/kit /workspace ro,relatime - ext4 /dev/sda1 rw
603 600 0:62 / /tmp rw,nosuid,nodev,noexec,relatime - tmpfs tmpfs rw,size=524288k,mode=1777
604 600 8:1 /var/lib/duora-media-runtime/.local/share/docker/containers/x/hosts /etc/hosts rw,relatime - ext4 /dev/sda1 rw
"""


class HostObservationTests(unittest.TestCase):
    def test_expected_client_mounts_pass(self):
        self.assertEqual([], hc.assess_mounts(hc.parse_mountinfo(MOUNTINFO), {"/", "/workspace"}))

    def test_forbidden_or_writable_mounts_fail(self):
        for line in ["700 600 0:63 /docker.sock /run/docker.sock rw - tmpfs tmpfs rw",
                     "701 600 0:63 /x /run/secrets/db ro - tmpfs tmpfs rw",
                     "702 600 8:1 /var/lib/duora-media/quarantine /data rw - ext4 /dev/sda1 rw",
                     "703 600 8:1 /etc/duora /cfg ro - ext4 /dev/sda1 rw",
                     "704 600 0:64 /user/988 /run/user/988 rw - tmpfs tmpfs rw"]:
            with self.subTest(line=line):
                self.assertTrue(hc.assess_mounts(hc.parse_mountinfo(MOUNTINFO + line + "\n"), {"/", "/workspace"}))
        writable = MOUNTINFO.replace("/workspace ro,relatime", "/workspace rw,relatime")
        self.assertTrue(hc.assess_mounts(hc.parse_mountinfo(writable), {"/", "/workspace"}))

    def test_malformed_mountinfo_is_an_error(self):
        with self.assertRaises(hc.HostCheckError):
            hc.parse_mountinfo("1 2 3\n")

    def test_network_interfaces_and_cgroup_path_are_read_from_proc_formats(self):
        netdev = ("Inter-|   Receive\n face |bytes\n    lo:  0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n"
                  "  eth0:  1 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n")
        self.assertEqual(["eth0", "lo"], hc.interfaces(netdev))
        self.assertEqual("/user.slice/user-988.slice/x.scope", hc.cgroup_path("0::/user.slice/user-988.slice/x.scope\n"))
        with self.assertRaises(hc.HostCheckError):
            hc.cgroup_path("1:name=systemd:/x\n")

    def test_client_cgroup_must_carry_limits_under_a_bounded_account_slice(self):
        scope = {"path": "/sys/fs/cgroup/user.slice/user-988.slice/user@988.service/user.slice/docker-x.scope",
                 "memory.max": "1073741824", "memory.swap.max": "0", "cpu.max": "50000 100000", "pids.max": "256"}
        account = {"path": "/sys/fs/cgroup/user.slice/user-988.slice", "memory.max": "2147483648",
                   "memory.swap.max": "0"}
        self.assertEqual([], hc.assess_client_cgroup([scope, account]))
        self.assertTrue(hc.assess_client_cgroup([{**scope, "pids.max": "max"}, account]))
        self.assertTrue(hc.assess_client_cgroup([scope, {**account, "memory.max": "max"}]))
        self.assertTrue(hc.assess_client_cgroup([scope, {**account, "memory.swap.max": "max"}]))
        self.assertTrue(hc.assess_client_cgroup([scope]))


class EvidenceTests(unittest.TestCase):
    def test_evidence_lists_the_hash_of_every_file(self):
        with tempfile.TemporaryDirectory() as directory:
            evidence = Path(directory)
            (evidence / "corpus.log").write_text("synthetic\n")
            hc.write_evidence(evidence, {"passed": True})
            rows = [line.split("  ") for line in (evidence / "SHA256SUMS").read_text().splitlines()]
            sums = {name: digest for digest, name in rows}
            self.assertEqual({"corpus.log", "evidence.json"}, set(sums))
            self.assertEqual(hashlib.sha256(b"synthetic\n").hexdigest(), sums["corpus.log"])

    def test_cli_refuses_to_run_without_root(self):
        with patch("os.geteuid", return_value=1000), patch("sys.stderr", io.StringIO()), \
                self.assertRaises(SystemExit) as raised:
            hc.main(["--kit-zip", "/k.zip", "--kit-sha256", "0" * 64, "--evidence-dir", "/tmp/e"])
        self.assertEqual(2, raised.exception.code)


if __name__ == "__main__":
    unittest.main()


class HostCommandResolutionTests(unittest.TestCase):
    """The host run clears PATH to /usr/bin:/bin, so every program it starts must be resolvable there or absolute."""

    def test_docker_through_runuser_uses_an_absolute_program_path(self):
        run = hc.HostRun.__new__(hc.HostRun)
        run.user, run.cli, run.socket = "duora-media-launcher", Path("/tmp/cli"), Path("/run/user/988/docker.sock")
        with patch.object(hc.subprocess, "run") as fake:
            run.docker("ps")
        command, environment = fake.call_args.args[0], fake.call_args.kwargs["env"]
        self.assertEqual("/usr/sbin/runuser", command[0])
        self.assertEqual("/usr/bin:/bin", environment["PATH"])

    def test_no_restricted_path_call_names_runuser_relatively(self):
        source = (ROOT / "scripts/media/host_corpus.py").read_text()
        self.assertNotIn('["runuser"', source)

