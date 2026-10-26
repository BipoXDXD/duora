"""Operator boundary regressions; synthetic bytes/processes only, no .NET or Docker."""
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import sys
import tempfile
import time
import unittest
from unittest.mock import patch


MODULE = Path(__file__).with_name("run_preserved_probe.py")
runner = None
if MODULE.exists():
    spec = importlib.util.spec_from_file_location("preserved_probe_runner", MODULE)
    runner = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(runner)


class PreservedProbeRunnerTests(unittest.TestCase):
    def setUp(self):
        self.assertTrue(runner is not None, "preserved_operator_runner_missing")
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.bundle = Path(self.directory.name)
        # Hashlib is independent of the implementation's verification function.
        self.expected = {}
        for name, data in {
            "Duora.IntegrationTests.dll": b"synthetic-managed-helper",
            "Duora.IntegrationTests.runtimeconfig.json": b"synthetic-runtime-config",
            "SHA256SUMS": b"synthetic-internal-manifest",
        }.items():
            (self.bundle / name).write_bytes(data)
            self.expected[name] = hashlib.sha256(data).hexdigest()
        self.pin_patch = patch.object(runner, "PINNED_FILES", self.expected)
        self.pin_patch.start()
        self.addCleanup(self.pin_patch.stop)

    def invoke(self, data=b"", result=None, verify_only=False):
        output = io.StringIO()
        calls = []

        def process(command, payload, timeout):
            calls.append((command, payload, timeout))
            return result if result is not None else (0, self.good_reply())

        args = ["--artifact-dir", str(self.bundle)]
        args += ["--verify-only"] if verify_only else ["--dotnet", sys.executable]
        with patch.object(runner, "run_bounded", process):
            code = runner.main(args, io.BytesIO(data), output)
        return code, json.loads(output.getvalue()), calls

    @staticmethod
    def capture():
        return json.dumps({
            "Role": "worker", "ApplicationAssembly": "/synthetic/app/Duora.Worker.dll",
            "ContentRoot": "/synthetic/app", "Environment": {
                "DOTNET_ENVIRONMENT": "Production", "PRIVATE_CANARY": "synthetic-private-value",
            }, "Arguments": [],
        }).encode()

    @staticmethod
    def good_reply(**changes):
        value = {
            "status": "replayed", "configuration_matches_approved": True,
            "ioptions_matches_approved": True, "source": "key_per_file",
            "cached_runtime_options_observed": False,
            "infrastructure_module_id": "11111111-2222-3333-4444-555555555555",
        }
        value.update(changes)
        return json.dumps(value).encode()

    def test_tampered_artifact_refuses_before_private_input_or_process(self):
        (self.bundle / "Duora.IntegrationTests.dll").write_bytes(b"different")
        class ReadAudit(io.BytesIO):
            was_read = False
            def read(self, size):
                self.was_read = True
                return super().read(size)
        private = ReadAudit(self.capture())
        output = io.StringIO()
        calls = []
        with patch.object(runner, "run_bounded", lambda *args: calls.append(True)):
            code = runner.main(["--artifact-dir", str(self.bundle), "--dotnet", sys.executable], private, output)
        result = json.loads(output.getvalue())
        self.assertTrue(code == 2 and result["status"] == "unknown" and not calls)
        self.assertTrue(result["reason"] == "artifact_untrusted")
        self.assertTrue(private.was_read is False)

    def test_symlink_bundle_entry_cannot_bypass_pin_check(self):
        dll = self.bundle / "Duora.IntegrationTests.dll"
        data = dll.read_bytes()
        dll.unlink()
        outside = tempfile.TemporaryDirectory()
        self.addCleanup(outside.cleanup)
        target = Path(outside.name) / "outside"
        target.write_bytes(data)
        dll.symlink_to(target)
        code, result, calls = self.invoke(self.capture())
        self.assertTrue(code == 2 and result["reason"] == "artifact_untrusted" and not calls)

    def test_unqualified_deps_sidecar_refuses_before_private_capture(self):
        (self.bundle / "Duora.IntegrationTests.deps.json").write_text("{}")
        code, result, calls = self.invoke(self.capture())
        self.assertTrue(code == 2 and result["reason"] == "artifact_untrusted" and not calls)

    def test_verify_only_never_starts_helper_or_reads_private_stdin(self):
        class Unreadable:
            def read(self, _):
                raise AssertionError("private_stdin_must_not_be_read")
        output = io.StringIO()
        calls = []
        with patch.object(runner, "run_bounded", lambda *args: calls.append(True)):
            code = runner.main(["--artifact-dir", str(self.bundle), "--verify-only"], Unreadable(), output)
        result = json.loads(output.getvalue())
        self.assertTrue(code == 0 and result["status"] == "artifact_ready" and not calls)
        self.assertTrue(result["runtime_effective_proven"] is False)

    def test_oversized_input_refuses_without_launch_or_value_echo(self):
        data = b"synthetic-private-canary" * 20000
        code, result, calls = self.invoke(data)
        self.assertTrue(code == 2 and result["reason"] == "capture_too_large" and not calls)
        self.assertTrue("synthetic-private-canary" not in json.dumps(result))

    def test_ambiguous_or_bootstrap_capture_refuses_before_launch(self):
        bootstrap = json.loads(self.capture())
        bootstrap["Environment"]["DOTNET_STARTUP_HOOKS"] = "synthetic"
        captures = [
            self.capture().replace(b'"Role": "worker"', b'"Role": "web", "Role": "worker"'),
            json.dumps(bootstrap).encode(),
        ]
        for data in captures:
            with self.subTest(case="unsupported_capture"):
                code, result, calls = self.invoke(data)
                self.assertTrue(code == 2 and result["reason"] == "capture_invalid" and not calls)

    def test_private_capture_stays_on_stdin_and_replay_keeps_runtime_unknown(self):
        data = self.capture()
        code, result, calls = self.invoke(data)
        self.assertTrue(code == 0 and result["status"] == "replay_verified" and len(calls) == 1)
        self.assertTrue(calls[0][1] == data)
        self.assertTrue(all("synthetic-private-value" not in arg for arg in calls[0][0]))
        self.assertTrue("synthetic-private-value" not in json.dumps(result))
        self.assertTrue(result["runtime_effective_proven"] is False)
        self.assertTrue(result["cached_runtime_options_observed"] is False)
        self.assertTrue(result["application_readiness_observed"] is False)

    def test_helper_failure_and_malformed_output_never_echo_raw_diagnostics(self):
        responses = [(2, b"synthetic-private-error"), (0, b"not-json-private-canary"),
                     (0, self.good_reply(secret="synthetic-private-canary"))]
        for response in responses:
            with self.subTest(case="private_output"):
                code, result, _ = self.invoke(self.capture(), response)
                self.assertTrue(code == 2 and result["status"] == "unknown")
                self.assertTrue("private" not in json.dumps(result))

    def test_unknown_provider_or_claimed_live_cache_cannot_pass(self):
        for changes in [{"source": "other"}, {"cached_runtime_options_observed": True},
                        {"configuration_matches_approved": 1}]:
            with self.subTest(case="unqualified_reply"):
                code, result, _ = self.invoke(self.capture(), (0, self.good_reply(**changes)))
                self.assertTrue(code == 2 and result["status"] == "unknown")

    def test_replay_mismatch_is_not_success_and_contains_no_config_value(self):
        code, result, _ = self.invoke(self.capture(), (0, self.good_reply(ioptions_matches_approved=False)))
        self.assertTrue(code == 3 and result["status"] == "replay_mismatch")
        self.assertTrue(result["ioptions_matches_approved"] is False)
        self.assertTrue(result["runtime_effective_proven"] is False)

    def test_real_subprocess_receives_no_inherited_authentication(self):
        program = "import os,sys,json; d=sys.stdin.buffer.read(); print(json.dumps({'inherited': 'CUSTODY_TEST_CANARY' in os.environ, 'stdin': d == b'only-stdin'}))"
        with patch.dict(os.environ, {"CUSTODY_TEST_CANARY": "synthetic-only"}):
            code, raw = runner.run_bounded([sys.executable, "-c", program], b"only-stdin", 2)
        result = json.loads(raw)
        self.assertTrue(code == 0 and result["inherited"] is False and result["stdin"] is True)

    def test_real_subprocess_stdout_or_stderr_flood_is_bounded(self):
        for stream in ["stdout", "stderr"]:
            program = "import sys; sys." + stream + ".write('x'*100000); sys." + stream + ".flush()"
            try:
                runner.run_bounded([sys.executable, "-c", program], b"", 2)
                self.fail("unbounded_child_output_accepted")
            except runner.Refusal as error:
                self.assertTrue(error.reason == "output_too_large")

    def test_real_subprocess_timeout_stops_its_owned_child(self):
        try:
            runner.run_bounded([sys.executable, "-c", "import time; time.sleep(5)"], b"", 0.1)
            self.fail("unbounded_child_timeout_accepted")
        except runner.Refusal as error:
            self.assertTrue(error.reason == "probe_timeout")

    def test_timeout_reaps_owned_descendant_even_after_parent_exited(self):
        marker = self.bundle / "owned-descendant-finished"
        child = "import time,sys,pathlib; time.sleep(0.6); pathlib.Path(sys.argv[1]).write_text('synthetic')"
        parent = "import subprocess,sys; subprocess.Popen([sys.executable, '-c', " + repr(child) + ", " + repr(str(marker)) + "])"
        try:
            runner.run_bounded([sys.executable, "-c", parent], b"", 0.3)
            self.fail("inherited_pipe_timeout_not_enforced")
        except runner.Refusal as error:
            self.assertTrue(error.reason == "probe_timeout")
        time.sleep(0.5)
        self.assertTrue(not marker.exists(), "owned_descendant_survived_timeout")

    def test_unsupported_process_platform_refuses_before_child_start(self):
        with patch.object(os, "name", "nt"):
            try:
                runner.run_bounded([sys.executable, "-c", "pass"], b"", 2)
                self.fail("unsupported_process_platform_accepted")
            except runner.Refusal as error:
                self.assertTrue(error.reason == "platform_unsupported")


if __name__ == "__main__":
    unittest.main()
