"""Bounded synthetic caller tests; never starts Docker or reads protected processes."""
import copy
import io
import json
from pathlib import Path
import unittest
from test_capture import load, admission, FixtureAuthority

class Executor:
    def __init__(self): self.calls = []; self.cleanup = True; self.reply = None; self.error = None
    def run(self, plan, supplier, deadline):
        self.calls.append(plan)
        self.payload = supplier()
        if self.error: raise self.error
        return self.reply
    def cleanup_owned(self, deadline): return self.cleanup

class CallerTests(unittest.TestCase):
    def test_missing_profile_is_closed_before_supplier(self):
        m = load("ram_caller"); c = m.capture_module
        t = FixtureAuthority(c); e = Executor()
        result = m.run_role("web", None, t, e, clock=lambda: 0)
        self.assertEqual(result["reason"], "UNKNOWN_RUNTIME_PROFILE")
        self.assertEqual((t.reads, e.calls), (0, []))
    def test_plan_is_fixed_nonroot_offline_readonly_and_has_no_private_values(self):
        m = load("ram_caller"); c = m.capture_module; t = FixtureAuthority(c)
        p = m.sandbox_plan("web", admission(c), t.snapshot("web", 5))
        args = p["create_argv"]
        for item in ["1654:1654", "none", "--read-only", "ALL", "no-new-privileges=true", "--log-driver", "--pull=never"]:
            self.assertIn(item, args)
        self.assertEqual(p["entrypoint"], ["/opt/python/bin/python3.13", "-I", "-B", "/opt/probe/run_preserved_probe.py", "--artifact-dir", "/opt/probe/artifact", "--dotnet", "/usr/share/dotnet/dotnet"])
        self.assertEqual(p["mounts"], [("/proc/123/root/app", "/app"), ("/proc/123/root/run/secrets", "/run/secrets")])
        self.assertNotIn("private-sentinel", json.dumps(p))
        self.assertNotIn("--privileged", args)
    def test_one_attempt_and_closed_reply_with_all_acceptance_flags_false(self):
        m = load("ram_caller"); c = m.capture_module; t = FixtureAuthority(c); e = Executor()
        e.reply = json.dumps({"artifact_head": m.ARTIFACT_HEAD,
            "cached_runtime_options_observed": False, "runtime_effective_proven": False,
            "application_readiness_observed": False, "status": "replay_verified",
            "configuration_matches_approved": True, "ioptions_matches_approved": True,
            "source": "environment", "infrastructure_module_id": "12345678-1234-1234-1234-123456789abc"}).encode()
        session = m.Session()
        result = m.run_role("web", admission(c), t, e, session=session, clock=lambda: 0)
        self.assertEqual(result["status"], "REPLAY_VERIFIED")
        self.assertTrue(result["cleanup_confirmed"])
        for key in m.FALSE_FLAGS: self.assertIs(result[key], False)
        again = m.run_role("web", admission(c), t, e, session=session, clock=lambda: 0)
        self.assertEqual(again["reason"], "ROLE_ALREADY_ATTEMPTED")
        self.assertEqual(len(e.calls), 1)
    def test_timeout_does_not_claim_cleanup_and_leaks_no_raw_diagnostics(self):
        m = load("ram_caller"); c = m.capture_module; t = FixtureAuthority(c); e = Executor()
        e.error = RuntimeError("private-sentinel"); e.cleanup = False
        result = m.run_role("worker", admission(c), t, e, clock=lambda: 0)
        self.assertEqual(result["status"], "UNKNOWN")
        self.assertFalse(result["cleanup_confirmed"])
        self.assertNotIn("private-sentinel", json.dumps(result))
    def test_raw_reply_unknown_keys_duplicates_and_diagnostics_fail_closed(self):
        m = load("ram_caller"); c = m.capture_module
        for raw in [b'{"status":"replay_verified","status":"replay_verified"}',
                    b'private-sentinel', b'{"status":"replay_verified","extra":"private-sentinel"}']:
            e = Executor(); e.reply = raw
            result = m.run_role("web", admission(c), FixtureAuthority(c), e, clock=lambda: 0)
            self.assertEqual(result["status"], "UNKNOWN")
            self.assertNotIn("private-sentinel", json.dumps(result))
    def test_supplier_cleanup_uncertainty_survives_successful_container_removal(self):
        m = load("ram_caller"); c = m.capture_module; e = Executor()
        e.error = m.Refusal("SUPPLIER_CLEANUP_UNCONFIRMED")
        result = m.run_role("web", admission(c), FixtureAuthority(c), e, clock=lambda: 0)
        self.assertFalse(result["cleanup_confirmed"])
        self.assertEqual(result["reason"], "CLEANUP_UNCONFIRMED")

    def test_mount_inode_mismatch_or_missing_witness_cannot_feed_stdin(self):
        m = load("ram_caller")
        self.assertTrue(hasattr(m, "validate_mounted_identity"), "missing mounted preimage verifier")
        expected = [(1, 2), (1, 3)]
        m.validate_mounted_identity(expected, [(1, 2), (1, 3)])
        for observed in [None, [], [(1, 2), (1, 4)], [(1, 2)]]:
            with self.assertRaisesRegex(m.Refusal, "C934_CONTEXT_CHANGED"):
                m.validate_mounted_identity(expected, observed)

    def test_supplier_deadline_uses_pipe_eof_without_starting_a_process(self):
        m = load("ram_caller")
        self.assertTrue(hasattr(m, "read_supplier_pipe"), "missing bounded EOF reader")
        import os
        import time
        read_fd, write_fd = os.pipe()
        try:
            os.write(write_fd, b"synthetic-private")
            with self.assertRaisesRegex(m.Refusal, "SUPPLIER_TIMEOUT"):
                m.read_supplier_pipe(read_fd, time.monotonic() + 0.03)
        finally:
            os.close(read_fd); os.close(write_fd)

    def test_actual_sandbox_inspection_rejects_added_capabilities_before_capture(self):
        m = load("ram_caller"); c = m.capture_module
        admitted = admission(c); identity = FixtureAuthority(c).snapshot("web", 5)
        plan = m.sandbox_plan("web", admitted, identity)
        host = {"NetworkMode": "none", "ReadonlyRootfs": True, "CapDrop": ["ALL"],
            "CapAdd": None, "SecurityOpt": ["no-new-privileges=true"], "LogConfig": {"Type": "none"},
            "Privileged": False, "PidMode": "", "IpcMode": "private", "Binds": None,
            "Memory": 536870912, "MemorySwap": 536870912, "PidsLimit": 64, "NanoCpus": 500000000,
            "Tmpfs": {"/tmp": "rw,noexec,nosuid,nodev,size=16777216"},
            "Ulimits": [{"Name": "core", "Soft": 0, "Hard": 0}]}
        value = {"image": admitted["profile"]["config_digest"], "host": host,
            "config": {"User": "1654:1654", "Entrypoint": m.ENTRYPOINT[:1], "Cmd": m.ENTRYPOINT[1:],
                       "WorkingDir": "/app", "Env": admitted["profile"]["public_environment"]},
            "mounts": [{"Type": "bind", "RW": False, "Propagation": "rprivate", "Source": source, "Destination": target} for source, target in plan["mounts"]]}
        e = m.DockerExecutor(); e.public = lambda *args: json.dumps(value).encode()
        e.verify_sandbox(plan, 5)
        host["CapAdd"] = ["SYS_ADMIN"]
        with self.assertRaisesRegex(m.Refusal, "SANDBOX_UNQUALIFIED"):
            e.verify_sandbox(plan, 5)

    def test_default_main_has_no_profile_no_authority_and_only_closed_output(self):
        m = load("ram_caller"); output = io.StringIO()
        self.assertEqual(m.main([], output), 2)
        value = json.loads(output.getvalue())["results"][0]
        self.assertEqual(value["reason"], "UNKNOWN_RUNTIME_PROFILE")
        self.assertEqual(set(value), set(m.FALSE_FLAGS) | {"status", "reason", "cleanup_confirmed"})

    def test_supplier_pipe_observes_eof_and_shared_byte_ceiling(self):
        m = load("ram_caller")
        import os
        import time
        read_fd, write_fd = os.pipe()
        try:
            os.write(write_fd, b"synthetic"); os.close(write_fd); write_fd = None
            self.assertEqual(m.read_supplier_pipe(read_fd, time.monotonic() + 1), b"synthetic")
        finally:
            os.close(read_fd)
            if write_fd is not None: os.close(write_fd)

    @unittest.skipUnless(__import__("os").name == "posix", "controlled POSIX process-group fixture")
    def test_supplier_timeout_or_leader_exit_cannot_leave_descendant_alive(self):
        m = load("ram_caller")
        import os
        import selectors
        import signal
        import time
        report_read, report_write = os.pipe()
        descendant = None
        m.capture_module.protect_memory = lambda: None  # synthetic data only; test process ownership, not Linux RAM qualification
        def supplier():
            child = os.fork()
            if child == 0:
                os.close(report_read)
                os.write(report_write, (str(os.getpid()) + "\n").encode())
                while True: time.sleep(0.01)
            return b"synthetic-private"
        try:
            with self.assertRaises(m.Refusal) as caught:
                m.bounded_supplier(supplier, time.monotonic() + 0.12)
            self.assertIn(caught.exception.reason, {"SUPPLIER_TIMEOUT", "SUPPLIER_CLEANUP_UNCONFIRMED"})
            os.close(report_write); report_write = None
            pid_bytes = bytearray()
            while not pid_bytes.endswith(b"\n"):
                pid_bytes.extend(os.read(report_read, 1))
            descendant = int(pid_bytes)
            ready = selectors.DefaultSelector()
            try:
                ready.register(report_read, selectors.EVENT_READ)
                self.assertTrue(ready.select(0.2), "supplier descendant still owns its pipe after timeout/leader exit")
                self.assertEqual(os.read(report_read, 1), b"", "supplier descendant survived cleanup")
            finally: ready.close()
        finally:
            if descendant is not None:
                try: os.kill(descendant, signal.SIGKILL)
                except ProcessLookupError: pass
            os.close(report_read)
            if report_write is not None: os.close(report_write)

    def test_supplier_eof_and_total_deadlines_are_separate_from_original_runner_limits(self):
        m = load("ram_caller")
        self.assertEqual((m.SUPPLIER_SECONDS, m.TOTAL_SECONDS, m.CLEANUP_SECONDS), (5, 25, 5))
        self.assertEqual((m.capture_module.MAX_CAPTURE, m.MAX_OUTPUT, m.RUNNER_SECONDS), (262144, 16384, 15))

if __name__ == "__main__": unittest.main()
