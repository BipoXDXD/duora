"""Synthetic protocol tests. These never claim PostgreSQL, OCI or host recovery proof."""
import copy
import hashlib
import json
import os
import subprocess
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
from typing import cast

import duora_deploy as deploy
from test_duora_deploy import FakeRunner, valid_manifest, parse


CATALOG = json.loads((Path(__file__).parent / "fixtures/recovery-35-48-catalog.json").read_text())


def releases():
    old = valid_manifest()
    old.update(contractVersion="v2", releaseId="old-current", migrations=CATALOG[:35],
               schema={"min": CATALOG[34], "max": CATALOG[34]})
    new = dict(old, releaseId="new-attempt", commit="b" * 40, previousReleaseId="old-current",
               migrations=CATALOG, schema={"min": CATALOG[-1], "max": CATALOG[-1]})
    return parse(old), parse(new)


class RecoveryAssertions(unittest.TestCase):
    def feature(self, name):
        self.assertTrue(hasattr(deploy, name), "Missing bounded current-v2 recovery: " + name)
        return getattr(deploy, name)


class CurrentV2AttemptFixture(RecoveryAssertions):
    def setUp(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.root = Path(directory.name)
        self.old, self.new = releases()
        self.bindings = {name: "a" * 64 for name in (
            "current", "previous", "manifest", "environment", "floor", "runtime", "compose", "hostEnvironment")}
        self.control = {"baselineSha256": "b" * 64, "incomingPinSha256": "c" * 64,
                        "globalIntentSha256": "d" * 64, "projectIntentSha256": "e" * 64}

    def attempt(self):
        return self.feature("make_recovery_attempt")(
            self.old, self.new, sql_sha256="5d666adcaefeaa7883d4e2596c6ef66ee03b67d04bca79d590e690f801573747",
            bindings=self.bindings, control=self.control, nonce="1" * 32)

    def qualify(self, attempt, catalog=None):
        return self.feature("qualify_recovery_attempt")(
            attempt, current=self.old, incoming=self.new, bindings=self.bindings,
            control=self.control, applied_catalog=CATALOG if catalog is None else catalog)


class CurrentV2AttemptTests(CurrentV2AttemptFixture):
    def test_exact_current_v2_attempt_admits_only_known_35_and_48(self):
        attempt = self.attempt()
        self.assertEqual(self.qualify(attempt, CATALOG[:35]), "initial")
        self.assertEqual(self.qualify(attempt), "expanded")
        self.assertNotIn("deployedAt", attempt)
        self.assertNotIn("success", attempt)

    def test_intermediate_unknown_reordered_and_duplicate_catalogs_refuse(self):
        attempt = self.attempt()
        for catalog in [CATALOG[:n] for n in range(36, 48)] + [
                [], CATALOG[:-1] + ["20990101000000_Unknown"], list(reversed(CATALOG)), CATALOG + [CATALOG[-1]]]:
            with self.subTest(catalog=catalog[-1:] or []), self.assertRaises(deploy.DeployError):
                self.qualify(attempt, catalog)

    def test_every_preserved_or_control_binding_drift_refuses(self):
        for field in ("bindings", "control"):
            for key in getattr(self, field):
                attempt = self.attempt()
                attempt[field][key] = "f" * 64
                with self.subTest(field=field, key=key), self.assertRaises(deploy.DeployError):
                    self.qualify(attempt)

    def test_attempt_exact_types_and_manifest_identity_cannot_be_forged(self):
        changes = [("schema", True), ("nonce", "../escape"), ("sqlSha256", "a" * 64),
                   ("current", self.new.raw), ("incoming", self.old.raw), ("extra", "unknown")]
        for key, value in changes:
            attempt = self.attempt()
            attempt[key] = value
            with self.subTest(key=key), self.assertRaises(deploy.DeployError): self.qualify(attempt)

    def test_non_v2_unlinked_or_other_catalog_cannot_arm_receipt(self):
        for changed in [dict(self.new.raw, contractVersion="v1"),
                        dict(self.new.raw, previousReleaseId=None),
                        dict(self.new.raw, migrations=CATALOG[:-1], schema={"min": CATALOG[-2], "max": CATALOG[-2]})]:
            with self.subTest(changed=changed["releaseId"]), self.assertRaises(deploy.DeployError):
                self.feature("make_recovery_attempt")(self.old, parse(changed),
                    sql_sha256="5d666adcaefeaa7883d4e2596c6ef66ee03b67d04bca79d590e690f801573747",
                    bindings=self.bindings, control=self.control, nonce="1" * 32)

    def test_receipt_is_exclusive_durable_and_pending_is_hash_bound(self):
        store = self.feature("RecoveryStore")(self.root)
        attempt = self.attempt()
        pointer = store.arm(attempt)
        self.assertEqual(store.load(), attempt)
        self.assertEqual(pointer["sha256"], hashlib.sha256((self.root / pointer["name"]).read_bytes()).hexdigest())
        self.assertEqual((self.root / pointer["name"]).stat().st_mode & 0o777, 0o600)
        self.assertEqual((self.root / "pending.json").stat().st_mode & 0o777, 0o600)
        with self.assertRaises(deploy.DeployError): store.arm(attempt)
        self.assertEqual(store.load(), attempt)

    def test_store_refuses_tamper_symlink_hardlink_permissions_or_duplicate_fields(self):
        for problem in ("tamper", "symlink", "hardlink", "mode", "duplicate"):
            directory = self.root / problem
            directory.mkdir(mode=0o700)
            store = self.feature("RecoveryStore")(directory)
            pointer = store.arm(self.attempt())
            path = directory / pointer["name"]
            if problem == "tamper": path.write_text('{}')
            if problem == "symlink":
                target = directory / "foreign"
                path.rename(target)
                path.symlink_to(target)
            if problem == "hardlink": os.link(path, directory / "second")
            if problem == "mode": path.chmod(0o640)
            if problem == "duplicate":
                (directory / "pending.json").write_text('{"name":"x","name":"y","sha256":"' + "a" * 64 + '"}')
            with self.subTest(problem=problem), self.assertRaises(deploy.DeployError): store.load()

    def test_unknown_orphan_pending_is_not_overwritten_or_cleaned(self):
        store = self.feature("RecoveryStore")(self.root)
        path = self.root / "pending.json"
        path.write_text('{}')
        path.chmod(0o600)
        before = {p.name: p.read_bytes() for p in self.root.iterdir()}
        with self.assertRaises(deploy.DeployError): store.arm(self.attempt())
        self.assertEqual({p.name: p.read_bytes() for p in self.root.iterdir()}, before)

    def test_orphan_attempt_without_pending_pointer_cannot_be_ignored_by_a_new_nonce(self):
        store = self.feature("RecoveryStore")(self.root)
        orphan = self.root / ("attempt-" + "2" * 32 + ".json")
        orphan.write_text('{"partial":true}')
        orphan.chmod(0o600)
        with self.assertRaises(deploy.DeployError): store.arm(self.attempt())
        self.assertEqual(list(self.root.iterdir()), [orphan])

    def test_file_sync_failure_keeps_partial_evidence_and_prevents_new_attempt(self):
        import stat
        store = self.feature("RecoveryStore")(self.root)
        original_sync = deploy.os.fsync
        def fail_file(descriptor):
            if stat.S_ISREG(os.fstat(descriptor).st_mode): raise OSError("Synthetic file sync crash")
            original_sync(descriptor)
        with patch.object(deploy.os, "fsync", side_effect=fail_file):
            with self.assertRaises(deploy.DeployError): store.arm(self.attempt())
        before = {p.name: p.read_bytes() for p in self.root.iterdir()}
        self.assertTrue(before)
        different = copy.deepcopy(self.attempt())
        different["nonce"] = "3" * 32
        with self.assertRaises(deploy.DeployError): store.arm(different)
        self.assertEqual({p.name: p.read_bytes() for p in self.root.iterdir()}, before)

    def test_expanded_recovery_never_trusts_head_or_a_phase_seal_in_place_of_sql_and_acl(self):
        # A real PostgreSQL transaction and old binary are separate native acceptance gates.
        for former_phase in ("before-sql", "transaction-failed", "commit-seal-lost", "confirmed"):
            events = []
            self.feature("reconcile_recovery_permissions")(
                "expanded", "SELECT 1;", hashlib.sha256(b"SELECT 1;").hexdigest(),
                quiescent=lambda: events.append("quiescent"),
                execute=lambda sql: events.append(("transaction", sql)),
                witness=lambda: events.append("acl"))
            with self.subTest(former_phase=former_phase):
                self.assertEqual(events, ["quiescent", ("transaction", "SELECT 1;"), "acl"])

    def test_initial_recovery_does_not_apply_the_incoming_48_payload(self):
        events = []
        self.feature("reconcile_recovery_permissions")(
            "initial", None, "a" * 64, quiescent=lambda: events.append("quiescent"),
            execute=lambda _: self.fail("35 must not execute SQL48"), witness=lambda: self.fail("No SQL48 witness at35"))
        self.assertEqual(events, ["quiescent"])

    def test_missing_or_different_payload_quiescence_sql_or_acl_failure_refuses(self):
        for failure in ("missing", "hash", "quiescence", "sql", "acl"):
            events = []
            def call(phase):
                events.append(phase)
                if failure == phase: raise deploy.DeployError("Synthetic failure/unknown " + phase)
            sql = None if failure == "missing" else "SELECT 1;"
            expected = "a" * 64 if failure == "hash" else hashlib.sha256(b"SELECT 1;").hexdigest()
            with self.subTest(failure=failure), self.assertRaises(deploy.DeployError):
                self.feature("reconcile_recovery_permissions")(
                    "expanded", sql, expected, quiescent=lambda: call("quiescence"),
                    execute=lambda _: call("sql"), witness=lambda: call("acl"))
            if failure in ("missing", "hash"): self.assertEqual(events, [])
            if failure == "quiescence": self.assertEqual(events, ["quiescence"])
            if failure == "sql": self.assertEqual(events, ["quiescence", "sql"])


class CurrentV2SqlBoundaryFixture(RecoveryAssertions):
    def feature(self, name):
        self.assertTrue(hasattr(deploy, name), "Missing real recovery boundary: " + name)
        return getattr(deploy, name)

    def runner(self, *, oneoffs="", metadata=None, sessions="0", processes="PID COMMAND\n1 postgres\n",
               acl="t", failure=None):
        self.calls = []
        def run(command, *, capture=False, input_text=None):
            self.calls.append((command, capture, input_text))
            output = ""
            joined = " ".join(command)
            if command[:2] == ["docker", "ps"]: output = oneoffs
            elif command[:2] == ["docker", "inspect"]: output = json.dumps(metadata)
            elif command[:2] == ["docker", "top"]: output = processes
            elif "pg_stat_activity" in joined: output = sessions
            elif "recovery_acl_witness" in joined: output = acl
            return subprocess.CompletedProcess(command, 1 if failure and failure in joined else 0, output, "")
        return run


class CurrentV2SqlBoundaryTests(CurrentV2SqlBoundaryFixture):
    def test_no_migrator_and_no_privileged_sql_sessions_or_processes_is_quiescent(self):
        self.feature("_recovery_require_quiescent")(["docker", "compose"], self.runner(), "a" * 64)
        commands = [c[0] for c in self.calls]
        self.assertTrue(any(c[:2] == ["docker", "ps"] and "--all" in c for c in commands))
        self.assertTrue(any("pg_stat_activity" in " ".join(c) for c in commands))
        self.assertTrue(any(c[:2] == ["docker", "top"] for c in commands))
        self.assertFalse(any("stop" in c or "kill" in c or "rm" in c for c in commands))

    def test_unknown_active_or_paused_migrator_and_sql_process_or_session_refuse(self):
        for kwargs in [{"oneoffs": "unknown"}, {"sessions": "1"}, {"sessions": ""},
                       {"processes": "PID COMMAND\n1 postgres\n42 psql\n"},
                       {"processes": ""}, {"failure": "docker ps"}, {"failure": "docker top"}]:
            with self.subTest(kwargs=kwargs), self.assertRaises(deploy.DeployError):
                self.feature("_recovery_require_quiescent")(["docker", "compose"], self.runner(**kwargs), "a" * 64)

    def test_stopped_owned_migrator_is_observed_and_never_removed(self):
        metadata = [{"Id": "b" * 64, "Config": {"Labels": {"com.docker.compose.project": "duora",
                    "com.docker.compose.service": "migrator", "com.docker.compose.oneoff": "True"}},
                    "State": {"Status": "exited", "Running": False, "Paused": False,
                              "Restarting": False, "Pid": 0}}]
        self.feature("_recovery_require_quiescent")(["docker", "compose"], self.runner(
            oneoffs="b" * 64 + "\n", metadata=metadata), "a" * 64)
        self.assertFalse(any("rm" in c[0] or "prune" in c[0] for c in self.calls))
        for key, value in [("Running", True), ("Paused", True), ("Restarting", True), ("Pid", 42), ("Status", "dead")]:
            changed = copy.deepcopy(metadata)
            changed[0]["State"][key] = value
            with self.subTest(key=key), self.assertRaises(deploy.DeployError):
                self.feature("_recovery_require_quiescent")(["docker", "compose"], self.runner(
                    oneoffs="b" * 64 + "\n", metadata=changed), "a" * 64)

    def test_only_incoming_input_runs_single_transaction_and_effective_acl_is_separate(self):
        sql = "SELECT 'synthetic';"
        run = self.runner()
        self.feature("_recovery_execute_sql")(["docker", "compose"], run, sql)
        command, _, body = self.calls[-1]
        self.assertEqual(body, sql)
        self.assertIn("--single-transaction", command)
        self.assertIn("ON_ERROR_STOP=1", command)
        self.assertIn("/dev/stdin", command)
        self.assertNotIn("/usr/local/share/duora/reconcile-privileges.sql", command)
        self.feature("_recovery_acl_witness")(["docker", "compose"], run)
        witness = self.calls[-1][0][-1]
        self.assertIn("has_any_column_privilege", witness)
        self.assertIn("has_table_privilege", witness)
        self.assertIn("photo_review_appeals", witness)
        self.assertIn("__ef_migrations_history", witness)

    def test_sql_or_acl_unknown_false_and_command_failure_refuse(self):
        for kwargs in [{"acl": "f"}, {"acl": ""}, {"acl": "true"}, {"failure": "recovery_acl_witness"}]:
            with self.subTest(kwargs=kwargs), self.assertRaises(deploy.DeployError):
                self.feature("_recovery_acl_witness")(["docker", "compose"], self.runner(**kwargs))
        with self.assertRaises(deploy.DeployError):
            self.feature("_recovery_execute_sql")(["docker", "compose"], self.runner(failure="--file"), "SELECT 1;")


class RecoveryControlBindingFixture(CurrentV2AttemptFixture):
    feature = RecoveryAssertions.feature

    def setUp(self):
        CurrentV2AttemptFixture.setUp(self)
        self.pin = {"manifest": "/opt/duora/releases/old-current.json", "manifest_sha256": "a" * 64,
            "compose": "/opt/duora/compose.coolify.json", "compose_sha256": "b" * 64,
            "application_uuid": "testresource", "observer_environment": {
                "DUORA_OBSERVER_IMAGE": self.old.images["edge"], "SOURCE_COMMIT": self.old.commit},
            "caddy_sha256": "c" * 64}
        self.pin_raw = (json.dumps(self.pin, indent=2) + "\n").encode()
        self.global_intent = {"project": "duora", "commit": self.new.commit, "status": "importing"}
        self.project_intent = {"release": {"project": "duora", "commit": self.new.commit,
            "duora": self.new.raw}, "deployment_uuid": "deployment123"}

    def baseline(self):
        return self.feature("make_recovery_control_baseline")(self.old, self.pin_raw,
            bindings=self.bindings, receiver_current_sha256="d" * 64, receiver_config_sha256="e" * 64)

    def changed_pin(self):
        pin = copy.deepcopy(self.pin)
        pin["manifest"] = "/opt/duora/releases/" + self.new.release_id + ".json"
        pin["manifest_sha256"] = "f" * 64
        pin["observer_environment"]["DUORA_OBSERVER_IMAGE"] = self.new.images["edge"]
        return (json.dumps(pin, indent=2) + "\n").encode()

    def qualify_control(self, baseline=None, pin_raw=None, **kwargs):
        return self.feature("qualify_recovery_control")(self.baseline() if baseline is None else baseline,
            self.new, self.changed_pin() if pin_raw is None else pin_raw,
            incoming_manifest_sha256="f" * 64, receiver_current_sha256="d" * 64,
            receiver_config_sha256="e" * 64, global_intent=kwargs.get("global_intent", self.global_intent),
            project_intent=kwargs.get("project_intent", self.project_intent))


class RecoveryControlBindingTests(RecoveryControlBindingFixture):
    def test_only_three_pin_fields_can_be_inverted_to_the_enrolled_prior_hash(self):
        baseline = self.baseline()
        control = self.qualify_control(baseline)
        self.assertEqual(set(control), deploy.RECOVERY_CONTROL)
        self.assertEqual(control["incomingPinSha256"], hashlib.sha256(self.changed_pin()).hexdigest())
        self.assertEqual(baseline["pinSha256"], hashlib.sha256(self.pin_raw).hexdigest())
        self.assertNotIn("observer_environment", baseline)
        self.assertNotIn("pin", baseline)

    def test_pin_ancestry_or_observer_metadata_is_not_relaxed_to_restore_a_manifest(self):
        for field, value in [("compose", "/opt/evil.json"), ("caddy_sha256", "f" * 64),
                             ("application_uuid", "foreign")]:
            changed = json.loads(self.changed_pin())
            changed[field] = value
            with self.subTest(field=field), self.assertRaises(deploy.DeployError):
                self.qualify_control(pin_raw=(json.dumps(changed, indent=2) + "\n").encode())
        changed = json.loads(self.changed_pin())
        changed["observer_environment"]["SOURCE_COMMIT"] = "f" * 40
        with self.assertRaises(deploy.DeployError):
            self.qualify_control(pin_raw=(json.dumps(changed, indent=2) + "\n").encode())

    def test_unknown_or_foreign_global_project_intents_or_missing_deployment_refuse(self):
        for change in [dict(self.global_intent, project="goblinshift"), dict(self.global_intent, commit="e" * 40),
                       dict(self.global_intent, status="unknown")]:
            with self.assertRaises(deploy.DeployError): self.qualify_control(global_intent=change)
        for change in [dict(self.project_intent, deployment_uuid=None),
                       dict(self.project_intent, release={"duora": self.old.raw})]:
            with self.assertRaises(deploy.DeployError): self.qualify_control(project_intent=change)

    def test_baseline_cannot_enroll_noncanonical_pin_old_image_or_non_v2(self):
        for raw in [json.dumps(self.pin).encode(), self.pin_raw.replace(b"old-current.json", b"foreign.json")]:
            with self.assertRaises(deploy.DeployError):
                self.feature("make_recovery_control_baseline")(self.old, raw, bindings=self.bindings,
                    receiver_current_sha256="d" * 64, receiver_config_sha256="e" * 64)


class RecoveryApplicationBoundaryFixture(CurrentV2AttemptFixture):
    feature = RecoveryAssertions.feature

    def setUp(self):
        CurrentV2AttemptFixture.setUp(self)
        self.paths = deploy.Paths(self.root / "compose", self.root / "host.env", self.root)
        self.env = self.root / "releases/old-current.env"
        self.events: list[str | tuple[list[str], str | None]] = []

    def perform(self, *, stage="expanded", sql="SELECT 'incoming';", preflight=False, fail=None):
        def event(name):
            self.events.append(name)
            if fail == name: raise deploy.DeployError("Synthetic failure/unknown " + name)
        def run(command, *, capture=False, input_text=None):
            self.events.append((command, input_text))
            return subprocess.CompletedProcess(command, 0, "", "")
        return self.feature("recover_current_v2_application")(
            self.old, self.paths, run, stage=stage, recorded_environment=self.env,
            incoming=self.new, sql_sha256=hashlib.sha256(sql.encode()).hexdigest(),
            authenticate_sql=lambda: (event("authenticate") or sql),
            require_bindings=lambda: event("bindings"), require_quiescent=lambda: event("quiescent"),
            witness_acl=lambda: event("acl"), finish_application=lambda: event("app-outcome"),
            preflight_only=preflight, sleep=lambda _: None)


class RecoveryApplicationBoundaryTests(RecoveryApplicationBoundaryFixture):
    def test_readonly_recovery_checks_bindings_quiescence_without_sql_extract_or_effects(self):
        result = self.perform(preflight=True)
        self.assertEqual(self.events, ["bindings", "quiescent"])
        self.assertEqual(result["status"], "preflight")
        self.assertIs(result["complete"], False)

    def test_expanded_return_is_exact_forward_sql_then_app_only_not_a_new_recorded_release(self):
        with patch.object(deploy, "_require_image_architectures", side_effect=lambda *a: self.events.append("images")), \
                patch.object(deploy, "_wait_ready", side_effect=lambda *a: self.events.append("ready")), \
                patch.object(deploy, "_require_services_running", side_effect=lambda *a: self.events.append("running")), \
                patch.object(deploy, "_require_no_privileged_account_without_mfa"), \
                patch.object(deploy, "runtime_recheck", side_effect=lambda *a: self.events.append("runtime")), \
                patch.object(deploy, "_read_migration_head", return_value=CATALOG[-1]), \
                patch.object(deploy, "_record_release", side_effect=AssertionError("Never rewrite history")), \
                patch.object(deploy, "_mark_current", side_effect=AssertionError("Never rewrite current")):
            result = self.perform()
        commands = [event[0] for event in self.events if type(event) is tuple]
        self.assertTrue(any("ON_ERROR_STOP=1" in c and "--single-transaction" in c for c in commands))
        self.assertTrue(any(c[-6:] == ["--detach", "--wait", "--no-deps", "web", "worker", "edge"] for c in commands))
        self.assertFalse(any(any(word in c for word in ("pull", "migrator", "restart", "stop", "postgres", "backup"))
                             for c in commands if "--file" not in c and "verify" not in c))
        app_index = next(index for index, event in enumerate(self.events)
                         if type(event) is tuple and "up" in event[0])
        self.assertLess(self.events.index("acl"), app_index)
        self.assertEqual(self.events[-1], "app-outcome")
        self.assertEqual(result["status"], "application-recovered-control-pending")
        self.assertIs(result["complete"], False)

    def test_any_pre_effect_failure_never_starts_an_application_or_writes_success(self):
        for failure in ("bindings", "quiescent", "authenticate", "acl"):
            self.events = []
            with patch.object(deploy, "_require_image_architectures"), \
                    patch.object(deploy, "runtime_recheck"), \
                    patch.object(deploy, "_require_no_privileged_account_without_mfa"), \
                    patch.object(deploy, "_read_migration_head", return_value=CATALOG[-1]):
                with self.subTest(failure=failure), self.assertRaises(deploy.DeployError): self.perform(fail=failure)
            self.assertNotIn("app-outcome", self.events)
            self.assertFalse(any(type(event) is tuple and "up" in event[0] for event in self.events))


class RecoveryObservationFixture(RecoveryAssertions):
    feature = RecoveryAssertions.feature

    def setUp(self):
        self.old, self.new = releases()
        self.paths = deploy.Paths(Path("/opt/duora/compose.coolify.json"), Path("/etc/duora/duora.env"), Path("/var/lib/duora"))
        self.data = {}
        self.data[self.paths.state_dir / "current.json"] = self.encode({"releaseId": self.old.release_id,
            "commit": self.old.commit, "deployedAt": "2026-10-25T12:00:00Z"})
        for path, value in [(self.paths.state_dir / "previous.json", b"private previous record\n"),
                (self.paths.state_dir / "releases/old-current.env", b"private recorded environment\n"),
                (self.paths.state_dir / "v2-contract-floor.json", b"private floor\n"),
                (self.paths.state_dir / "executor-runtime-baseline.json", b"sealed runtime profile\n"),
                (self.paths.compose_file, b"reviewed effective compose\n"),
                (self.paths.host_env, b"private host environment\n")]: self.data[path] = value
        self.data[self.paths.state_dir / "releases/old-current.json"] = self.encode(self.old.raw)
        old_opt = Path("/opt/duora/releases/old-current.json")
        new_opt = Path("/opt/duora/releases/new-attempt.json")
        self.data[old_opt], self.data[new_opt] = self.encode(self.old.raw), self.encode(self.new.raw)
        self.pin = {"manifest": str(old_opt), "manifest_sha256": hashlib.sha256(self.data[old_opt]).hexdigest(),
            "compose": str(self.paths.compose_file), "compose_sha256": hashlib.sha256(self.data[self.paths.compose_file]).hexdigest(),
            "application_uuid": "testresource", "observer_environment": {"DUORA_OBSERVER_IMAGE": self.old.images["edge"]}}
        self.data[Path("/var/lib/coolify-release/duora/current.json")] = b"original receiver record\n"
        self.data[Path("/etc/coolify-release/duora.json")] = self.encode({"uuid": "testresource"})
        binding_paths = dict(current=self.paths.state_dir / "current.json", previous=self.paths.state_dir / "previous.json",
            manifest=self.paths.state_dir / "releases/old-current.json", environment=self.paths.state_dir / "releases/old-current.env",
            floor=self.paths.state_dir / "v2-contract-floor.json", runtime=self.paths.state_dir / "executor-runtime-baseline.json",
            compose=self.paths.compose_file, hostEnvironment=self.paths.host_env)
        bindings = {name: hashlib.sha256(self.data[path]).hexdigest() for name, path in binding_paths.items()}
        self.baseline_value = deploy.make_recovery_control_baseline(self.old, self.encode(self.pin), bindings=bindings,
            receiver_current_sha256=hashlib.sha256(self.data[Path("/var/lib/coolify-release/duora/current.json")]).hexdigest(),
            receiver_config_sha256=hashlib.sha256(self.data[Path("/etc/coolify-release/duora.json")]).hexdigest())
        self.baseline_path = self.paths.state_dir / "recovery-control/old-current.json"
        self.data[self.baseline_path] = self.encode(self.baseline_value)
        self.pin["manifest"], self.pin["manifest_sha256"] = str(new_opt), hashlib.sha256(self.data[new_opt]).hexdigest()
        self.pin["observer_environment"]["DUORA_OBSERVER_IMAGE"] = self.new.images["edge"]
        self.data[Path("/etc/duora/coolify-deploy.json")] = self.encode(self.pin)
        self.global_path = Path("/var/lib/coolify-release/pending.json")
        self.project_path = Path("/var/lib/coolify-release/duora/pending.json")
        self.data[self.global_path] = self.encode({"project": "duora", "commit": self.new.commit, "status": "importing"})
        self.data[self.project_path] = self.encode({"release": {"project": "duora", "commit": self.new.commit,
            "duora": self.new.raw}, "deployment_uuid": "deployment123"})

    @staticmethod
    def encode(value): return (json.dumps(value, indent=2) + "\n").encode()

    def observe(self):
        return self.feature("observe_current_v2_recovery")(self.new, self.paths,
            read_root=lambda path: self.data[path], inspect_runtime=lambda current: {
                "profile": {"services": {"postgres": {"id": "1" * 64}}}},
            read_catalog=lambda: CATALOG)


class RecoveryObservationTests(RecoveryObservationFixture):
    def test_observation_binds_real_byte_hashes_and_does_not_emit_private_content(self):
        result = self.observe()
        self.assertEqual(result["current"], self.old)
        self.assertEqual(result["catalog"], CATALOG)
        self.assertEqual(result["postgresId"], "1" * 64)
        self.assertEqual(result["control"]["globalIntentSha256"], hashlib.sha256(self.data[self.global_path]).hexdigest())
        self.assertEqual(result["control"]["projectIntentSha256"], hashlib.sha256(self.data[self.project_path]).hexdigest())
        self.assertNotIn("private recorded environment", repr(result))
        self.assertNotIn("private host environment", repr(result))

    def test_any_preserved_file_changed_or_missing_is_not_enrolled_as_a_new_baseline(self):
        for name in ("previous.json", "releases/old-current.env", "v2-contract-floor.json", "executor-runtime-baseline.json"):
            path = self.paths.state_dir / name
            before = self.data[path]
            self.data[path] = b"unreviewed drift"
            with self.subTest(path=name), self.assertRaises(deploy.DeployError): self.observe()
            self.data[path] = before
        del self.data[self.baseline_path]
        with self.assertRaises(deploy.DeployError): self.observe()

    def test_old_registered_and_observer_manifests_are_both_preserved(self):
        path = Path("/opt/duora/releases/old-current.json")
        self.data[path] = self.encode(self.new.raw)
        with self.assertRaises(deploy.DeployError): self.observe()

    def test_compose_path_cannot_use_a_cli_default_instead_of_the_observer_pin(self):
        self.paths = deploy.Paths(Path("/opt/duora/compose.prod.yml"), self.paths.host_env, self.paths.state_dir)
        self.data[self.paths.compose_file] = b"reviewed effective compose\n"
        with self.assertRaises(deploy.DeployError): self.observe()

    def test_root_reader_refuses_relative_path_without_attempting_io(self):
        with self.assertRaises(deploy.DeployError): self.feature("_recovery_root_read")(Path("relative.json"))

    def test_enrollment_observes_the_original_registered_current_and_actual_pinned_compose_readonly(self):
        pin = copy.deepcopy(self.pin)
        pin["manifest"], pin["manifest_sha256"] = self.baseline_value["manifestPath"], self.baseline_value["manifestSha256"]
        pin["observer_environment"]["DUORA_OBSERVER_IMAGE"] = self.old.images["edge"]
        self.data[Path("/etc/duora/coolify-deploy.json")] = self.encode(pin)
        before = copy.deepcopy(self.data)
        result = self.feature("inspect_current_v2_recovery_control")(
            self.old, self.paths, read_root=lambda p: self.data[p], inspect_runtime=lambda _: {"profile": {}})
        self.assertEqual(result, self.baseline_value)
        self.assertEqual(self.data, before)

    def test_enrollment_cannot_turn_an_incoming_or_drifted_pin_into_a_current_baseline(self):
        with self.assertRaises(deploy.DeployError):
            self.feature("inspect_current_v2_recovery_control")(
                self.old, self.paths, read_root=lambda p: self.data[p], inspect_runtime=lambda _: {"profile": {}})


class RecoveryProducerJournalFixture(CurrentV2AttemptFixture):
    feature = RecoveryAssertions.feature

    attempt = CurrentV2AttemptFixture.attempt

    def setUp(self):
        CurrentV2AttemptFixture.setUp(self)
        self.store = deploy.RecoveryStore(self.root)
        self.store.arm(self.attempt())

    def invoke(self, outcome: int | Exception = 0, phase="migration"):
        self.assertTrue(hasattr(self.store, "run_operation"), "Missing durable producer-operation journal")
        def operation():
            intent = self.root / ("operation-" + phase + "-intent.json")
            self.assertTrue(intent.is_file(), "Durable intent must precede invoking any producer")
            self.assertEqual(intent.stat().st_mode & 0o777, 0o600)
            if isinstance(outcome, Exception): raise outcome
            return subprocess.CompletedProcess(["synthetic"], outcome, "", "")
        return self.store.run_operation(phase, "a" * 64, operation)

    def require_terminal(self):
        self.assertTrue(hasattr(self.store, "require_terminal_operations"), "Missing producer-terminal qualification")
        return self.store.require_terminal_operations()


class RecoveryProducerJournalTests(RecoveryProducerJournalFixture):
    def test_successful_waited_producer_ack_is_durable_and_hash_bound(self):
        self.assertEqual(self.invoke().returncode, 0)
        self.require_terminal()
        receipt = json.loads((self.root / "operation-migration-terminal.json").read_bytes())
        self.assertEqual(receipt["returncode"], 0)
        self.assertIs(receipt["acknowledged"], True)

    def test_timeout_or_unknown_rpc_never_gets_a_terminal_ack_and_does_not_retry(self):
        with self.assertRaises(deploy.DeployError): self.invoke(deploy.DeployError("Synthetic unknown RPC"))
        with self.assertRaises(deploy.DeployError): self.require_terminal()
        before = {p.name: p.read_bytes() for p in self.root.iterdir()}
        with self.assertRaises(deploy.DeployError): self.invoke()
        self.assertEqual({p.name: p.read_bytes() for p in self.root.iterdir()}, before)

    def test_nonzero_cli_exit_does_not_prove_an_uncertain_remote_process_terminated(self):
        self.invoke(1)
        with self.assertRaises(deploy.DeployError): self.require_terminal()

    def test_terminal_tamper_extra_or_boolean_fields_do_not_become_acknowledged(self):
        self.invoke()
        path = self.root / "operation-migration-terminal.json"
        old = path.read_bytes()
        for key, value in [("returncode", True), ("acknowledged", 1), ("intentSha256", "f" * 64), ("extra", "unknown")]:
            receipt = json.loads(old)
            receipt[key] = value
            path.write_text(json.dumps(receipt))
            with self.subTest(key=key), self.assertRaises(deploy.DeployError): self.require_terminal()
        path.write_bytes(old)

    def test_a_new_producer_cannot_begin_while_an_older_one_is_unknown(self):
        with self.assertRaises(deploy.DeployError): self.invoke(deploy.DeployError("unknown"))
        with self.assertRaises(deploy.DeployError): self.invoke(phase="privileges")
        self.assertFalse((self.root / "operation-privileges-intent.json").exists())


class RecoveryEnrollmentFixture(CurrentV2AttemptFixture):
    feature = RecoveryAssertions.feature

    def setUp(self):
        CurrentV2AttemptFixture.setUp(self)
        self.calls = []

    def enroll(self, *, expected=None, changed=False):
        baseline = {"schema": 1, "currentReleaseId": self.old.release_id, "bindings": self.bindings,
            "pinSha256": "a" * 64, "manifestPath": "/opt/duora/releases/old-current.json",
            "manifestSha256": "b" * 64, "edgeImage": self.old.images["edge"], "applicationUuid": "testresource",
            "receiverCurrentSha256": "c" * 64, "receiverConfigSha256": "d" * 64}
        def inspect():
            self.calls.append("inspect")
            return dict(baseline, currentReleaseId="drift") if changed else copy.deepcopy(baseline)
        return self.feature("enroll_current_v2_recovery_control")(
            self.root, inspect=inspect, reviewed_sha256=expected)


class RecoveryEnrollmentTests(RecoveryEnrollmentFixture):
    def test_enrollment_inspection_is_readonly_until_the_exact_projection_hash_is_reviewed(self):
        result = self.enroll()
        self.assertEqual(list(self.root.iterdir()), [])
        self.assertEqual(result["status"], "preflight")
        result = self.enroll(expected=result["sha256"])
        self.assertEqual(result["status"], "enrolled")
        path = self.root / "recovery-control/old-current.json"
        self.assertEqual(path.stat().st_mode & 0o777, 0o600)

    def test_drifted_projection_or_wrong_hash_never_writes_a_baseline(self):
        expected = self.enroll()["sha256"]
        for kwargs in [{"expected": "f" * 64}, {"expected": expected, "changed": True}]:
            with self.subTest(kwargs=kwargs), self.assertRaises(deploy.DeployError): self.enroll(**kwargs)
        self.assertEqual(list(self.root.iterdir()), [])

    def test_already_enrolled_is_observed_without_overwrite_and_foreign_baseline_refuses(self):
        expected = self.enroll()["sha256"]
        self.enroll(expected=expected)
        path = self.root / "recovery-control/old-current.json"
        info, raw = path.stat(), path.read_bytes()
        self.assertEqual(self.enroll(expected=expected)["status"], "already-enrolled")
        self.assertEqual((path.stat().st_ino, path.stat().st_mtime_ns, path.read_bytes()), (info.st_ino, info.st_mtime_ns, raw))
        path.write_text('{}')
        with self.assertRaises(deploy.DeployError): self.enroll(expected=expected)


class RecoveryForwardAdmissionFixture(CurrentV2AttemptFixture):
    feature = RecoveryAssertions.feature

    def setUp(self):
        CurrentV2AttemptFixture.setUp(self)
        self.paths = deploy.Paths(self.root / "compose", self.root / "host.env", self.root)
        self.sql = (Path(__file__).parent / "fixtures/recovery-48-privileges.sql").read_text()
        self.context = {"current": self.old, "bindings": self.bindings, "control": self.control,
                        "catalog": CATALOG[:35], "postgresId": "1" * 64}

    def arm(self, **kwargs):
        with patch.object(deploy, "_recovery_require_cached_images", return_value={}, create=True):
            return self.feature("arm_current_v2_deploy")(
                kwargs.get("incoming", self.new), self.paths, kwargs.get("sql", self.sql),
                observe=kwargs.get("observe", lambda: self.context))

    def pipeline(self, *, fail_on=None):
        records = self.root / "releases"
        records.mkdir(exist_ok=True)
        (records / "old-current.json").write_text(json.dumps(self.old.raw))
        (records / "old-current.env").write_text("synthetic old environment\n")
        (self.root / "current.json").write_text(json.dumps({"releaseId": self.old.release_id, "commit": self.old.commit}))
        (self.root / "v2-contract-floor.json").write_text(json.dumps({"schema": 1, "minimumContract": "v2",
            "releaseId": self.old.release_id, "commit": self.old.commit, "sqlSha256": "a" * 64}))
        (self.root / "v2-contract-floor.json").chmod(0o600)
        run = FakeRunner(pre_head=CATALOG[34], migration_head=CATALOG[-1], fail_on=fail_on)
        real_arm = deploy.arm_current_v2_deploy
        with patch.object(deploy, "runtime_preflight", return_value=self.sql), \
                patch.object(deploy, "runtime_recheck"), \
                patch.object(deploy, "_recovery_require_cached_images", return_value={}), \
                patch.object(deploy, "arm_current_v2_deploy", side_effect=lambda incoming, paths, sql, **kwargs:
                    real_arm(incoming, paths, sql, observe=lambda: self.context, **kwargs)), \
                patch.object(deploy, "finish_current_v2_forward", create=True) as finish:
            deploy.deploy(self.new, self.paths, run, sleep=lambda _: None)
            self.assertEqual(finish.call_count, 1)
        return run


class RecoveryForwardAdmissionTests(RecoveryForwardAdmissionFixture):
    def test_reviewed_pair_arms_a_durable_intention_before_any_worker_or_ddl_operation(self):
        store = self.arm()
        self.assertIsInstance(store, deploy.RecoveryStore)
        attempt = store.load()
        self.assertEqual(attempt["current"], self.old.raw)
        self.assertEqual(attempt["incoming"], self.new.raw)
        self.assertEqual(attempt["sqlSha256"], deploy.RECOVERY_SQL_48_SHA256)
        self.assertEqual(sorted(p.name for p in store.directory.iterdir()),
                         sorted(["pending.json", "attempt-" + attempt["nonce"] + ".json"]))

    def test_missing_unreviewed_control_payload_or_noninitial_catalog_cannot_arm(self):
        for failure in ("control", "payload", "catalog"):
            if failure == "catalog": self.context["catalog"] = CATALOG
            def observation(): raise deploy.DeployError("No qualified control baseline")
            kwargs = {"observe": observation} if failure == "control" else ({"sql": self.sql + "--drift"} if failure == "payload" else {})
            with self.subTest(failure=failure), self.assertRaises(deploy.DeployError): self.arm(**kwargs)
        self.assertEqual(list(self.root.iterdir()), [])

    def test_existing_unknown_intent_is_not_replaced_by_another_attempt(self):
        store = self.arm()
        before = {p.name: p.read_bytes() for p in store.directory.iterdir()}
        with self.assertRaises(deploy.DeployError): self.arm()
        self.assertEqual({p.name: p.read_bytes() for p in store.directory.iterdir()}, before)

    def test_other_existing_release_contracts_do_not_create_a_recovery_control_baseline(self):
        ordinary = parse(dict(valid_manifest(), contractVersion="v2"))
        self.assertIsNone(self.arm(incoming=ordinary, observe=lambda: self.fail("No new recovery enrollment for another release")))
        self.assertEqual(list(self.root.iterdir()), [])

    def test_actual_deploy_arms_and_journals_all_four_producers_before_marking_the_new_current(self):
        self.pipeline()
        store = deploy.RecoveryStore(self.root / "current-v2-recovery")
        store.require_terminal_operations()
        for name in ("worker-stop", "migration", "privileges", "application-up"):
            self.assertTrue((store.directory / ("operation-" + name + "-intent.json")).is_file())
            self.assertTrue((store.directory / ("operation-" + name + "-terminal.json")).is_file())
        self.assertEqual(json.loads((self.root / "current.json").read_bytes())["releaseId"], self.new.release_id)

    def test_failed_sql_keeps_the_old_current_and_blocks_another_deploy_before_any_pull(self):
        with self.assertRaises(deploy.DeployError): self.pipeline(fail_on="--file /dev/stdin")
        self.assertEqual(json.loads((self.root / "current.json").read_bytes())["releaseId"], self.old.release_id)
        store = deploy.RecoveryStore(self.root / "current-v2-recovery")
        with self.assertRaises(deploy.DeployError): store.require_terminal_operations()
        second = FakeRunner()
        with patch.object(deploy, "runtime_preflight", side_effect=AssertionError("Refuse before qualification/pull")):
            with self.assertRaises(deploy.DeployError): deploy.deploy(self.new, self.paths, second)
        self.assertEqual(second.commands, [])


class RecoveryCachedImageFixture(RecoveryAssertions):
    feature = RecoveryAssertions.feature

    def setUp(self):
        old, _ = releases()
        raw = copy.deepcopy(old.raw)
        raw["images"]["postgres"] = deploy._guard.LEGACY_DIGESTS[old.architecture]
        self.current = parse(raw)
        self.metadata = {reference: {"Id": "sha256:" + str(index + 1) * 64, "Os": "linux",
            "Architecture": self.current.architecture, "RepoDigests": [reference], "Config": {"Labels": {
                "org.opencontainers.image.source": "https://github.com/BipoXDXD/duora",
                "org.opencontainers.image.title": "duora-" + role,
                "org.opencontainers.image.revision": self.current.commit}}}
            for index, (role, reference) in enumerate(self.current.images.items())}
        self.calls = []

    def capture(self, command):
        self.calls.append(command)
        self.assertEqual(command[:3], ["docker", "image", "inspect"])
        return json.dumps(self.metadata[command[-1]]).encode()


class RecoveryCachedImageTests(RecoveryCachedImageFixture):
    def test_all_five_exact_local_roles_are_bound_without_pull_and_pg_does_not_need_a_new_revision_label(self):
        del self.metadata[self.current.images["postgres"]]["Config"]["Labels"]["org.opencontainers.image.revision"]
        result = self.feature("_recovery_require_cached_images")(self.current, capture=self.capture)
        self.assertEqual(set(result), set(deploy.IMAGE_NAMES))
        self.assertEqual(len(self.calls), 5)
        self.assertNotEqual(result["web"], self.current.images["web"].split("@")[-1])

    def test_unbound_platform_digest_role_source_or_revision_refuses(self):
        reference = self.current.images["web"]
        old = copy.deepcopy(self.metadata[reference])
        for field, value in [("Architecture", "amd64"), ("Os", "windows"), ("RepoDigests", []), ("Id", "mutable")]:
            self.metadata[reference] = dict(old, **{field: value})
            with self.subTest(field=field), self.assertRaises(deploy.DeployError):
                self.feature("_recovery_require_cached_images")(self.current, capture=self.capture)

        for field, value in [("org.opencontainers.image.title", "duora-worker"),
                             ("org.opencontainers.image.revision", "f" * 40),
                             ("org.opencontainers.image.source", "https://example.invalid/foreign")]:
            changed = copy.deepcopy(old)
            changed["Config"]["Labels"][field] = value
            self.metadata[reference] = changed
            with self.subTest(field=field), self.assertRaises(deploy.DeployError):
                self.feature("_recovery_require_cached_images")(self.current, capture=self.capture)


class RecoveryForwardCompletionFixture(CurrentV2AttemptFixture):
    feature = RecoveryAssertions.feature

    def setUp(self):
        CurrentV2AttemptFixture.setUp(self)
        self.paths = deploy.Paths(self.root / "compose", self.root / "host.env", self.root)
        self.old_current = b'{"releaseId":"old-current","commit":"synthetic-old-record"}\n'
        files = {"current.json": self.old_current, "previous.json": b'old previous\n',
            "releases/old-current.json": json.dumps(self.old.raw).encode(), "releases/old-current.env": b"original environment\n",
            "v2-contract-floor.json": b"original floor\n", "executor-runtime-baseline.json": b"original runtime\n",
            "compose": b"original compose\n", "host.env": b"original private environment\n"}
        for name, raw in files.items():
            path = self.root / name
            path.parent.mkdir(exist_ok=True)
            path.write_bytes(raw)
            path.chmod(0o600)
        self.bindings = {name: hashlib.sha256((path).read_bytes()).hexdigest()
            for name, path in deploy._recovery_preservation_paths(self.old, self.paths).items()}
        self.store = deploy.RecoveryStore(self.root / "current-v2-recovery")
        self.attempt_value = deploy.make_recovery_attempt(self.old, self.new, sql_sha256=deploy.RECOVERY_SQL_48_SHA256,
            bindings=self.bindings, control=self.control, nonce="1" * 32)
        self.store.arm(self.attempt_value)
        for phase in ("worker-stop", "migration", "privileges", "application-up"):
            self.store.run_operation(phase, "a" * 64, lambda: subprocess.CompletedProcess(["synthetic"], 0, "", ""))
        (self.root / "previous.json").write_bytes(self.old_current)
        (self.root / "current.json").write_text(json.dumps({"releaseId": self.new.release_id,
            "commit": self.new.commit, "deployedAt": "2026-10-25T15:00:00+00:00"}, indent=2) + "\n")
        (self.root / "releases/new-attempt.json").write_text(json.dumps(self.new.raw, indent=2, sort_keys=True) + "\n")
        (self.root / "releases/new-attempt.env").write_text(
            "\n".join([f"DUORA_{name.upper()}_IMAGE={reference}" for name, reference in self.new.images.items()]
                      + ["DUORA_RELEASE_ID=" + self.new.release_id]) + "\n")
        self.old_bytes = {name: (self.root / name).read_bytes() for name in files if name not in {"current.json", "previous.json"}}

    def finish(self):
        return self.feature("finish_current_v2_forward")(self.store, self.new, self.paths, read_root=lambda p: p.read_bytes())


class RecoveryForwardCompletionTests(RecoveryForwardCompletionFixture):
    def test_successful_forward_archives_only_its_own_attempt_and_preserves_old_history_and_all_others(self):
        result = self.finish()
        self.assertEqual(result["status"], "forward-recorded")
        self.assertFalse(self.store.directory.exists())
        archived = self.root / "current-v2-recovery-history" / self.attempt_value["nonce"]
        self.assertTrue((archived / "pending.json").is_file())
        self.assertTrue((archived / "forward-outcome.json").is_file())
        self.assertEqual({name: (self.root / name).read_bytes() for name in self.old_bytes}, self.old_bytes)
        deploy.require_no_current_v2_recovery_intent(self.paths)

    def test_old_preservation_drift_or_unknown_new_current_never_creates_a_forward_outcome(self):
        for path in (self.root / "releases/old-current.env", self.root / "current.json"):
            before = path.read_bytes()
            path.write_bytes(b"unknown drift")
            with self.subTest(path=path.name), self.assertRaises(deploy.DeployError): self.finish()
            self.assertFalse((self.store.directory / "forward-outcome.json").exists())
            path.write_bytes(before)

    def test_archive_failure_keeps_its_complete_outcome_and_explicit_completion_reuses_it(self):
        with patch.object(deploy.os, "rename", side_effect=OSError("Synthetic archive interruption")):
            with self.assertRaises(deploy.DeployError): self.finish()
        outcome = self.store.directory / "forward-outcome.json"
        self.assertTrue(outcome.is_file())
        old = outcome.read_bytes()
        result = self.finish()
        self.assertEqual(result["status"], "forward-recorded")
        self.assertEqual((self.root / "current-v2-recovery-history" / self.attempt_value["nonce"] / "forward-outcome.json").read_bytes(), old)


class RecoveryCliBoundaryFixture(RecoveryAssertions):
    pass


class RecoveryCliBoundaryTests(RecoveryCliBoundaryFixture):
    def test_real_lock_order_and_executor_handoff_do_not_release_host_or_project_locks(self):
        import fcntl
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        root = Path(directory.name)
        ingress = root / "ingress"
        (ingress / "duora").mkdir(parents=True)
        state = root / "state"
        state.mkdir()
        lock_paths = [ingress / "host.lock", ingress / "duora/release.lock", state / ".deploy.lock"]
        for path in lock_paths: path.touch(mode=0o600)
        self.assertTrue(hasattr(deploy, "recovery_administrative_locks"), "Missing three real administrative locks")
        paths = deploy.Paths(root / "compose", root / "env", state)
        with patch.object(deploy, "_recovery_root_read", side_effect=lambda path: path.read_bytes()):
            with deploy.recovery_administrative_locks(paths, ingress=ingress) as lease:
                for path in lock_paths:
                    with path.open() as handle, self.assertRaises(BlockingIOError):
                        fcntl.flock(handle, fcntl.LOCK_EX | fcntl.LOCK_NB)
                lease.release_executor()
                with lock_paths[-1].open() as handle:
                    fcntl.flock(handle, fcntl.LOCK_EX | fcntl.LOCK_NB)
                    with self.assertRaises(deploy.DeployError): lease.reacquire_executor()
                lease.reacquire_executor()
                for path in lock_paths[:2]:
                    with path.open() as handle, self.assertRaises(BlockingIOError):
                        fcntl.flock(handle, fcntl.LOCK_EX | fcntl.LOCK_NB)

    def test_explicit_administrative_modes_use_all_three_locks_and_the_reviewed_handler(self):
        from contextlib import nullcontext, redirect_stdout, redirect_stderr
        import io
        old, _ = releases()
        for flag, mode in [("--enroll-current-v2-recovery-control", "enroll"),
                           ("--recover-current-v2", "recover"), ("--finish-current-v2-forward", "finish-forward")]:
            with self.subTest(mode=mode), \
                    patch.object(deploy, "read_manifest", return_value=json.dumps(old.raw)), \
                    patch.object(Path, "read_text", return_value="DUORA_IMAGE_PREFIX=ghcr.io/bipoxdxd/duora-\n"), \
                    patch.object(deploy, "preflight"), \
                    patch.object(deploy, "_require_recovery_administrator", create=True) as root_gate, \
                    patch.object(deploy, "recovery_administrative_locks", return_value=nullcontext(lambda: None), create=True) as locks, \
                    patch.object(deploy, "recovery_administrative_entry", return_value={"status": "preflight"}, create=True) as entry, \
                    patch.object(deploy, "deploy_lock", side_effect=AssertionError("Do not acquire the executor lock twice")), \
                    redirect_stdout(io.StringIO()), redirect_stderr(io.StringIO()):
                try:
                    result = deploy.main(["--manifest", "/synthetic/current.json", "--compose-file", "/synthetic/pinned-compose.json",
                                          "--preflight-only", flag])
                except SystemExit:
                    self.fail("Missing explicit current-v2 administrative CLI mode: " + mode)
                self.assertEqual(result, 0)
                self.assertEqual(locks.call_count, 1)
                self.assertEqual(root_gate.call_count, 1)
                self.assertEqual(entry.call_args.kwargs["mode"], mode)
                self.assertIs(entry.call_args.kwargs["readonly"], True)

    def test_an_administrative_mode_cannot_bypass_backup_or_combine_with_inventory(self):
        from contextlib import redirect_stderr
        import io
        for extra in ("--accept-missing-backup-check", "--inventory-only"):
            with self.subTest(extra=extra), redirect_stderr(io.StringIO()), self.assertRaises(SystemExit) as error:
                deploy.main(["--manifest", "/synthetic/current.json", "--recover-current-v2", extra])
            self.assertEqual(error.exception.code, 2)

    def test_admin_gate_refuses_a_forced_receiver_or_delegated_sudo_caller(self):
        self.assertTrue(hasattr(deploy, "_require_recovery_administrator"), "Missing administrative root gate")
        for extra in ({"SSH_ORIGINAL_COMMAND": "release " + "a" * 40}, {"SUDO_UID": "1001"}):
            with self.subTest(extra=extra), patch.object(deploy.os, "geteuid", return_value=0), \
                    patch.object(deploy.os, "getuid", return_value=0), patch.dict(os.environ, extra, clear=True):
                with self.assertRaises(deploy.DeployError): deploy._require_recovery_administrator()


class RecoveryForwardReadonlyCompletionFixture(RecoveryForwardCompletionFixture):
    feature = RecoveryAssertions.feature

    setUp = RecoveryForwardCompletionFixture.setUp


class RecoveryForwardReadonlyCompletionTests(RecoveryForwardReadonlyCompletionFixture):
    def test_readonly_forward_completion_checks_but_never_writes_or_archives(self):
        import inspect
        function = self.feature("finish_current_v2_forward")
        self.assertIn("readonly", inspect.signature(function).parameters)
        before = {p.name: p.read_bytes() for p in self.store.directory.iterdir()}
        result = function(self.store, self.new, self.paths, read_root=lambda p: p.read_bytes(), readonly=True)
        self.assertEqual(result["status"], "preflight")
        self.assertEqual({p.name: p.read_bytes() for p in self.store.directory.iterdir()}, before)
        self.assertFalse((self.root / "current-v2-recovery-history").exists())


class RecoveryAdministrativeAdmissionFixture(CurrentV2AttemptFixture, RecoveryObservationFixture):
    feature = RecoveryAssertions.feature

    def setUp(self):
        from types import SimpleNamespace
        CurrentV2AttemptFixture.setUp(self)
        self.temporary_root = self.root
        RecoveryObservationFixture.setUp(self)
        self.store = deploy.RecoveryStore(self.temporary_root / "current-v2-recovery")
        context = deploy.observe_current_v2_recovery(self.new, self.paths, read_root=lambda p: self.data[p],
            inspect_runtime=lambda _: {"profile": {"services": {"postgres": {"id": "1" * 64}}}}, read_catalog=lambda: CATALOG)
        attempt = deploy.make_recovery_attempt(self.old, self.new, sql_sha256=deploy.RECOVERY_SQL_48_SHA256,
            bindings=context["bindings"], control=context["control"], nonce="1" * 32)
        self.store.arm(attempt)
        for phase in ("worker-stop", "migration"):
            self.store.run_operation(phase, "a" * 64, lambda: subprocess.CompletedProcess(["synthetic"], 0, "", ""))
        self.events = []
        self.receiver = SimpleNamespace(require_idle=lambda uuid: self.events.append(("idle", uuid)))

    encode = staticmethod(RecoveryObservationFixture.encode)

    def entry(self, *, readonly=True, fail_on=None, runtime_apps=None):
        from contextlib import ExitStack
        self.last_run = FakeRunner(pre_head=CATALOG[-1], migration_head=CATALOG[-1], fail_on=fail_on)
        with ExitStack() as patches:
            patches.enter_context(patch.object(deploy, "_recovery_root_read", side_effect=lambda p: self.data[p]))
            patches.enter_context(patch.object(deploy, "_recovery_verified_receiver", return_value=self.receiver, create=True))
            patches.enter_context(patch.object(deploy, "_recovery_other_ingress_idle", side_effect=lambda: self.events.append("others-idle"), create=True))
            patches.enter_context(patch.object(deploy, "RecoveryStore", return_value=self.store))
            patches.enter_context(patch.object(deploy, "runtime_recheck", return_value={"profile": {"services": {"postgres": {"id": "1" * 64}}}}))
            patches.enter_context(patch.object(deploy, "_query", return_value=json.dumps(CATALOG)))
            patches.enter_context(patch.object(deploy, "_read_migration_head", return_value=CATALOG[-1]))
            patches.enter_context(patch.object(deploy, "_recovery_require_cached_images", return_value={name: "sha256:" + "1" * 64 for name in deploy.IMAGE_NAMES}))
            actual_apps = {role: {"id": str(index + 2) * 64, "imageId": "sha256:" + "1" * 64,
                "reference": self.old.images[role], "commit": self.old.commit,
                "startedAt": "2026-10-25T15:00:00Z", "restartCount": 0}
                for index, role in enumerate(("web", "worker", "edge"))}
            patches.enter_context(patch.object(deploy, "_recovery_require_running_applications",
                return_value=actual_apps if runtime_apps is None else runtime_apps))
            patches.enter_context(patch.object(deploy, "_recovery_require_quiescent", side_effect=lambda *a: self.events.append("quiescent")))
            patches.enter_context(patch.object(deploy, "_recovery_acl_witness", side_effect=lambda *a: self.events.append("acl")))
            patches.enter_context(patch.object(deploy, "_record_release", side_effect=AssertionError("Never rewrite successful historical releases")))
            patches.enter_context(patch.object(deploy, "_mark_current", side_effect=AssertionError("Never fabricate current success")))
            if readonly:
                patches.enter_context(patch.object(deploy._payload, "extract_sql", side_effect=AssertionError("Readonly must not create a SQL extraction container")))
            else:
                patches.enter_context(patch.object(deploy._payload, "extract_sql", return_value=(Path(__file__).parent / "fixtures/recovery-48-privileges.sql").read_text()))
            return self.feature("recovery_administrative_entry")(self.old, self.paths,
                (lambda *a, **k: self.fail("Readonly must not stop/start/reconcile or verify backups")) if readonly else self.last_run,
                mode="recover", readonly=readonly, reviewed_sha256=None, lease=None)

    def successful_application_recovery(self):
        try: return self.entry(readonly=False)
        except deploy.DeployError: self.fail("Explicit admitted application recovery is not implemented")


class RecoveryAdministrativeAdmissionTests(RecoveryAdministrativeAdmissionFixture):
    def test_exact_administrative_readonly_recovery_preserves_all_evidence_and_authenticates_no_payload(self):
        before = {p.name: p.read_bytes() for p in self.store.directory.iterdir()}
        result = self.entry()
        self.assertEqual(result["status"], "preflight")
        self.assertIs(result["complete"], False)
        self.assertIs(result["payloadAuthenticated"], False)
        self.assertEqual({p.name: p.read_bytes() for p in self.store.directory.iterdir()}, before)
        self.assertIn(("idle", "testresource"), self.events)
        self.assertIn("quiescent", self.events)

    def test_uncertain_prior_producer_blocks_the_administrative_path_before_any_control_or_payload_action(self):
        def uncertain(): raise deploy.DeployError("Synthetic unknown prior SQL RPC")
        with self.assertRaises(deploy.DeployError): self.store.run_operation("privileges", "a" * 64, uncertain)
        with self.assertRaises(deploy.DeployError): self.entry()
        self.assertEqual(self.events, [])

    def test_explicit_application_recovery_journals_wal_exact_incoming_sql_and_app_only_without_rewriting_history(self):
        before = copy.deepcopy(self.data)
        old_attempt_files = {p.name: p.read_bytes() for p in self.store.directory.iterdir()}
        result = self.successful_application_recovery()
        self.assertEqual(result["status"], "application-recovered-control-pending")
        self.assertIs(result["complete"], False)
        self.assertEqual(self.data, before)
        for name, body in old_attempt_files.items(): self.assertEqual((self.store.directory / name).read_bytes(), body)
        self.assertTrue((self.store.directory / "application-outcome.json").is_file())
        phases = [p.name for p in self.store.directory.iterdir() if p.name.startswith("operation-recovery-")]
        self.assertEqual(len(phases), 6)
        self.assertTrue(any("--single-transaction" in c for c in self.last_run.commands))
        self.assertTrue(any(c[-6:] == ["--detach", "--wait", "--no-deps", "web", "worker", "edge"] for c in self.last_run.commands))
        self.assertFalse(any(any(flag in c for flag in ("pull", "restart", "prune", "migrator")) for c in self.last_run.commands))

    def test_a_known_application_outcome_is_reobserved_without_replaying_sql_backup_or_app_up(self):
        self.successful_application_recovery()
        before = {p.name: p.read_bytes() for p in self.store.directory.iterdir()}
        result = self.successful_application_recovery()
        self.assertIs(result["complete"], False)
        self.assertIs(result["reusedApplicationOutcome"], True)
        self.assertEqual({p.name: p.read_bytes() for p in self.store.directory.iterdir()}, before)
        self.assertFalse(any("verify" in c or "up" in c or "--single-transaction" in c for c in self.last_run.commands))

    def test_recovery_sql_failure_preserves_unknown_ack_and_never_starts_apps_or_seals_success(self):
        with self.assertRaises(deploy.DeployError): self.entry(readonly=False, fail_on="--file /dev/stdin")
        self.assertFalse((self.store.directory / "application-outcome.json").exists())
        self.assertFalse(any("up" in c for c in self.last_run.commands))
        with self.assertRaises(deploy.DeployError): self.store.require_terminal_operations()

    def test_application_outcome_records_the_actual_running_containers_not_only_cached_images(self):
        self.successful_application_recovery()
        result = json.loads((self.store.directory / "application-outcome.json").read_bytes())
        self.assertIn("runtimeApps", result)
        self.assertEqual(set(result["runtimeApps"]), {"web", "worker", "edge"})
        self.assertEqual(result["runtimeApps"]["web"]["id"], "2" * 64)
        self.assertEqual(result["runtimeApps"]["web"]["commit"], self.old.commit)

    def test_reused_application_outcome_refuses_container_identity_drift_without_replaying_any_producer(self):
        self.successful_application_recovery()
        result = json.loads((self.store.directory / "application-outcome.json").read_bytes())
        self.assertIn("runtimeApps", result)
        changed = result["runtimeApps"]
        changed["web"]["id"] = "f" * 64
        with self.assertRaises(deploy.DeployError): self.entry(readonly=False, runtime_apps=changed)
        self.assertFalse(any("up" in c or "verify" in c or "--single-transaction" in c for c in self.last_run.commands))


class RecoveryEnvironmentProjectionFixture(RecoveryAssertions):
    feature = RecoveryAssertions.feature

    def setUp(self):
        self.old, self.new = releases()
        self.rows = [{"uuid": "knownvar", "key": "DUORA_OBSERVER_IMAGE", "is_preview": False,
                      "is_literal": True, "is_shown_once": True},
                     {"key": "SYNTHETIC_OTHER_KEY", "is_preview": False, "value": "synthetic-secret-value"}]

    def project(self, rows=None):
        return self.feature("_recovery_environment_projection")(self.rows if rows is None else rows,
            expected_images={self.old.images["edge"], self.new.images["edge"]})


class RecoveryEnvironmentProjectionTests(RecoveryEnvironmentProjectionFixture):
    def test_a_shown_once_value_is_not_requested_unhidden_or_invented(self):
        result = self.project()
        self.assertEqual(result["valueVisibility"], "hidden")
        self.assertEqual(set(result), {"targetSha256", "othersSha256", "valueVisibility"})
        self.assertNotIn("synthetic-secret-value", repr(result))
        self.assertNotIn("SYNTHETIC_OTHER_KEY", repr(result))

    def test_visible_image_if_present_must_match_an_exact_allowed_ref_and_other_rows_are_hash_bound(self):
        before = self.project()
        self.rows[0]["value"] = self.old.images["edge"]
        self.assertEqual(self.project()["valueVisibility"], "visible")
        self.rows[1]["value"] = "different-private-value"
        self.assertNotEqual(self.project()["othersSha256"], before["othersSha256"])
        self.rows[0]["value"] = "foreign-or-secret"
        with self.assertRaises(deploy.DeployError): self.project()

    def test_missing_duplicate_preview_only_or_changed_privacy_flags_refuse(self):
        for rows in [[], [self.rows[1]], self.rows + [self.rows[0]],
                     [dict(self.rows[0], is_preview=True)], [dict(self.rows[0], is_literal=False)],
                     [dict(self.rows[0], is_shown_once=False)]]:
            with self.subTest(rows=len(rows)), self.assertRaises(deploy.DeployError): self.project(rows)

    def test_nonlist_ambiguous_types_and_budget_excess_refuse(self):
        for rows in [{}, [dict(self.rows[0], is_preview=0)], self.rows * 65]:
            with self.subTest(kind=type(rows).__name__), self.assertRaises(deploy.DeployError): self.project(rows)


class RecoveryRunningApplicationIdentityFixture(RecoveryAssertions):
    feature = RecoveryAssertions.feature

    def setUp(self):
        self.current, _ = releases()
        self.paths = deploy.Paths(Path("/synthetic/compose"), Path("/synthetic/host.env"), Path("/synthetic/state"))
        self.prefix = deploy._compose_command(self.paths, self.paths.state_dir / "releases/old-current.env")
        self.ids = {role: "sha256:" + str(index + 4) * 64 for index, role in enumerate(("web", "worker", "edge"))}
        self.containers = {role: {"Id": str(index + 1) * 64, "Image": self.ids[role], "RestartCount": 0,
            "Config": {"Image": self.current.images[role], "Labels": {"com.docker.compose.project": "duora",
                "com.docker.compose.service": role, "com.docker.compose.oneoff": "False",
                "org.opencontainers.image.source": "https://github.com/BipoXDXD/duora",
                "org.opencontainers.image.revision": self.current.commit, "org.opencontainers.image.title": "duora-" + role}},
            "State": {"Status": "running", "Running": True, "Restarting": False, "Paused": False,
                "OOMKilled": False, "StartedAt": "2026-10-25T15:00:00Z"}}
            for index, role in enumerate(("web", "worker", "edge"))}
        self.calls = []

    def capture(self, command):
        self.calls.append(command)
        if command[:-1] == self.prefix + ["ps", "--all", "--quiet"]:
            return (self.containers[command[-1]]["Id"] + "\n").encode()
        self.assertEqual(command[:3], ["docker", "container", "inspect"])
        record = next(v for v in self.containers.values() if v["Id"] == command[-1])
        return json.dumps(record).encode()

    def observe(self):
        return self.feature("_recovery_require_running_applications")(
            self.current, self.paths, self.ids, capture=self.capture)


class RecoveryRunningApplicationIdentityTests(RecoveryRunningApplicationIdentityFixture):
    def test_each_running_app_is_the_exact_recorded_role_image_source_revision_and_container_id(self):
        result = self.observe()
        self.assertEqual(set(result), {"web", "worker", "edge"})
        for role in result:
            self.assertEqual(result[role]["id"], self.containers[role]["Id"])
            self.assertEqual(result[role]["imageId"], self.ids[role])
            self.assertEqual(result[role]["commit"], self.current.commit)
        self.assertFalse(any(any(flag in c for flag in ("up", "stop", "restart", "pull")) for c in self.calls))

    def test_ready_looking_foreign_wrong_image_or_revision_restarted_and_oom_apps_refuse(self):
        old = copy.deepcopy(self.containers["web"])
        for field, value in [("Image", "sha256:" + "f" * 64), ("RestartCount", 1), ("RestartCount", True)]:
            self.containers["web"] = dict(old, **{field: value})
            with self.subTest(field=field), self.assertRaises(deploy.DeployError): self.observe()
        for field, value in [("OOMKilled", True), ("Running", False), ("Paused", True), ("Restarting", True)]:
            changed = copy.deepcopy(old); changed["State"][field] = value; self.containers["web"] = changed
            with self.subTest(field=field), self.assertRaises(deploy.DeployError): self.observe()
        for field, value in [("com.docker.compose.project", "foreign"),
                             ("com.docker.compose.service", "worker"),
                             ("org.opencontainers.image.revision", "f" * 40)]:
            changed = copy.deepcopy(old); changed["Config"]["Labels"][field] = value; self.containers["web"] = changed
            with self.subTest(field=field), self.assertRaises(deploy.DeployError): self.observe()


class RecoveryPinInverseFixture(RecoveryControlBindingFixture):
    feature = RecoveryAssertions.feature

    def setUp(self):
        RecoveryControlBindingFixture.setUp(self)
        images = dict(self.new.images)
        images["edge"] = "ghcr.io/bipoxdxd/duora-edge@sha256:" + "b" * 64
        self.new = parse(dict(self.new.raw, images=images))

    baseline = RecoveryControlBindingFixture.baseline

    changed_pin = RecoveryControlBindingFixture.changed_pin

    def project(self, raw):
        return self.feature("_recovery_pin_inverse")(self.old, self.new, self.baseline(), raw,
            incoming_manifest_sha256="f" * 64, admitted_incoming_pin_sha256=hashlib.sha256(self.changed_pin()).hexdigest())


class RecoveryPinInverseTests(RecoveryPinInverseFixture):
    def test_incoming_pin_inverts_exactly_three_fields_and_the_old_pin_can_be_recognized_without_rewriting(self):
        incoming = self.changed_pin()
        result = self.project(incoming)
        self.assertEqual(result["stage"], "incoming")
        self.assertEqual(result["restoredRaw"], self.pin_raw)
        self.assertEqual(result["virtualIncomingRaw"], incoming)
        result = self.project(self.pin_raw)
        self.assertEqual(result["stage"], "restored")
        self.assertEqual(result["restoredRaw"], self.pin_raw)
        self.assertEqual(result["virtualIncomingRaw"], incoming)

    def test_foreign_pin_or_other_changed_field_cannot_be_restored_from_a_snapshot(self):
        for field, value in [("application_uuid", "foreign"), ("compose", "/unknown/compose"), ("caddy_sha256", "f" * 64)]:
            changed = json.loads(self.changed_pin()); changed[field] = value
            with self.subTest(field=field), self.assertRaises(deploy.DeployError):
                self.project((json.dumps(changed, indent=2) + "\n").encode())


class RecoveryAtomicPinFixture(RecoveryAssertions):
    feature = RecoveryAssertions.feature

    def setUp(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.root = Path(directory.name)
        self.path = self.root / "coolify-deploy.json"
        self.before = b'{"reviewed":"incoming"}\n'
        self.after = b'{"reviewed":"restored"}\n'
        self.path.write_bytes(self.before); self.path.chmod(0o600)

    def apply(self):
        with patch.object(deploy, "_recovery_root_read", side_effect=lambda p: p.read_bytes()):
            return self.feature("_recovery_apply_pin")(self.path,
                expected_current_sha256=hashlib.sha256(self.before).hexdigest(),
                restored_raw=self.after, nonce="1" * 32)


class RecoveryAtomicPinTests(RecoveryAtomicPinFixture):
    def test_own_exclusive_temp_is_synced_then_replaces_only_the_exact_pin_and_repeated_observation_does_not_write(self):
        self.assertEqual(self.apply()["status"], "restored")
        self.assertEqual(self.path.read_bytes(), self.after)
        self.assertEqual(self.path.stat().st_mode & 0o777, 0o600)
        info = self.path.stat()
        self.assertEqual(self.apply()["status"], "already-restored")
        self.assertEqual((self.path.stat().st_ino, self.path.stat().st_mtime_ns), (info.st_ino, info.st_mtime_ns))

    def test_foreign_destination_or_existing_temp_is_not_overwritten_or_removed(self):
        self.path.write_bytes(b"foreign pin")
        with self.assertRaises(deploy.DeployError): self.apply()
        self.assertEqual(self.path.read_bytes(), b"foreign pin")
        self.path.write_bytes(self.before)
        foreign = self.root / (".duora-recovery-pin-" + "1" * 32 + ".tmp")
        foreign.write_bytes(b"foreign temp")
        with self.assertRaises(deploy.DeployError): self.apply()
        self.assertEqual(foreign.read_bytes(), b"foreign temp")
        self.assertEqual(self.path.read_bytes(), self.before)

    def test_destination_drift_before_replace_refuses_and_preserves_own_partial_temp(self):
        original_sync = deploy.os.fsync
        changed = False
        def sync(descriptor):
            nonlocal changed
            original_sync(descriptor)
            if not changed:
                changed = True
                self.path.write_bytes(b"concurrent unknown pin")
        with patch.object(deploy.os, "fsync", side_effect=sync):
            with self.assertRaises(deploy.DeployError): self.apply()
        self.assertEqual(self.path.read_bytes(), b"concurrent unknown pin")
        self.assertTrue((self.root / (".duora-recovery-pin-" + "1" * 32 + ".tmp")).is_file())


class RecoveryControlObservationFixture(RecoveryObservationFixture):
    feature = RecoveryAssertions.feature

    setUp = RecoveryObservationFixture.setUp

    encode = staticmethod(RecoveryObservationFixture.encode)

    def observe(self, *, archived_intents=None, read=None):
        context = deploy.observe_current_v2_recovery(self.new, self.paths, read_root=lambda p: self.original[p],
            inspect_runtime=lambda _: {"profile": {"services": {"postgres": {"id": "1" * 64}}}}, read_catalog=lambda: CATALOG)
        attempt = deploy.make_recovery_attempt(self.old, self.new, sql_sha256=deploy.RECOVERY_SQL_48_SHA256,
            bindings=context["bindings"], control=context["control"], nonce="1" * 32)
        return self.feature("observe_current_v2_control")(attempt, self.paths,
            read_root=(lambda p: self.data[p]) if read is None else read,
            inspect_runtime=lambda _: {"profile": {"services": {"postgres": {"id": "1" * 64}}}},
            read_catalog=lambda: CATALOG, archived_intents=archived_intents)

    def start(self): self.original = copy.deepcopy(self.data)

    def restore_pin_fixture(self):
        pin = json.loads(self.data[Path("/etc/duora/coolify-deploy.json")])
        pin["manifest"], pin["manifest_sha256"] = self.baseline_value["manifestPath"], self.baseline_value["manifestSha256"]
        pin["observer_environment"]["DUORA_OBSERVER_IMAGE"] = self.old.images["edge"]
        self.data[Path("/etc/duora/coolify-deploy.json")] = self.encode(pin)


class RecoveryControlObservationTests(RecoveryControlObservationFixture):
    def test_incoming_and_exact_restored_pin_are_reobserved_without_mutating_or_reenrolling(self):
        self.start()
        before = copy.deepcopy(self.data)
        incoming = self.observe()
        self.assertEqual(incoming["pinStage"], "incoming")
        self.assertEqual(self.data, before)
        self.restore_pin_fixture()
        before = copy.deepcopy(self.data)
        restored = self.observe()
        self.assertEqual(restored["pinStage"], "restored")
        self.assertEqual(restored["control"], incoming["control"])
        self.assertEqual(self.data, before)

    def test_archived_intents_only_fill_actual_absence_and_never_hide_changed_originals_or_permissions(self):
        self.start(); self.restore_pin_fixture()
        copies = {self.global_path: self.data[self.global_path], self.project_path: self.data[self.project_path]}
        del self.data[self.global_path]
        def read(path):
            if path not in self.data: raise FileNotFoundError(str(path))
            return self.data[path]
        result = self.observe(archived_intents=copies, read=read)
        self.assertEqual(result["pinStage"], "restored")
        self.data[self.global_path] = b"foreign original"
        with self.assertRaises(deploy.DeployError): self.observe(archived_intents=copies, read=read)
        del self.data[self.global_path]
        def denied(path):
            if path == self.global_path: raise PermissionError("synthetic refusal")
            return read(path)
        with self.assertRaises(deploy.DeployError): self.observe(archived_intents=copies, read=denied)

    def test_unknown_pin_or_unbound_archived_intent_cannot_be_used_as_a_snapshot_restore(self):
        self.start(); self.restore_pin_fixture()
        changed = json.loads(self.data[Path("/etc/duora/coolify-deploy.json")]); changed["compose"] = "/foreign/compose"
        self.data[Path("/etc/duora/coolify-deploy.json")] = self.encode(changed)
        with self.assertRaises(deploy.DeployError): self.observe()
        self.data[Path("/etc/duora/coolify-deploy.json")] = self.original[Path("/etc/duora/coolify-deploy.json")]
        self.restore_pin_fixture()
        with self.assertRaises(deploy.DeployError):
            self.observe(archived_intents={self.global_path: b"unbound archived intent"})


class RecoveryIngressFenceFixture(RecoveryAssertions):
    feature = RecoveryAssertions.feature

    def setUp(self):
        directory = tempfile.TemporaryDirectory(); self.addCleanup(directory.cleanup)
        self.root = Path(directory.name)
        self.nonce, self.checksum = "1" * 32, "a" * 64

    def ensure(self):
        return self.feature("ensure_current_v2_ingress_fence")(self.root, nonce=self.nonce, attempt_sha256=self.checksum)

    def release(self):
        return self.feature("release_current_v2_ingress_fence")(self.root, nonce=self.nonce, attempt_sha256=self.checksum)


class RecoveryIngressFenceTests(RecoveryIngressFenceFixture):
    def test_known_durable_fence_can_be_reobserved_then_only_its_own_marker_released(self):
        first = self.ensure()
        marker = self.root / "duora-current-v2-recovery/pending.json"
        before = marker.read_bytes(); info = marker.stat()
        self.assertEqual(info.st_mode & 0o777, 0o600)
        self.assertEqual(self.ensure()["sha256"], first["sha256"])
        self.assertEqual((marker.read_bytes(), marker.stat().st_ino, marker.stat().st_mtime_ns), (before, info.st_ino, info.st_mtime_ns))
        self.release()
        self.assertFalse(marker.exists())
        self.assertTrue(marker.parent.is_dir())

    def test_foreign_marker_unknown_member_or_mode_is_preserved(self):
        self.ensure()
        marker = self.root / "duora-current-v2-recovery/pending.json"
        old = marker.read_bytes()
        marker.write_bytes(b"foreign marker")
        with self.assertRaises(deploy.DeployError): self.ensure()
        with self.assertRaises(deploy.DeployError): self.release()
        self.assertEqual(marker.read_bytes(), b"foreign marker")
        marker.write_bytes(old)
        unknown = marker.parent / "foreign"; unknown.write_bytes(b"preserve")
        with self.assertRaises(deploy.DeployError): self.release()
        self.assertEqual(unknown.read_bytes(), b"preserve")


class RecoveryControlJournalFixture(CurrentV2AttemptFixture):
    feature = RecoveryAssertions.feature

    attempt = CurrentV2AttemptFixture.attempt

    def setUp(self):
        CurrentV2AttemptFixture.setUp(self)
        self.store = deploy.RecoveryStore(self.root)
        self.store.arm(self.attempt())


class RecoveryControlJournalTests(RecoveryControlJournalFixture):
    def test_control_observer_only_records_a_valid_deployment_identifier_not_raw_api_responses(self):
        try:
            result = self.store.run_operation("control-observer", "a" * 64,
                lambda: subprocess.CompletedProcess(["bounded-api-start"], 0, "deployment123", ""))
        except deploy.DeployError: self.fail("Missing bounded control-observer journal")
        self.assertEqual(result.stdout, "deployment123")
        terminal = json.loads((self.root / "operation-control-observer-terminal.json").read_bytes())
        self.assertEqual(terminal["deploymentUuid"], "deployment123")
        self.store.require_terminal_operations()

    def test_unknown_control_rpc_has_a_durable_intent_and_cannot_be_replayed(self):
        calls = []
        def request():
            calls.append("request")
            raise deploy.DeployError("Synthetic lost API acknowledgement")
        with self.assertRaises(deploy.DeployError): self.store.run_operation("control-env", "a" * 64, request)
        self.assertEqual(calls, ["request"])
        self.assertTrue((self.root / "operation-control-env-intent.json").is_file())
        with self.assertRaises(deploy.DeployError): self.store.run_operation("control-env", "a" * 64, request)
        self.assertEqual(calls, ["request"])

    def test_exact_public_environment_preimage_is_carried_through_all_operations_without_copying_values(self):
        import inspect
        self.assertIn("outcomes", inspect.signature(self.store.run_operation).parameters)
        with self.store._directory() as directory:
            raw = self.store._write(directory, "environment-preimage.json", {"schema": 1, "targetSha256": "a" * 64,
                "othersSha256": "b" * 64, "valueVisibility": "hidden"})
        outcomes = {"environment-preimage.json": hashlib.sha256(raw).hexdigest()}
        self.store.run_operation("control-pin", "a" * 64,
            lambda: subprocess.CompletedProcess(["exact-local-inverse"], 0, "", ""), outcomes=outcomes)
        self.store.require_terminal_operations(outcomes=outcomes)


class RecoveryOwnFenceExemptionFixture(RecoveryAssertions):
    feature = RecoveryAssertions.feature

    def setUp(self):
        directory = tempfile.TemporaryDirectory(); self.addCleanup(directory.cleanup)
        self.root = Path(directory.name)
        (self.root / "duora").mkdir(mode=0o700)
        self.fence = deploy._current_v2_fence_value(nonce="1" * 32, attempt_sha256="a" * 64)
        deploy.ensure_current_v2_ingress_fence(self.root, nonce="1" * 32, attempt_sha256="a" * 64)

    def observe(self, own=None):
        import inspect
        self.assertIn("own_fence", inspect.signature(deploy._recovery_other_ingress_idle).parameters)
        deploy._recovery_other_ingress_idle(ingress=self.root, own_fence=own)


class RecoveryOwnFenceExemptionTests(RecoveryOwnFenceExemptionFixture):
    def test_only_the_exact_own_fence_is_exempt_and_another_project_remains_blocked(self):
        with self.assertRaises(deploy.DeployError): self.observe()
        self.observe(self.fence)
        other = self.root / "goblinshift"; other.mkdir(mode=0o700)
        (other / "pending.json").write_text('{}')
        with self.assertRaises(deploy.DeployError): self.observe(self.fence)

    def test_unknown_own_fence_never_becomes_an_idle_window(self):
        marker = self.root / "duora-current-v2-recovery/pending.json"
        marker.write_text('{}')
        with self.assertRaises(deploy.DeployError): self.observe(self.fence)
        self.assertEqual(marker.read_bytes(), b'{}')


class RecoveryDurabilityReentryFixture(RecoveryEnrollmentFixture, RecoveryForwardCompletionFixture):
    feature = RecoveryAssertions.feature

    setUp = CurrentV2AttemptFixture.setUp

    enroll = RecoveryEnrollmentFixture.enroll

    finish = RecoveryForwardCompletionFixture.finish


class RecoveryDurabilityReentryTests(RecoveryDurabilityReentryFixture):
    def test_existing_fence_after_file_or_directory_sync_failure_must_reestablish_durability_or_remain_unknown(self):
        import stat
        original_sync = deploy.os.fsync
        for boundary in ("file", "directory"):
            ingress = self.root / boundary; ingress.mkdir(mode=0o700)
            directory = ingress / "duora-current-v2-recovery"; directory.mkdir(mode=0o700)
            marker = directory / "pending.json"
            saw_file = False
            def first_sync(descriptor):
                nonlocal saw_file
                regular = stat.S_ISREG(os.fstat(descriptor).st_mode)
                if regular:
                    saw_file = True
                    if boundary == "file": raise OSError("Synthetic file durability failure")
                elif saw_file: raise OSError("Synthetic directory durability failure")
                original_sync(descriptor)
            with self.subTest(boundary=boundary):
                with patch.object(deploy.os, "fsync", side_effect=first_sync):
                    with self.assertRaises(deploy.DeployError):
                        deploy.ensure_current_v2_ingress_fence(ingress, nonce="1" * 32, attempt_sha256="a" * 64)
                self.assertTrue(marker.is_file())
                before = marker.read_bytes()
                with patch.object(deploy.os, "fsync", side_effect=OSError("Still not durable")):
                    with self.assertRaises(deploy.DeployError):
                        deploy.ensure_current_v2_ingress_fence(ingress, nonce="1" * 32, attempt_sha256="a" * 64)
                self.assertEqual(marker.read_bytes(), before)
                self.assertIs(deploy.ensure_current_v2_ingress_fence(ingress, nonce="1" * 32, attempt_sha256="a" * 64)["created"], False)

    def test_already_restored_pin_after_replace_parent_sync_failure_does_not_invent_a_durable_ack(self):
        import stat
        path = self.root / "pin.json"
        before, after = b'{"state":"incoming"}\n', b'{"state":"restored"}\n'
        path.write_bytes(before); path.chmod(0o600)
        original_sync = deploy.os.fsync
        def first_sync(descriptor):
            if stat.S_ISDIR(os.fstat(descriptor).st_mode): raise OSError("Synthetic replacement-parent sync failure")
            original_sync(descriptor)
        def apply():
            with patch.object(deploy, "_recovery_root_read", side_effect=lambda p: p.read_bytes()):
                return deploy._recovery_apply_pin(path, expected_current_sha256=hashlib.sha256(before).hexdigest(),
                    restored_raw=after, nonce="1" * 32)
        with patch.object(deploy.os, "fsync", side_effect=first_sync):
            with self.assertRaises(deploy.DeployError): apply()
        self.assertEqual(path.read_bytes(), after)
        with patch.object(deploy.os, "fsync", side_effect=OSError("Still unknown")):
            with self.assertRaises(deploy.DeployError): apply()
        self.assertEqual(apply()["status"], "already-restored")

    def test_existing_enrollment_after_failed_file_sync_requires_the_same_barrier_before_positive_reentry(self):
        import stat
        self.calls = []
        expected = self.enroll()["sha256"]
        (self.root / "recovery-control").mkdir(mode=0o700)
        original_sync = deploy.os.fsync
        def sync(descriptor):
            if stat.S_ISREG(os.fstat(descriptor).st_mode): raise OSError("Synthetic enrollment file sync failure")
            original_sync(descriptor)
        with patch.object(deploy.os, "fsync", side_effect=sync):
            with self.assertRaises(deploy.DeployError): self.enroll(expected=expected)
        with patch.object(deploy.os, "fsync", side_effect=sync):
            with self.assertRaises(deploy.DeployError): self.enroll(expected=expected)
        self.assertEqual(self.enroll(expected=expected)["status"], "already-enrolled")

    def test_forward_outcome_after_failed_file_sync_is_not_archived_until_its_own_barrier_is_known(self):
        import stat
        RecoveryForwardCompletionFixture.setUp(self)
        path = self.store.directory / "forward-outcome.json"
        original_sync = deploy.os.fsync
        def sync(descriptor):
            info = os.fstat(descriptor)
            if path.exists() and stat.S_ISREG(info.st_mode) and info.st_ino == path.stat().st_ino:
                raise OSError("Synthetic outcome file sync failure")
            original_sync(descriptor)
        with patch.object(deploy.os, "fsync", side_effect=sync):
            with self.assertRaises(deploy.DeployError): self.finish()
        self.assertTrue(path.is_file())
        before = path.read_bytes()
        with patch.object(deploy.os, "fsync", side_effect=sync):
            with self.assertRaises(deploy.DeployError): self.finish()
        self.assertEqual(path.read_bytes(), before)
        self.assertEqual(self.finish()["status"], "forward-recorded")

    def test_forward_recorded_current_must_be_durable_before_archiving_without_changing_legacy_private_permissions(self):
        RecoveryForwardCompletionFixture.setUp(self)
        path = self.root / "current.json"; path.chmod(0o644)
        original_sync = deploy.os.fsync
        def sync(descriptor):
            if os.fstat(descriptor).st_ino == path.stat().st_ino:
                raise OSError("Synthetic current record sync failure")
            original_sync(descriptor)
        with patch.object(deploy.os, "fsync", side_effect=sync):
            with self.assertRaises(deploy.DeployError): self.finish()
        self.assertTrue(self.store.directory.is_dir())
        self.assertEqual(path.stat().st_mode & 0o777, 0o644)
        self.assertEqual(self.finish()["status"], "forward-recorded")


class RecoveryJournalDurabilityFixture(CurrentV2AttemptFixture):
    feature = RecoveryAssertions.feature

    attempt = CurrentV2AttemptFixture.attempt

    def setUp(self):
        CurrentV2AttemptFixture.setUp(self)
        self.store = deploy.RecoveryStore(self.root)
        self.store.arm(self.attempt())

    def failing_sync_for(self, path):
        import stat
        original = deploy.os.fsync
        def sync(descriptor):
            info = os.fstat(descriptor)
            if path.exists() and stat.S_ISREG(info.st_mode) and info.st_ino == path.stat().st_ino:
                raise OSError("Synthetic private acknowledgement durability failure")
            original(descriptor)
        return sync


class RecoveryJournalDurabilityTests(RecoveryJournalDurabilityFixture):
    def test_an_undurable_terminal_cannot_authorize_the_next_producer_without_repairing_its_barrier(self):
        terminal = self.root / "operation-worker-stop-terminal.json"
        completed = []
        def first():
            completed.append("old-producer")
            return subprocess.CompletedProcess(["synthetic"], 0, "", "")
        with patch.object(deploy.os, "fsync", side_effect=self.failing_sync_for(terminal)):
            with self.assertRaises(deploy.DeployError): self.store.run_operation("worker-stop", "a" * 64, first)
        next_calls = []
        def next_operation():
            next_calls.append("new-producer")
            return subprocess.CompletedProcess(["synthetic"], 0, "", "")
        with patch.object(deploy.os, "fsync", side_effect=self.failing_sync_for(terminal)):
            with self.assertRaises(deploy.DeployError): self.store.run_operation("migration", "a" * 64, next_operation)
        self.assertEqual(next_calls, [])
        self.assertEqual(completed, ["old-producer"])
        self.store.run_operation("migration", "a" * 64, next_operation)
        self.assertEqual(next_calls, ["new-producer"])
        self.assertEqual(completed, ["old-producer"])

    def test_readonly_terminal_classification_never_fsyncs_or_claims_a_durable_mutation_permission(self):
        import inspect
        self.assertIn("durable", inspect.signature(self.store.require_terminal_operations).parameters)
        with patch.object(deploy.os, "fsync", side_effect=AssertionError("Readonly must not sync")):
            self.store.require_terminal_operations(durable=False)

    def test_an_existing_known_outcome_is_also_synced_before_a_new_control_effect(self):
        path = self.root / "application-outcome.json"
        with self.store._directory() as directory:
            raw = self.store._write(directory, path.name, {"schema": 1, "synthetic": "known public projection"})
        outcomes = {path.name: hashlib.sha256(raw).hexdigest()}
        calls = []
        def operation():
            calls.append("control")
            return subprocess.CompletedProcess(["synthetic"], 0, "", "")
        with patch.object(deploy.os, "fsync", side_effect=self.failing_sync_for(path)):
            with self.assertRaises(deploy.DeployError):
                self.store.run_operation("control-pin", "a" * 64, operation, outcomes=outcomes)
        self.assertEqual(calls, [])


class RecoveryApplicationOutcomeDurabilityFixture(RecoveryAdministrativeAdmissionFixture):
    feature = RecoveryAssertions.feature

    setUp = RecoveryAdministrativeAdmissionFixture.setUp

    encode = staticmethod(RecoveryAdministrativeAdmissionFixture.encode)

    entry = RecoveryAdministrativeAdmissionFixture.entry


class RecoveryApplicationOutcomeDurabilityTests(RecoveryApplicationOutcomeDurabilityFixture):
    def test_reused_application_outcome_requires_a_durable_ack_without_replaying_completed_producers(self):
        import stat
        path = self.store.directory / "application-outcome.json"
        original_sync = deploy.os.fsync
        def sync(descriptor):
            info = os.fstat(descriptor)
            if path.exists() and stat.S_ISREG(info.st_mode) and info.st_ino == path.stat().st_ino:
                raise OSError("Synthetic app outcome durability failure")
            original_sync(descriptor)
        with patch.object(deploy.os, "fsync", side_effect=sync):
            with self.assertRaises(deploy.DeployError): self.entry(readonly=False)
        self.assertTrue(path.exists())
        with patch.object(deploy.os, "fsync", side_effect=sync):
            with self.assertRaises(deploy.DeployError): self.entry(readonly=False)
        self.assertFalse(any("verify" in c or "up" in c or "--single-transaction" in c for c in self.last_run.commands))
        self.assertIs(self.entry(readonly=False)["reusedApplicationOutcome"], True)


class RecoveryObserverRuntimeFixture(RecoveryAssertions):
    """Synthetic Docker metadata; native Docker inspection remains a separate remote gate."""

    feature = RecoveryAssertions.feature

    def setUp(self):
        self.old, self.new = releases()
        self.pin = {"application_uuid": "testresource", "observer_container_name": "observer-testresource",
            "observer_environment": {"DUORA_OBSERVER_IMAGE": self.old.images["edge"], "HOST": "0.0.0.0"},
            "observer_labels": {"traefik.enable": "false", "coolify.version": "4.5.0"},
            "observer_networks": {"default": {"name": "testresource_default", "ipam": {}},
                "duora_edge": {"name": "duora_edge", "external": True, "ipam": {}}}}
        self.image = {"Id": "sha256:" + "9" * 64, "Config": {"Env": ["PATH=/usr/bin:/bin"],
            "Labels": {"org.opencontainers.image.source": "https://github.com/BipoXDXD/duora",
                "org.opencontainers.image.title": "duora-edge", "org.opencontainers.image.revision": self.old.commit}}}
        self.container = {"Id": "8" * 64, "Name": "/observer-testresource", "Image": self.image["Id"],
            "Config": {"Image": self.old.images["edge"], "Env": ["PATH=/usr/bin:/bin", "DUORA_OBSERVER_IMAGE=" + self.old.images["edge"], "HOST=0.0.0.0"],
                "Labels": {**self.image["Config"]["Labels"], **self.pin["observer_labels"],
                    "com.docker.compose.project": "testresource", "com.docker.compose.service": "deployment-observer",
                    "com.docker.compose.oneoff": "False"},
                "User": "10001:10001", "Entrypoint": ["/bin/sh", "-c", "exec sleep 2147483647"], "Cmd": None},
            "HostConfig": {"ReadonlyRootfs": True, "Privileged": False, "CapDrop": ["ALL"], "CapAdd": None,
                "SecurityOpt": ["no-new-privileges"], "Memory": 16777216, "PidsLimit": 16,
                "RestartPolicy": {"Name": "unless-stopped", "MaximumRetryCount": 0}, "Binds": None,
                "VolumesFrom": None, "Devices": [], "PortBindings": {}, "PidMode": "", "NetworkMode": "testresource_default"},
            "Mounts": [], "NetworkSettings": {"Ports": {}, "Networks": {"testresource_default": {}, "duora_edge": {}}},
            "State": {"Status": "running", "Running": True, "Paused": False, "Restarting": False,
                "OOMKilled": False, "StartedAt": "2026-10-25T15:00:00Z", "Health": {"Status": "healthy"}}, "RestartCount": 0}

    def inspect(self, *, item=None):
        item = self.container if item is None else item
        return self.feature("_recovery_require_running_observer")(self.old, self.pin, self.image["Id"],
            capture=lambda command: json.dumps(self.image if command[1] == "image" else item).encode())


class RecoveryObserverRuntimeTests(RecoveryObserverRuntimeFixture):
    def test_exact_running_old_observer_including_effective_environment_and_security_is_witnessed(self):
        result = self.inspect()
        self.assertEqual(result["id"], "8" * 64)
        self.assertEqual(result["imageId"], self.image["Id"])
        self.assertEqual(result["reference"], self.old.images["edge"])
        self.assertNotIn("Env", result)
        self.assertNotIn("PATH", json.dumps(result))

    def test_healthy_cached_image_alone_cannot_hide_effective_value_or_privilege_drift(self):
        mutations = [lambda c: c["Config"]["Env"].append("EXTRA=private"),
            lambda c: c["Config"].update(Image=self.new.images["edge"] + "foreign"),
            lambda c: c["Config"].update(User="0"), lambda c: c["HostConfig"].update(Privileged=True),
            lambda c: c["HostConfig"].update(Binds=["/etc:/host:ro"]),
            lambda c: c["NetworkSettings"]["Networks"].update(foreign={}),
            lambda c: c["State"].update(OOMKilled=True), lambda c: c.update(RestartCount=1),
            lambda c: c["Config"]["Labels"].update({"com.docker.compose.project": "foreign"}),
            lambda c: c["Config"].update(Entrypoint=["/bin/sh"])]
        for mutate in mutations:
            with self.subTest(mutation=mutate):
                changed = copy.deepcopy(self.container)
                mutate(changed)
                with self.assertRaises(deploy.DeployError): self.inspect(item=changed)

    def test_runtime_identity_and_environment_duplicates_cannot_be_inferred_from_a_ready_endpoint(self):
        for change in (lambda c: c.update(Id="wrong"), lambda c: c["Config"]["Env"].append("HOST=0.0.0.0"),
                lambda c: c["State"]["Health"].update(Status="starting"),
                lambda c: c["Config"]["Labels"].update({"traefik.enable": "true"})):
            candidate = copy.deepcopy(self.container)
            change(candidate)
            with self.subTest(candidate=candidate["Id"]):
                with self.assertRaises(deploy.DeployError): self.inspect(item=candidate)


class RecoveryControlEngineFixture(CurrentV2AttemptFixture, RecoveryObservationFixture):
    feature = RecoveryAssertions.feature

    encode = staticmethod(RecoveryObservationFixture.encode)

    def setUp(self):
        from types import SimpleNamespace
        CurrentV2AttemptFixture.setUp(self)
        RecoveryObservationFixture.setUp(self)
        self.local = self.root
        self.store = deploy.RecoveryStore(self.local / "current-v2-control")
        self.context = deploy.observe_current_v2_recovery(self.new, self.paths, read_root=lambda p: self.data[p],
            inspect_runtime=lambda _: {"profile": {"services": {"postgres": {"id": "1" * 64}}}}, read_catalog=lambda: CATALOG)
        self.attempt_value = deploy.make_recovery_attempt(self.old, self.new, sql_sha256=deploy.RECOVERY_SQL_48_SHA256,
            bindings=self.context["bindings"], control=self.context["control"], nonce="1" * 32)
        pin = json.loads(self.data[Path('/etc/duora/coolify-deploy.json')])
        pin['manifest'], pin['manifest_sha256'] = self.baseline_value['manifestPath'], self.baseline_value['manifestSha256']
        pin['observer_environment']['DUORA_OBSERVER_IMAGE'] = self.old.images['edge']
        self.context.update(restoredPinRaw=deploy._recovery_pin_bytes(pin), pinStage="incoming", incoming=self.new)
        self.events = []
        self.rows = [{"key": "DUORA_OBSERVER_IMAGE", "is_preview": False, "is_literal": True, "is_shown_once": True}]
        self.fail_start = False
        def api(path, method="GET", payload=None):
            self.events.append((method, path))
            if path.endswith('/envs'): return copy.deepcopy(self.rows)
            if path.endswith('/start'):
                if self.fail_start: raise deploy.DeployError('Synthetic lost API acknowledgement')
                return {"deployment_uuid": "known-job123"}
            if path == 'deployments/known-job123': return {"status": "finished"}
            self.fail('Unexpected API request: ' + path)
        self.receiver = SimpleNamespace(api=api, update_envs=lambda u,v: self.events.append(('env', u, v)),
            require_idle=lambda u: self.events.append(('idle',u)), deployment_finished=lambda r: r['status']=='finished')
        self.lease = SimpleNamespace(release_executor=lambda: self.events.append('unlock-executor'),
            reacquire_executor=lambda: self.events.append('lock-executor'))
        self.confirms = []
        self.observer = {'id': '8'*64, 'imageId': 'sha256:'+'9'*64,
            'reference': self.old.images['edge'], 'startedAt': '2026-10-25T15:00:00Z', 'restartCount': 0,
            'environmentSha256': 'f'*64}

    def phases(self):
        def inverse(*args, **kwargs):
            self.events.append('pin')
            self.context['pinStage'] = 'restored'
            return {'sha256': self.baseline_value['pinSha256']}
        with patch.object(deploy, '_recovery_apply_pin', side_effect=inverse):
            return self.feature('_recovery_control_phases')(self.store, self.attempt_value, self.context, self.paths,
                receiver=self.receiver, lease=self.lease, confirm_application=lambda: self.confirms.append('actual-apps'),
                observe_frame=lambda: self.context, observe_observer=lambda: copy.deepcopy(self.observer),
                ingress=self.local / 'ingress', sleep=lambda _: self.fail('No polling after a finished known job'))


class RecoveryControlEngineTests(RecoveryControlEngineFixture):
    def test_control_inverse_and_api_start_are_each_acknowledged_before_control_ready_without_app_replay(self):
        (self.local/'ingress').mkdir(mode=0o700)
        result = self.phases()
        self.assertEqual(result['status'], 'control-ready-tail-pending')
        self.assertIs(result['complete'], False)
        self.assertEqual(self.events.count('pin'), 1)
        self.assertEqual([v[0] for v in self.events if isinstance(v,tuple)].count('env'), 1)
        self.assertIn('unlock-executor', self.events)
        self.assertLess(self.events.index('unlock-executor'), self.events.index(('POST','applications/testresource/start')))
        self.assertGreater(self.events.index('lock-executor'), self.events.index(('GET','deployments/known-job123')))
        self.assertTrue((self.local/'ingress/duora-current-v2-recovery/pending.json').is_file())
        self.assertTrue((self.store.directory/'control-ready.json').is_file())
        self.assertGreaterEqual(len(self.confirms), 4)

    def test_known_completed_control_phases_are_reobserved_without_repeating_pin_patch_or_start(self):
        (self.local/'ingress').mkdir(mode=0o700)
        self.phases()
        self.events.clear()
        self.phases()
        self.assertNotIn('pin', self.events)
        self.assertFalse(any(isinstance(v,tuple) and v[0] in {'env','POST'} for v in self.events))
        self.assertIn(('GET','deployments/known-job123'), self.events)

    def test_lost_start_ack_is_unknown_and_never_retried_even_if_the_resource_is_healthy(self):
        (self.local/'ingress').mkdir(mode=0o700)
        self.fail_start=True
        with self.assertRaises(deploy.DeployError): self.phases()
        self.assertTrue((self.store.directory/'operation-control-observer-intent.json').is_file())
        self.assertFalse((self.store.directory/'operation-control-observer-terminal.json').exists())
        self.events.clear()
        self.fail_start=False
        with self.assertRaises(deploy.DeployError): self.phases()
        self.assertFalse(any(isinstance(v,tuple) and v[0] in {'POST','env'} for v in self.events))
        self.assertFalse((self.store.directory/'control-ready.json').exists())

    def test_foreign_environment_or_app_drift_prevents_a_false_control_ready_seal(self):
        (self.local/'ingress').mkdir(mode=0o700)
        self.phases()
        self.rows.append({'key':'FOREIGN','is_preview':False,'value':'private'})
        self.events.clear()
        with self.assertRaises(deploy.DeployError): self.phases()
        self.assertFalse(any(isinstance(v,tuple) and v[0]=='POST' for v in self.events))

    def test_a_new_control_effect_requires_a_durable_existing_phase_and_no_foreign_fence(self):
        (self.local/'ingress').mkdir(mode=0o700)
        self.phases()
        self.events.clear()
        with patch.object(deploy.os, 'fsync', side_effect=OSError('synthetic durability loss')):
            with self.assertRaises(deploy.DeployError): self.phases()
        self.assertFalse(any(isinstance(v,tuple) and v[0] in {'POST','env'} for v in self.events))


class RecoveryControlledObserverAdmissionFixture(RecoveryControlEngineFixture):
    feature=RecoveryAssertions.feature

    setUp=RecoveryControlEngineFixture.setUp

    encode=staticmethod(RecoveryObservationFixture.encode)

    phases=RecoveryControlEngineFixture.phases


class RecoveryControlledObserverAdmissionTests(RecoveryControlledObserverAdmissionFixture):
    def test_only_the_exact_inflight_observer_intent_can_have_no_ack_in_a_readonly_steady_observation(self):
        (self.local/'ingress').mkdir(mode=0o700)
        self.phases()
        terminal=self.store.directory/'operation-control-observer-terminal.json'
        terminal.unlink()  # Synthetic crash point; never a native or production mutation.
        intent=json.loads((self.store.directory/'operation-control-observer-intent.json').read_bytes())
        # A general recovery caller still refuses. This exception can only classify a harmless child entry.
        known={name:hashlib.sha256((self.store.directory/name).read_bytes()).hexdigest()
            for name in ('environment-preimage.json','control-ready.json')}
        with self.assertRaises(deploy.DeployError): self.store.require_terminal_operations(outcomes=known)
        try:
            self.store.require_terminal_operations(outcomes=known, active_observer_sha256=intent['commandSha256'])
        except TypeError: self.fail('Missing strictly readonly admission of the exact controlled sleeping observer')
        with self.assertRaises(deploy.DeployError):
            self.store.require_terminal_operations(outcomes=known, active_observer_sha256='f'*64)
        with self.assertRaises(deploy.DeployError):
            self.store.require_terminal_operations(outcomes=known, active_observer_sha256=intent['commandSha256'],durable=True)

    def test_an_unknown_pin_or_environment_result_is_never_exempted_for_the_observer(self):
        (self.local/'ingress').mkdir(mode=0o700)
        self.phases()
        (self.store.directory/'operation-control-env-terminal.json').unlink()
        intent=json.loads((self.store.directory/'operation-control-observer-intent.json').read_bytes())
        known={name:hashlib.sha256((self.store.directory/name).read_bytes()).hexdigest()
            for name in ('environment-preimage.json','control-ready.json')}
        self.assertIn('active_observer_sha256', __import__('inspect').signature(self.store.require_terminal_operations).parameters,
            'The steady exception must be an explicit readonly parameter, never the general mutation path')
        with self.assertRaises(deploy.DeployError):
            self.store.require_terminal_operations(outcomes=known, active_observer_sha256=intent['commandSha256'])


class RecoverySteadyEntryFixture(RecoveryAdministrativeAdmissionFixture):
    feature=RecoveryAssertions.feature

    encode=staticmethod(RecoveryObservationFixture.encode)

    entry=RecoveryAdministrativeAdmissionFixture.entry

    successful_application_recovery=RecoveryAdministrativeAdmissionFixture.successful_application_recovery

    def setUp(self):
        RecoveryAdministrativeAdmissionFixture.setUp(self)
        self.successful_application_recovery()
        self.attempt_value=self.store.load()
        self.application=json.loads((self.store.directory/'application-outcome.json').read_bytes())
        self.control_store=deploy.RecoveryStore(self.temporary_root/'current-v2-control')
        pointer=self.control_store.arm(self.attempt_value)
        projection={'targetSha256':'a'*64,'othersSha256':'b'*64,'valueVisibility':'hidden'}
        self.environment={'schema':1,'attemptSha256':pointer['sha256'],'projection':projection}
        envraw=deploy._recovery_write_same(self.control_store,'environment-preimage.json',self.environment)
        self.known={'environment-preimage.json':hashlib.sha256(envraw).hexdigest()}
        pin=json.loads(self.data[Path('/etc/duora/coolify-deploy.json')])
        pin['manifest'],pin['manifest_sha256']=self.baseline_value['manifestPath'],self.baseline_value['manifestSha256']
        pin['observer_environment']['DUORA_OBSERVER_IMAGE']=self.old.images['edge']
        self.data[Path('/etc/duora/coolify-deploy.json')]=self.encode(pin)
        descs={
            'pin': {'kind':'three-field-inverse','from':self.attempt_value['control']['incomingPinSha256'],'to':self.baseline_value['pinSha256']},
            'env': {'kind':'observer-env-inverse','application':'testresource','image':self.old.images['edge'], 'preimageSha256':self.known['environment-preimage.json']},
            'observer': {'kind':'start-restored-observer','application':'testresource','pinSha256':self.baseline_value['pinSha256'],'edgeImage':self.old.images['edge']}}
        for name,descriptor in descs.items():
            self.control_store.run_operation('control-'+name,deploy._guard.digest(descriptor),
                lambda:subprocess.CompletedProcess(['synthetic'],0,'known-job123',''),outcomes=self.known)
        # Native transport would create this before the phases; this dictionary is a clearly synthetic reader.
        self.data[Path('/var/lib/coolify-release/duora-current-v2-recovery/pending.json')]=(json.dumps(
            deploy._current_v2_fence_value(nonce=self.attempt_value['nonce'],attempt_sha256=pointer['sha256']),
            sort_keys=True,separators=(',',':'))+'\n').encode()

    def steady(self,release=None):
        from contextlib import ExitStack
        _constructor=deploy.RecoveryStore
        with ExitStack() as patches:
            patches.enter_context(patch.object(deploy,'_recovery_root_read',side_effect=lambda p:self.data[p]))
            patches.enter_context(patch.object(deploy,'_recovery_resolve_stores',return_value=(self.store,self.control_store,self.attempt_value)))
            patches.enter_context(patch.object(deploy,'RecoveryStore',side_effect=lambda p:self.control_store if p.name=='current-v2-control' else self.store))
            patches.enter_context(patch.object(deploy,'runtime_recheck',return_value={'profile':{'services':{'postgres':{'id':'1'*64}}}}))
            patches.enter_context(patch.object(deploy,'_query',return_value=json.dumps(CATALOG)))
            patches.enter_context(patch.object(deploy,'_recovery_require_cached_images',return_value=self.application['imageIds']))
            patches.enter_context(patch.object(deploy,'_recovery_require_running_applications',return_value=self.application['runtimeApps']))
            patches.enter_context(patch.object(deploy,'_recovery_require_quiescent',return_value=None))
            patches.enter_context(patch.object(deploy,'_minimum_contract_v2',return_value=True))
            patches.enter_context(patch.object(deploy,'_recovery_acl_witness',return_value=None))
            patches.enter_context(patch.object(deploy,'_require_no_privileged_account_without_mfa',return_value=None))
            patches.enter_context(patch.object(deploy,'_wait_ready',return_value=None))
            patches.enter_context(patch.object(deploy,'_require_services_running',return_value=None))
            self.runner_under_test=FakeRunner(pre_head=CATALOG[-1],migration_head=CATALOG[-1])
            return self.feature('_recovery_current_steady')(self.old if release is None else release,self.paths,self.runner_under_test)


class RecoverySteadyEntryTests(RecoverySteadyEntryFixture):
    def test_exact_recovered_old_current_and_inflight_controlled_observer_return_readonly_without_redeploy(self):
        (self.control_store.directory/'operation-control-observer-terminal.json').unlink()
        before={p.name:p.read_bytes() for p in self.control_store.directory.iterdir()}
        self.assertIs(self.steady(),True)
        self.assertEqual(self.runner_under_test.commands,[])
        self.assertEqual({p.name:p.read_bytes() for p in self.control_store.directory.iterdir()},before)

    def test_steady_cannot_admit_an_incoming_manifest_changed_pin_or_unknown_prior_mutation(self):
        self.assertIs(self.steady(self.new),False)
        self.data[Path('/etc/duora/coolify-deploy.json')]+=b'\n'
        with self.assertRaises(deploy.DeployError):self.steady()

    def test_steady_refuses_unknown_environment_ack_even_when_application_and_readiness_are_healthy(self):
        (self.control_store.directory/'operation-control-env-terminal.json').unlink()
        with self.assertRaises(deploy.DeployError):self.steady()

    def test_normal_deploy_and_preflight_consume_steady_before_any_general_intent_guard_or_pull(self):
        self.assertTrue(hasattr(deploy,'_recovery_current_steady'),'Missing controlled steady entry')
        with patch.object(deploy,'_recovery_current_steady',return_value=True), \
                patch.object(deploy,'require_no_current_v2_recovery_intent',side_effect=AssertionError('Steady must be handled before the guard')), \
                patch.object(deploy,'_record_release',side_effect=AssertionError('Never rewrite history in steady')):
            run=lambda *a,**k:self.fail('No commands for the already-verified steady branch')
            self.assertIsNone(deploy.runtime_preflight(self.old,self.paths,run))
            self.assertIsNone(deploy.deploy(self.old,self.paths,run))


class RecoveryControlCliFixture(RecoveryAssertions):
    feature=RecoveryAssertions.feature


class RecoveryControlCliTests(RecoveryControlCliFixture):
    def test_control_completion_is_a_distinct_administrative_mode_under_all_three_locks(self):
        from contextlib import contextmanager
        temporary=tempfile.TemporaryDirectory();self.addCleanup(temporary.cleanup)
        root=Path(temporary.name);host=root/'host.env';host.write_text('DUORA_IMAGE_PREFIX=ghcr.io/bipoxdxd/duora-\nDUORA_MIN_FREE_GIB=1\nDUORA_DISK_CHECK_PATH=/\n')
        manifest=root/'manifest.json';manifest.write_text(json.dumps(releases()[0].raw))
        events=[];lease=object()
        @contextmanager
        def locks(paths):
            events.append('three-locks');yield lease
        with patch.object(deploy,'_require_recovery_administrator',side_effect=lambda:events.append('root')), \
                patch.object(deploy,'preflight',return_value=None), \
                patch.object(deploy,'recovery_administrative_locks',side_effect=locks), \
                patch.object(deploy,'recovery_administrative_entry',side_effect=lambda *a,**k:events.append(k) or {'complete':False}):
            try:
                result=deploy.main(['--manifest',str(manifest),'--host-env',str(host),'--compose-file',str(root/'compose'),
                    '--complete-current-v2-recovery-control','--preflight-only'])
            except SystemExit:self.fail('Missing explicit administrative complete-control mode')
        self.assertEqual(result,0)
        self.assertEqual(events[:2],['root','three-locks'])
        self.assertEqual(events[-1]['mode'],'complete-control')
        self.assertIs(events[-1]['lease'],lease)
        self.assertIs(events[-1]['readonly'],True)

    def test_control_entry_does_not_turn_an_application_pending_receipt_into_a_full_success(self):
        from types import SimpleNamespace
        self.assertTrue(hasattr(deploy,'_recovery_complete_control'),'Missing admitted control completion adapter')
        release=releases()[0];paths=deploy.Paths(Path('/opt/duora/compose'),Path('/etc/duora/env'),Path('/var/lib/duora'))
        with patch.object(deploy,'_recovery_complete_control',return_value={'status':'preflight','complete':False}) as adapter:
            result=deploy.recovery_administrative_entry(release,paths,lambda *a,**k:self.fail('No general deploy'),
                mode='complete-control',readonly=True,reviewed_sha256=None,lease=SimpleNamespace())
        self.assertIs(result['complete'],False)
        self.assertEqual(adapter.call_count,1)


class RecoveryTailFixture(RecoveryAdministrativeAdmissionFixture):
    feature=RecoveryAssertions.feature

    encode=staticmethod(RecoveryObservationFixture.encode)

    entry=RecoveryAdministrativeAdmissionFixture.entry

    successful_application_recovery=RecoveryAdministrativeAdmissionFixture.successful_application_recovery

    def setUp(self):
        from types import SimpleNamespace
        RecoveryAdministrativeAdmissionFixture.setUp(self)
        self.successful_application_recovery()
        self.attempt_value=self.store.load()
        self.state=self.temporary_root
        self.local=self.state
        self.control_store=deploy.RecoveryStore(self.state/'current-v2-control')
        self.context=deploy.observe_current_v2_recovery(self.new,self.paths,read_root=lambda p:self.data[p],
            inspect_runtime=lambda _:{'profile':{'services':{'postgres':{'id':'1'*64}}}},read_catalog=lambda:CATALOG)
        pin=json.loads(self.data[Path('/etc/duora/coolify-deploy.json')])
        pin['manifest'],pin['manifest_sha256']=self.baseline_value['manifestPath'],self.baseline_value['manifestSha256']
        pin['observer_environment']['DUORA_OBSERVER_IMAGE']=self.old.images['edge']
        self.context.update(restoredPinRaw=deploy._recovery_pin_bytes(pin),pinStage='incoming',incoming=self.new)
        self.ingress=self.state/'ingress';self.ingress.mkdir(mode=0o700);(self.ingress/'duora').mkdir(mode=0o700)
        for path,raw in [(self.ingress/'pending.json',self.data[self.global_path]),(self.ingress/'duora/pending.json',self.data[self.project_path])]:
            path.write_bytes(raw);path.chmod(0o600)
        self.events=[]
        def api(path,method='GET',payload=None):
            self.events.append((method,path))
            if path.endswith('/envs'):return [{'key':'DUORA_OBSERVER_IMAGE','is_preview':False,'is_literal':True,'is_shown_once':True}]
            if path.endswith('/start'):return {'deployment_uuid':'known-job123'}
            if path=='deployments/known-job123':return {'status':'finished'}
            self.fail(path)
        receiver=SimpleNamespace(api=api,update_envs=lambda *a:None,require_idle=lambda *a:None,deployment_finished=lambda r:r['status']=='finished')
        lease=SimpleNamespace(release_executor=lambda:None,reacquire_executor=lambda:None)
        self.observer={'id':'8'*64,'imageId':'sha256:'+'1'*64,'reference':self.old.images['edge'],
            'startedAt':'2026-10-25T15:00:00Z','restartCount':0,'environmentSha256':'f'*64}
        def inverse(*a,**k):self.context['pinStage']='restored'
        with patch.object(deploy,'_recovery_apply_pin',side_effect=inverse):
            deploy._recovery_control_phases(self.control_store,self.attempt_value,self.context,self.paths,receiver=receiver,lease=lease,
                confirm_application=lambda:None,observe_frame=lambda:self.context,observe_observer=lambda:self.observer,ingress=self.ingress)
        self.paths=deploy.Paths(self.state/'compose',self.state/'host.env',self.state)

    def finish(self):
        with patch.object(deploy,'_recovery_root_read',side_effect=lambda p:p.read_bytes()):
            return self.feature('_recovery_finish_control_tail')(self.store,self.control_store,self.attempt_value,self.context,self.paths,
                ingress=self.ingress,confirm=lambda:self.events.append('final-witness'))


class RecoveryTailTests(RecoveryTailFixture):
    def test_only_owned_intents_are_archived_after_exact_copies_and_control_outcome_and_the_fence_is_last(self):
        prior=list(self.events)
        result=self.finish()
        self.assertIs(result['complete'],True)
        self.assertEqual(result['releaseId'],self.old.release_id)
        self.assertFalse((self.ingress/'pending.json').exists())
        self.assertFalse((self.ingress/'duora/pending.json').exists())
        self.assertEqual(list((self.ingress/'duora-current-v2-recovery').iterdir()),[])
        a=self.state/'current-v2-recovery-history'/self.attempt_value['nonce']
        b=self.state/'current-v2-control-history'/self.attempt_value['nonce']
        self.assertTrue((a/'application-outcome.json').is_file())
        self.assertTrue((b/'control-outcome.json').is_file())
        self.assertEqual((b/'global-intent.preimage.json').read_bytes(),self.data[self.global_path])
        self.assertEqual((b/'project-intent.preimage.json').read_bytes(),self.data[self.project_path])
        self.assertTrue((self.state/'current-v2-compatibility.json').is_file())
        self.assertEqual([v for v in self.events if v!='final-witness'],prior)

    def test_changed_original_intent_is_preserved_and_no_archive_or_fence_release_occurs(self):
        (self.ingress/'duora/pending.json').write_text('{"foreign":true}\n')
        with self.assertRaises(deploy.DeployError):self.finish()
        self.assertTrue((self.ingress/'pending.json').is_file())
        self.assertTrue((self.ingress/'duora/pending.json').is_file())
        self.assertTrue((self.ingress/'duora-current-v2-recovery/pending.json').is_file())
        self.assertTrue(self.store.directory.is_dir())

    def test_failure_after_first_unlink_reuses_only_known_preimages_and_never_replays_control_producers(self):
        original=deploy.os.unlink;calls=[]
        def fail_second(name,*args,**kwargs):
            if name=='pending.json':
                calls.append(name)
                if len(calls)==2:raise OSError('Synthetic interrupted own pending cleanup')
            return original(name,*args,**kwargs)
        with patch.object(deploy.os,'unlink',side_effect=fail_second):
            with self.assertRaises(deploy.DeployError):self.finish()
        self.assertFalse((self.ingress/'pending.json').exists())
        self.assertTrue((self.ingress/'duora/pending.json').exists())
        before=list(self.events)
        self.assertIs(self.finish()['complete'],True)
        self.assertEqual([v for v in self.events[len(before):] if v!='final-witness'],[])

    def test_failure_between_the_two_archives_resolves_exact_known_locations_without_overwriting_history(self):
        original=deploy.os.rename;calls=[]
        def fail_second(*args,**kwargs):
            calls.append(args)
            if len(calls)==2:raise OSError('Synthetic archive interruption')
            return original(*args,**kwargs)
        with patch.object(deploy.os,'rename',side_effect=fail_second):
            with self.assertRaises(deploy.DeployError):self.finish()
        self.assertTrue((self.state/'current-v2-recovery-history'/self.attempt_value['nonce']).is_dir())
        self.assertTrue(self.control_store.directory.is_dir())
        self.assertIs(self.finish()['complete'],True)

    def test_already_completed_tail_is_reobserved_without_recreating_a_fence_or_replaying_any_rpc(self):
        self.finish()
        before={str(p.relative_to(self.state)):p.read_bytes() for p in self.state.rglob('*') if p.is_file()}
        self.assertIs(self.finish()['complete'],True)
        after={str(p.relative_to(self.state)):p.read_bytes() for p in self.state.rglob('*') if p.is_file()}
        self.assertEqual(after,before)
        self.assertFalse((self.ingress/'duora-current-v2-recovery/pending.json').exists())

    def test_foreign_compatibility_pointer_or_partial_copy_does_not_authorize_cleanup(self):
        foreign=self.state/'current-v2-compatibility.json';foreign.write_text('{"foreign":true}\n');foreign.chmod(0o600)
        with self.assertRaises(deploy.DeployError):self.finish()
        self.assertTrue((self.ingress/'pending.json').is_file())
        self.assertEqual(foreign.read_text(),'{"foreign":true}\n')


class RecoveryArchiveResolutionFixture(RecoveryTailFixture):
    feature=RecoveryAssertions.feature

    setUp=RecoveryTailFixture.setUp

    encode=staticmethod(RecoveryObservationFixture.encode)

    entry=RecoveryAdministrativeAdmissionFixture.entry

    successful_application_recovery=RecoveryAdministrativeAdmissionFixture.successful_application_recovery

    finish=RecoveryTailFixture.finish


class RecoveryArchiveResolutionTests(RecoveryArchiveResolutionFixture):
    def test_interrupted_archive_pointer_resolves_only_its_exact_active_or_historical_stores(self):
        original=deploy.os.rename;calls=[]
        def fail_second(*a,**k):
            calls.append(a)
            if len(calls)==2:raise OSError('Synthetic B archive not yet acknowledged')
            return original(*a,**k)
        with patch.object(deploy.os,'rename',side_effect=fail_second):
            with self.assertRaises(deploy.DeployError):self.finish()
        with patch.object(deploy,'_recovery_root_read',side_effect=lambda p:p.read_bytes()):
            try:
                a,b,attempt=self.feature('_recovery_resolve_stores')(self.paths)
            except TypeError:self.fail('Missing resolution of the exact partial two-archive transition')
        self.assertEqual(a.directory,self.state/'current-v2-recovery-history'/self.attempt_value['nonce'])
        self.assertEqual(b.directory,self.state/'current-v2-control')
        self.assertEqual(attempt,self.attempt_value)
        with patch.object(deploy,'_recovery_root_read',side_effect=lambda p:p.read_bytes()):
            with self.assertRaises(deploy.DeployError):deploy._recovery_parse_compatibility((self.state/'current-v2-compatibility.json').read_bytes(),self.paths)

    def test_completed_resolution_does_not_ignore_a_foreign_active_directory_or_pointer(self):
        self.finish()
        with patch.object(deploy,'_recovery_root_read',side_effect=lambda p:p.read_bytes()):
            _a,_b,attempt=self.feature('_recovery_resolve_stores')(self.paths)
        self.assertEqual(attempt,self.attempt_value)
        foreign=self.state/'current-v2-control';foreign.mkdir(mode=0o700)
        (foreign/'unknown.json').write_text('{}')
        with patch.object(deploy,'_recovery_root_read',side_effect=lambda p:p.read_bytes()):
            with self.assertRaises(deploy.DeployError):deploy._recovery_resolve_stores(self.paths)

    def test_archived_raw_intents_are_a_capability_only_after_known_control_and_exact_own_fence(self):
        self.assertTrue(hasattr(deploy,'_recovery_qualified_preimages'),'Missing qualified raw-preimage capability')
        with self.store._directory() as directory:_ptr=deploy._guard.decode_json(self.store._read(directory,'pending.json'))
        # A source caller cannot virtualize its own missing intent merely from a copied JSON body.
        with patch.object(deploy,'_recovery_root_read',side_effect=lambda p:p.read_bytes()):
            self.assertEqual(deploy._recovery_qualified_preimages(self.store,self.control_store,self.attempt_value,self.context,self.paths,ingress=self.ingress),{})
        self.finish()
        a=deploy.RecoveryStore(self.state/'current-v2-recovery-history'/self.attempt_value['nonce'])
        b=deploy.RecoveryStore(self.state/'current-v2-control-history'/self.attempt_value['nonce'])
        with patch.object(deploy,'_recovery_root_read',side_effect=lambda p:p.read_bytes()):
            copies=deploy._recovery_qualified_preimages(a,b,self.attempt_value,self.context,self.paths,ingress=self.ingress)
        self.assertEqual(copies[Path('/var/lib/coolify-release/pending.json')],self.data[self.global_path])


class RecoveryControlCompletionWiringFixture(RecoveryTailFixture):
    feature=RecoveryAssertions.feature

    setUp=RecoveryTailFixture.setUp

    encode=staticmethod(RecoveryObservationFixture.encode)

    entry=RecoveryAdministrativeAdmissionFixture.entry

    successful_application_recovery=RecoveryAdministrativeAdmissionFixture.successful_application_recovery

    finish=RecoveryTailFixture.finish


class RecoveryControlCompletionWiringTests(RecoveryControlCompletionWiringFixture):
    def test_administrative_control_completion_consumes_known_ready_without_repeating_pin_api_or_application(self):
        from contextlib import ExitStack
        with ExitStack() as patches:
            patches.enter_context(patch.object(deploy,'_recovery_resolve_stores',return_value=(self.store,self.control_store,self.attempt_value),create=True))
            patches.enter_context(patch.object(deploy,'_recovery_observe_resolved',return_value=dict(self.context,catalogStage='expanded'),create=True))
            patches.enter_context(patch.object(deploy,'_recovery_recorded_current',return_value=self.old))
            patches.enter_context(patch.object(deploy,'_recovery_confirm_application',return_value={}))
            patches.enter_context(patch.object(deploy,'_recovery_other_ingress_idle',return_value=None))
            patches.enter_context(patch.object(deploy,'_recovery_verified_receiver',side_effect=AssertionError('No RPC client for known completed control')))
            patches.enter_context(patch.object(deploy,'_recovery_require_running_observer',return_value=self.observer))
            patches.enter_context(patch.object(deploy,'_recovery_control_phases',side_effect=AssertionError('Known ready must not replay producers')))
            finish=patches.enter_context(patch.object(deploy,'_recovery_finish_control_tail',return_value={'status':'current-v2-recovered','complete':True}))
            try:result=deploy._recovery_complete_control(self.old,self.paths,lambda *a,**k:self.fail('No redeploy'),readonly=False,lease=object())
            except (KeyError,deploy.DeployError) as error:self.fail('Missing known-ready adapter to qualified tail: '+str(error))
        self.assertIs(result['complete'],True)
        self.assertEqual(finish.call_count,1)

    def test_tail_can_be_resumed_using_already_resolved_archived_stores(self):
        self.finish()
        a=deploy.RecoveryStore(self.state/'current-v2-recovery-history'/self.attempt_value['nonce'])
        b=deploy.RecoveryStore(self.state/'current-v2-control-history'/self.attempt_value['nonce'])
        with patch.object(deploy,'_recovery_root_read',side_effect=lambda p:p.read_bytes()):
            try:result=deploy._recovery_finish_control_tail(a,b,self.attempt_value,self.context,self.paths,ingress=self.ingress,confirm=lambda:None)
            except deploy.DeployError as error:self.fail('An exact already-resolved own archive must be reusable: '+str(error))
        self.assertIs(result['complete'],True)


class RecoveryCompletedSteadyFixture(RecoveryTailFixture):
    feature=RecoveryAssertions.feature

    setUp=RecoveryTailFixture.setUp

    encode=staticmethod(RecoveryObservationFixture.encode)

    entry=RecoveryAdministrativeAdmissionFixture.entry

    successful_application_recovery=RecoveryAdministrativeAdmissionFixture.successful_application_recovery

    finish=RecoveryTailFixture.finish


class RecoveryCompletedSteadyTests(RecoveryCompletedSteadyFixture):
    def test_completed_compatibility_is_usable_after_archiving_without_new_effects_or_a_ghost_fence(self):
        self.finish()
        a=deploy.RecoveryStore(self.state/'current-v2-recovery-history'/self.attempt_value['nonce'])
        b=deploy.RecoveryStore(self.state/'current-v2-control-history'/self.attempt_value['nonce'])
        context=dict(self.context,catalogStage='expanded')
        with patch.object(deploy,'_recovery_resolve_stores',return_value=(a,b,self.attempt_value)), \
                patch.object(deploy,'_recovery_recorded_current',return_value=self.old), \
                patch.object(deploy,'_recovery_observe_resolved',return_value=context), \
                patch.object(deploy,'_recovery_confirm_application',return_value={}), \
                patch.object(deploy,'_recovery_root_read',side_effect=lambda p:p.read_bytes()), \
                patch.object(deploy.os,'fsync',side_effect=AssertionError('Readonly steady never fsyncs')):
            self.assertIs(deploy._recovery_current_steady(self.old,self.paths,lambda *a,**k:self.fail('No deployment commands')),True)
        self.assertFalse((self.ingress/'duora-current-v2-recovery/pending.json').exists())

    def test_completed_steady_does_not_turn_a_new_incoming_release_into_the_recorded_current(self):
        self.finish()
        a=deploy.RecoveryStore(self.state/'current-v2-recovery-history'/self.attempt_value['nonce'])
        b=deploy.RecoveryStore(self.state/'current-v2-control-history'/self.attempt_value['nonce'])
        with patch.object(deploy,'_recovery_resolve_stores',return_value=(a,b,self.attempt_value)), \
                patch.object(deploy,'_recovery_recorded_current',return_value=self.old), \
                patch.object(deploy,'_recovery_observe_resolved',side_effect=AssertionError('Different release is a normal candidate, not steady')):
            self.assertIs(deploy._recovery_current_steady(self.new,self.paths,lambda *a,**k:self.fail('No deployment commands')),False)


class RecoveryObserverTerminalPublicationFixture(RecoveryControlEngineFixture):
    feature=RecoveryAssertions.feature

    setUp=RecoveryControlEngineFixture.setUp

    encode=staticmethod(RecoveryObservationFixture.encode)

    phases=RecoveryControlEngineFixture.phases


class RecoveryObserverTerminalPublicationTests(RecoveryObserverTerminalPublicationFixture):
    def test_exact_readonly_child_does_not_consume_an_inflight_empty_partial_or_changing_terminal_as_authority(self):
        (self.local/'ingress').mkdir(mode=0o700)
        self.phases()
        (self.store.directory/'control-ready.json').unlink()  # Parent has not published control-ready in this interleaving.
        terminal=self.store.directory/'operation-control-observer-terminal.json'
        intent=json.loads((self.store.directory/'operation-control-observer-intent.json').read_bytes())
        known={'environment-preimage.json':hashlib.sha256((self.store.directory/'environment-preimage.json').read_bytes()).hexdigest()}
        for raw in (b'',b'{"schema":1,',b'not a terminal'):
            terminal.write_bytes(raw)
            before={p.name:p.read_bytes() for p in self.store.directory.iterdir()}
            with self.subTest(raw=raw):
                try:self.store.require_terminal_operations(outcomes=known,active_observer_sha256=intent['commandSha256'])
                except deploy.DeployError as error:self.fail('Readonly child consumed an incompletely published observer terminal: '+str(error))
                self.assertEqual({p.name:p.read_bytes() for p in self.store.directory.iterdir()},before)
                with self.assertRaises(deploy.DeployError):self.store.require_terminal_operations(outcomes=known,durable=True)
        original=self.store._read
        def changing(directory,name):
            if name=='operation-control-observer-terminal.json':raise AssertionError('Do not read a concurrently published terminal in the readonly child')
            return original(directory,name)
        with patch.object(self.store,'_read',side_effect=changing), \
                patch.object(deploy.os,'fsync',side_effect=AssertionError('Readonly child never syncs/writes')):
            self.store.require_terminal_operations(outcomes=known,active_observer_sha256=intent['commandSha256'])

    def test_ignoring_a_publishing_terminal_never_relaxes_the_exact_intent_or_other_phase_ack(self):
        (self.local/'ingress').mkdir(mode=0o700);self.phases()
        (self.store.directory/'control-ready.json').unlink()
        intent=json.loads((self.store.directory/'operation-control-observer-intent.json').read_bytes())
        (self.store.directory/'operation-control-observer-terminal.json').write_bytes(b'')
        known={'environment-preimage.json':hashlib.sha256((self.store.directory/'environment-preimage.json').read_bytes()).hexdigest()}
        with self.assertRaises(deploy.DeployError):self.store.require_terminal_operations(outcomes=known,active_observer_sha256='f'*64)
        (self.store.directory/'operation-control-pin-terminal.json').write_bytes(b'')
        with self.assertRaises(deploy.DeployError):self.store.require_terminal_operations(outcomes=known,active_observer_sha256=intent['commandSha256'])


class RecoveryNextForwardOriginFixture(RecoveryTailFixture):
    feature=RecoveryAssertions.feature

    setUp=RecoveryTailFixture.setUp

    encode=staticmethod(RecoveryObservationFixture.encode)

    entry=RecoveryAdministrativeAdmissionFixture.entry

    successful_application_recovery=RecoveryAdministrativeAdmissionFixture.successful_application_recovery

    finish=RecoveryTailFixture.finish

    def origin(self):
        from contextlib import ExitStack
        self.assertTrue(hasattr(deploy,'_recovery_expanded_origin'),'Missing explicit proof for a future forward on retained schema48')
        a=deploy.RecoveryStore(self.state/'current-v2-recovery-history'/self.attempt_value['nonce'])
        app=json.loads((a.directory/'application-outcome.json').read_bytes())
        with ExitStack() as patches:
            patches.enter_context(patch.object(deploy,'_recovery_root_read',side_effect=lambda p:p.read_bytes()))
            patches.enter_context(patch.object(deploy,'_recovery_require_cached_images',return_value=app['imageIds']))
            patches.enter_context(patch.object(deploy,'_recovery_require_running_applications',return_value=app['runtimeApps']))
            patches.enter_context(patch.object(deploy,'_recovery_require_quiescent',return_value=None))
            patches.enter_context(patch.object(deploy,'_recovery_acl_witness',return_value=None))
            return deploy._recovery_expanded_origin(self.old,self.context,self.paths,run=lambda *a,**k:self.fail('No producer command'))


class RecoveryNextForwardOriginTests(RecoveryNextForwardOriginFixture):
    def test_retained_48_requires_the_completed_immutable_old_recovery_not_just_its_head(self):
        self.finish()
        origin=self.origin()
        self.assertEqual(origin['catalogSha256'],deploy.RECOVERY_CATALOG_48_SHA256)
        self.assertEqual(origin['compatibilitySha256'],hashlib.sha256((self.state/'current-v2-compatibility.json').read_bytes()).hexdigest())
        self.assertEqual(origin['attemptSha256'],json.loads((self.state/'current-v2-recovery-history'/self.attempt_value['nonce']/'pending.json').read_bytes())['sha256'])
        (self.state/'current-v2-compatibility.json').write_text('{"unknown":true}\n')
        with self.assertRaises(deploy.DeployError):self.origin()

    def test_new_attempt_binds_its_expanded_origin_before_any_worker_migration_or_privilege_producer(self):
        self.finish();origin=self.origin()
        with patch.object(deploy,'_recovery_expanded_origin',return_value=origin), \
                patch.object(deploy,'_recovery_require_cached_images',return_value={}):
            try:store=cast(deploy.RecoveryStore, deploy.arm_current_v2_deploy(self.new,self.paths,
                (Path(__file__).parent/'fixtures/recovery-48-privileges.sql').read_text(),observe=lambda:self.context))
            except deploy.DeployError as error:self.fail('A fully qualified retained48 forward is not admitted: '+str(error))
        attempt=store.load()
        self.assertEqual(attempt['expandedOrigin'],origin)
        self.assertEqual(set(p.name for p in store.directory.iterdir()),{'pending.json','attempt-'+attempt['nonce']+'.json'})
        self.assertEqual(deploy.qualify_recovery_attempt(attempt,current=self.old,incoming=self.new,
            bindings=self.context['bindings'],control=self.context['control'],applied_catalog=CATALOG),'expanded')
        with self.assertRaises(deploy.DeployError):deploy.qualify_recovery_attempt(attempt,current=self.old,incoming=self.new,
            bindings=self.context['bindings'],control=self.context['control'],applied_catalog=CATALOG[:35])

    def test_missing_compatibility_proof_or_intermediate_schema_does_not_arm_an_expanded_origin(self):
        self.assertTrue(hasattr(deploy,'_recovery_expanded_origin'),'No broad head48 fallback is allowed')
        with patch.object(deploy,'_recovery_root_read',side_effect=lambda p:p.read_bytes()), \
                patch.object(deploy,'_recovery_require_cached_images',return_value={}):
            with self.assertRaises(deploy.DeployError):deploy.arm_current_v2_deploy(self.new,self.paths,
                (Path(__file__).parent/'fixtures/recovery-48-privileges.sql').read_text(),observe=lambda:self.context)
        context=dict(self.context,catalog=CATALOG[:40])
        with self.assertRaises(deploy.DeployError):deploy.arm_current_v2_deploy(self.new,self.paths,
            (Path(__file__).parent/'fixtures/recovery-48-privileges.sql').read_text(),observe=lambda:context)


class RecoveryFinalObserverWitnessFixture(RecoveryTailFixture):
    feature=RecoveryAssertions.feature

    setUp=RecoveryTailFixture.setUp

    encode=staticmethod(RecoveryObservationFixture.encode)

    entry=RecoveryAdministrativeAdmissionFixture.entry

    successful_application_recovery=RecoveryAdministrativeAdmissionFixture.successful_application_recovery


class RecoveryFinalObserverWitnessTests(RecoveryFinalObserverWitnessFixture):
    def test_a_changed_observer_during_tail_is_not_accepted_as_the_known_controlled_runtime(self):
        from contextlib import ExitStack
        changed=dict(self.observer,id='9'*64)
        def finish(*a,**kwargs):kwargs['confirm']();return {'complete':True}
        with ExitStack() as patches:
            patches.enter_context(patch.object(deploy,'_recovery_resolve_stores',return_value=(self.store,self.control_store,self.attempt_value)))
            patches.enter_context(patch.object(deploy,'_recovery_observe_resolved',return_value=dict(self.context,catalogStage='expanded')))
            patches.enter_context(patch.object(deploy,'_recovery_recorded_current',return_value=self.old))
            patches.enter_context(patch.object(deploy,'_recovery_confirm_application',return_value={}))
            patches.enter_context(patch.object(deploy,'_recovery_other_ingress_idle',return_value=None))
            patches.enter_context(patch.object(deploy,'_recovery_require_running_observer',side_effect=[self.observer,changed]))
            patches.enter_context(patch.object(deploy,'_recovery_finish_control_tail',side_effect=finish))
            with self.assertRaises(deploy.DeployError):deploy._recovery_complete_control(self.old,self.paths,lambda *a,**k:self.fail('No redeploy'),readonly=False,lease=object())


class RecoveryReadyJobCrosslinkFixture(RecoveryTailFixture):
    feature=RecoveryAssertions.feature

    setUp=RecoveryTailFixture.setUp

    encode=staticmethod(RecoveryObservationFixture.encode)

    entry=RecoveryAdministrativeAdmissionFixture.entry

    successful_application_recovery=RecoveryAdministrativeAdmissionFixture.successful_application_recovery

    finish=RecoveryTailFixture.finish


class RecoveryReadyJobCrosslinkTests(RecoveryReadyJobCrosslinkFixture):
    def test_foreign_but_well_formed_ready_job_is_not_the_acknowledged_observer_operation(self):
        path=self.control_store.directory/'control-ready.json';value=json.loads(path.read_bytes())
        value['deploymentUuid']='foreign-job456';path.write_text(json.dumps(value)+'\n')
        with self.assertRaises(deploy.DeployError):self.finish()
        self.assertTrue((self.ingress/'pending.json').is_file())
        self.assertTrue((self.ingress/'duora/pending.json').is_file())
        self.assertTrue((self.ingress/'duora-current-v2-recovery/pending.json').is_file())
        with self.assertRaises(deploy.DeployError):deploy._recovery_control_evidence(self.control_store,self.attempt_value,self.context,inflight=True)


class RecoveryNormalRollbackAfterForwardFixture(RecoveryTailFixture):
    feature=RecoveryAssertions.feature

    setUp=RecoveryTailFixture.setUp

    encode=staticmethod(RecoveryObservationFixture.encode)

    entry=RecoveryAdministrativeAdmissionFixture.entry

    successful_application_recovery=RecoveryAdministrativeAdmissionFixture.successful_application_recovery

    finish=RecoveryTailFixture.finish

    def record_successful_forward_fixture(self):
        self.finish()
        for release in (self.old,self.new):
            deploy._stage_release_files(release,self.state)
            deploy._record_release(release,self.state)
            deploy._mark_current(release,self.state)


class RecoveryNormalRollbackAfterForwardTests(RecoveryNormalRollbackAfterForwardFixture):
    def test_historical_compatibility_does_not_intercept_the_existing_normal_previous_rollback_after_forward_success(self):
        self.record_successful_forward_fixture()
        original=(self.state/'current-v2-compatibility.json').read_bytes()
        run=FakeRunner(pre_head=CATALOG[-1],migration_head=CATALOG[-1])
        with patch.object(deploy,'_recovery_root_read',side_effect=lambda p:p.read_bytes()), \
                patch.object(deploy,'_recovery_observe_resolved',side_effect=AssertionError('Old compatibility is not the registered current anymore')), \
                patch.object(deploy,'runtime_preflight',return_value=None),patch.object(deploy,'runtime_recheck',return_value={}):
            try:deploy.deploy(self.old,self.paths,run,sleep=lambda _:None)
            except AssertionError as error:self.fail('Historical compatibility intercepted normal previous rollback: '+str(error))
        self.assertEqual(json.loads((self.state/'current.json').read_bytes())['releaseId'],self.old.release_id)
        self.assertFalse(any(('run' in command and 'migrator' in command) or '--single-transaction' in command for command in run.commands))
        self.assertEqual((self.state/'current-v2-compatibility.json').read_bytes(),original)

    def test_a_torn_registered_current_or_unknown_active_attempt_still_refuses_before_a_pull(self):
        self.record_successful_forward_fixture()
        (self.state/'current.json').write_text('{')
        run=FakeRunner(pre_head=CATALOG[-1],migration_head=CATALOG[-1])
        with patch.object(deploy,'_recovery_root_read',side_effect=lambda p:p.read_bytes()):
            with self.assertRaises(deploy.DeployError):deploy.deploy(self.old,self.paths,run)
        self.assertEqual(run.commands,[])
        (self.state/'current.json').write_text(json.dumps({'releaseId':self.new.release_id,'commit':self.new.commit,'deployedAt':'2026-10-25T19:00:00Z'}))
        active=deploy.RecoveryStore(self.state/'current-v2-recovery');active.arm(self.attempt_value)
        with self.assertRaises(deploy.DeployError):active.run_operation('migration','a'*64,lambda:(_ for _ in ()).throw(deploy.DeployError('Synthetic UNKNOWN')))
        with patch.object(deploy,'_recovery_root_read',side_effect=lambda p:p.read_bytes()), \
                patch.object(deploy,'runtime_preflight',side_effect=AssertionError('UNKNOWN must refuse before normal qualification')):
            with self.assertRaises(deploy.DeployError):deploy.deploy(self.old,self.paths,run)
        self.assertEqual(run.commands,[])


class RecoveryImmutableExpandedOriginFixture(RecoveryTailFixture):
    feature=RecoveryAssertions.feature

    setUp=RecoveryTailFixture.setUp

    encode=staticmethod(RecoveryObservationFixture.encode)

    entry=RecoveryAdministrativeAdmissionFixture.entry

    successful_application_recovery=RecoveryAdministrativeAdmissionFixture.successful_application_recovery

    finish=RecoveryTailFixture.finish


class RecoveryImmutableExpandedOriginTests(RecoveryImmutableExpandedOriginFixture):
    def test_an_admitted_origin_uses_its_immutable_prior_pointer_even_after_the_global_pointer_advances(self):
        self.finish()
        raw=(self.state/'current-v2-compatibility.json').read_bytes()
        pointer=json.loads(raw)
        with patch.object(deploy,'_recovery_root_read',side_effect=lambda p:p.read_bytes()):
            origin=deploy._recovery_origin_proof(self.old,self.context,self.paths)
            (self.state/'current-v2-compatibility.json').write_text('{"later-global-pointer":true}\n')
            try:observed=deploy._recovery_origin_proof(self.old,self.context,self.paths,admitted_origin=origin)
            except TypeError:self.fail('Expanded origin must carry and verify an immutable prior pointer snapshot')
        self.assertEqual(observed,origin)
        self.assertEqual(origin['pointer'],pointer)
        self.assertEqual(origin['compatibilitySha256'],hashlib.sha256(raw).hexdigest())

    def test_origin_snapshot_is_bound_in_the_attempt_and_cannot_reference_another_release_or_nonce(self):
        self.finish()
        raw=(self.state/'current-v2-compatibility.json').read_bytes()
        pointer=json.loads(raw)
        origin={'catalogSha256':deploy.RECOVERY_CATALOG_48_SHA256,'compatibilitySha256':hashlib.sha256(raw).hexdigest(),
            'attemptSha256':pointer['attemptSha256'],'pointer':pointer}
        try:attempt=deploy.make_recovery_attempt(self.old,self.new,sql_sha256=deploy.RECOVERY_SQL_48_SHA256,
            bindings=self.context['bindings'],control=self.context['control'],expanded_origin=origin)
        except deploy.DeployError as error:self.fail('Valid immutable origin snapshot is not admitted: '+str(error))
        self.assertEqual(attempt['expandedOrigin']['pointer'],pointer)
        for change in (lambda o:o['pointer'].update(nonce='f'*32),lambda o:o['pointer'].update(releaseId='foreign-current'),
                lambda o:o.update(compatibilitySha256='e'*64),lambda o:o.update(attemptSha256='a'*64)):
            value=copy.deepcopy(origin);change(value)
            with self.assertRaises(deploy.DeployError):deploy.make_recovery_attempt(self.old,self.new,sql_sha256=deploy.RECOVERY_SQL_48_SHA256,
                bindings=self.context['bindings'],control=self.context['control'],expanded_origin=value)


class RecoveryTwoCycleRealFrameFixture(RecoveryTailFixture):
    """Synthetic runtime metadata, REAL protocol/FS and observe_resolved across two failed forwards.

    This is a regression test for binding/ordering, not Linux/root/Docker acceptance.
    """

    feature=RecoveryAssertions.feature

    encode=staticmethod(RecoveryObservationFixture.encode)

    entry=RecoveryAdministrativeAdmissionFixture.entry

    successful_application_recovery=RecoveryAdministrativeAdmissionFixture.successful_application_recovery

    def setUp(self):
        from contextlib import ExitStack
        RecoveryTailFixture.setUp(self)
        self.paths=deploy.Paths(Path('/opt/duora/compose.coolify.json'),Path('/etc/duora/duora.env'),self.state)
        self.frame=copy.deepcopy(self.data)
        for path,raw in list(self.data.items()):
            if path.is_relative_to(Path('/var/lib/duora')):self.frame[self.state/path.relative_to('/var/lib/duora')]=raw
        self.frame[Path('/etc/duora/coolify-deploy.json')]=self.context['restoredPinRaw']
        self.aliases={Path('/var/lib/coolify-release/pending.json'):self.ingress/'pending.json',
            Path('/var/lib/coolify-release/duora/pending.json'):self.ingress/'duora/pending.json',
            Path('/var/lib/coolify-release/duora-current-v2-recovery/pending.json'):self.ingress/'duora-current-v2-recovery/pending.json'}
        self.stack=ExitStack();self.addCleanup(self.stack.close)
        def read(path):
            if path in self.aliases:return self.aliases[path].read_bytes()
            if path.exists():return path.read_bytes()
            if path in self.frame:return self.frame[path]
            raise FileNotFoundError(str(path))
        self.stack.enter_context(patch.object(deploy,'_recovery_root_read',side_effect=read))
        preimages=deploy._recovery_qualified_preimages
        self.stack.enter_context(patch.object(deploy,'_recovery_qualified_preimages',side_effect=lambda a,b,t,c,p:
            preimages(a,b,t,c,p,ingress=self.ingress)))
        self.stack.enter_context(patch.object(deploy,'runtime_recheck',return_value={'profile':{'services':{'postgres':{'id':'1'*64}}}}))
        self.stack.enter_context(patch.object(deploy,'_query',return_value=json.dumps(CATALOG)))
        self.application=json.loads((self.store.directory/'application-outcome.json').read_bytes())
        for name,value in (('_recovery_require_cached_images',self.application['imageIds']),
                ('_recovery_require_running_applications',self.application['runtimeApps']),('_minimum_contract_v2',True),
                ('_recovery_require_quiescent',None),('_recovery_acl_witness',None),('_require_no_privileged_account_without_mfa',None),
                ('_wait_ready',None),('_require_services_running',None)):
            self.stack.enter_context(patch.object(deploy,name,return_value=value))
        self.runner_under_test=lambda *a,**k:self.fail('No application producer is authorized by a tail observation')

    def confirm_real_frame(self):
        a,b,attempt=cast(tuple[deploy.RecoveryStore, deploy.RecoveryStore, dict], deploy._recovery_resolve_stores(self.paths))
        context=deploy._recovery_observe_resolved(a,b,attempt,self.paths)
        deploy._recovery_confirm_application(a,attempt,context,self.paths,self.runner_under_test)
        return a,b,context

    def prepare_second(self):
        from types import SimpleNamespace
        deploy._recovery_finish_control_tail(self.store,self.control_store,self.attempt_value,self.context,self.paths,
            ingress=self.ingress,confirm=lambda:self.confirm_real_frame())
        self.first_pointer=(self.state/'current-v2-compatibility.json').read_bytes()
        self.first_archives={str(p.relative_to(self.state)):p.read_bytes() for root in
            (self.state/'current-v2-recovery-history',self.state/'current-v2-control-history') for p in root.rglob('*') if p.is_file()}
        second_raw=copy.deepcopy(self.new.raw);second_raw.update(releaseId='new-attempt-two',commit='c'*40)
        second=parse(second_raw)
        new_opt=Path('/opt/duora/releases/new-attempt-two.json');self.frame[new_opt]=self.encode(second.raw)
        pin=json.loads(self.frame[Path('/etc/duora/coolify-deploy.json')])
        pin['manifest'],pin['manifest_sha256']=str(new_opt),hashlib.sha256(self.frame[new_opt]).hexdigest()
        pin['observer_environment']['DUORA_OBSERVER_IMAGE']=second.images['edge']
        self.frame[Path('/etc/duora/coolify-deploy.json')]=self.encode(pin)
        global_raw=self.encode({'project':'duora','commit':second.commit,'status':'importing'})
        project_raw=self.encode({'release':{'project':'duora','commit':second.commit,'duora':second.raw},'deployment_uuid':'second-job456'})
        for path,raw in ((self.ingress/'pending.json',global_raw),(self.ingress/'duora/pending.json',project_raw)):
            path.write_bytes(raw);path.chmod(0o600)
        context=deploy.observe_current_v2_recovery(second,self.paths)
        a=cast(deploy.RecoveryStore, deploy.arm_current_v2_deploy(second,self.paths,(Path(__file__).parent/'fixtures/recovery-48-privileges.sql').read_text(),
            observe=lambda:context,run=self.runner_under_test))
        attempt=a.load()
        for phase in ('worker-stop','migration'):
            a.run_operation(phase,'a'*64,lambda:subprocess.CompletedProcess(['synthetic'],0,'',''))
        with a._directory() as directory:
            pointer=deploy._guard.decode_json(a._read(directory,'pending.json'))
            # Tail-specific fixture: this app result is synthetic; application execution has independent cases.
            outcome=dict(self.application,attemptSha256=pointer['sha256'],controlSha256=deploy._guard.digest(attempt['control']))
            a._write(directory,'application-outcome.json',outcome)
        context=deploy.observe_current_v2_control(attempt,self.paths)
        b=deploy.RecoveryStore(self.state/'current-v2-control')
        def api(path,method='GET',payload=None):
            if path.endswith('/envs'):return [{'key':'DUORA_OBSERVER_IMAGE','is_preview':False,'is_literal':True,'is_shown_once':True}]
            if path.endswith('/start'):return {'deployment_uuid':'second-control789'}
            if path=='deployments/second-control789':return {'status':'finished'}
            self.fail('Unexpected synthetic API '+path)
        receiver=SimpleNamespace(api=api,update_envs=lambda *a:None,require_idle=lambda *a:None,deployment_finished=lambda r:r['status']=='finished')
        lease=SimpleNamespace(release_executor=lambda:None,reacquire_executor=lambda:None)
        original=deploy._recovery_apply_pin
        def inverse(path,**kwargs):
            if path==Path('/etc/duora/coolify-deploy.json'):
                self.frame[path]=kwargs['restored_raw'];return {'status':'restored','sha256':hashlib.sha256(kwargs['restored_raw']).hexdigest()}
            return original(path,**kwargs)
        self.stack.enter_context(patch.object(deploy,'_recovery_apply_pin',side_effect=inverse))
        deploy._recovery_control_phases(b,attempt,context,self.paths,receiver=receiver,lease=lease,
            confirm_application=lambda:self.confirm_real_frame(),observe_frame=lambda:self.confirm_real_frame()[2],
            observe_observer=lambda:self.observer,ingress=self.ingress)
        return a,b,attempt,context

    def second_tail(self,a,b,attempt,context,confirm=None):
        return deploy._recovery_finish_control_tail(a,b,attempt,context,self.paths,ingress=self.ingress,
            confirm=(lambda:self.confirm_real_frame()) if confirm is None else confirm)

    def assert_second_complete(self,attempt):
        self.assertEqual(json.loads((self.state/'current-v2-compatibility.json').read_bytes())['nonce'],attempt['nonce'])
        for name,raw in self.first_archives.items():self.assertEqual((self.state/name).read_bytes(),raw)
        self.assertEqual(attempt['expandedOrigin']['pointer'],json.loads(self.first_pointer))
        self.assertFalse((self.ingress/'duora-current-v2-recovery/pending.json').exists())
        self.confirm_real_frame()  # Actual observeresolved, not a confirm mock, after new global pointer and both archives.


class RecoveryTwoCycleRealFrameTests(RecoveryTwoCycleRealFrameFixture):
    def test_second_recovery_resumes_after_new_pointer_publication_before_any_archive(self):
        a,b,attempt,context=self.prepare_second()
        with patch.object(deploy.os,'rename',side_effect=OSError('Synthetic after pointer / before archive')):
            with self.assertRaises(deploy.DeployError):self.second_tail(a,b,attempt,context)
        self.assertNotEqual((self.state/'current-v2-compatibility.json').read_bytes(),self.first_pointer)
        self.confirm_real_frame()
        self.assertIs(self.second_tail(a,b,attempt,context)['complete'],True)
        self.assert_second_complete(attempt)

    def test_second_recovery_resumes_after_both_archives_while_the_own_fence_is_still_held(self):
        a,b,attempt,context=self.prepare_second()
        def interrupted_confirm():
            current_a,current_b,_=self.confirm_real_frame()
            if current_a.directory.parent.name=='current-v2-recovery-history' and current_b.directory.parent.name=='current-v2-control-history':
                raise deploy.DeployError('Synthetic after both archives / before fence release')
        with self.assertRaises(deploy.DeployError):self.second_tail(a,b,attempt,context,confirm=interrupted_confirm)
        self.assertTrue((self.ingress/'duora-current-v2-recovery/pending.json').is_file())
        self.confirm_real_frame()
        self.assertIs(self.second_tail(a,b,attempt,context)['complete'],True)
        self.assert_second_complete(attempt)


