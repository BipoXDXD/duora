"""Synthetic fixtures only; no Docker, host proc, runtime, or network access."""
import copy
import hashlib
import importlib.util
import json
from pathlib import Path
import unittest

HERE = Path(__file__).parent

def load(name):
    path = HERE / (name + ".py")
    if not path.is_file():
        raise AssertionError("missing protected supplier implementation: " + name)
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module

def admission(module):
    digest = "a" * 64
    value = {"version": 1, "pins_sha256": module.PINS_SHA256,
        "reader_uid": 1234, "exclusive_operator": 69,
        "handoff_sha256": digest,
        "profile": {key: digest for key in module.PROFILE_KEYS},
        "roles": {role: {key: digest for key in module.WITNESS_KEYS} for role in module.ROLES}}
    value["profile"]["public_environment"] = ["PATH=/usr/bin:/bin"]
    value["profile"]["image_digest"] = "sha256:" + digest
    value["profile"]["config_digest"] = "sha256:" + digest
    value["profile"]["capture_source_sha256"] = hashlib.sha256((HERE / "capture.py").read_bytes()).hexdigest()
    value["profile"]["caller_source_sha256"] = hashlib.sha256((HERE / "ram_caller.py").read_bytes()).hexdigest()
    raw = json.dumps(value).encode()
    return module.load_admission(raw, hashlib.sha256(raw).hexdigest())

class FixtureAuthority:
    def __init__(self, module):
        self.module = module
        self.reads = 0
        self.snapshots = 0
        self.changed_after = False
        self.bad_before = False
        self.clock = lambda: 0.0
    def snapshot(self, role, deadline):
        self.snapshots += 1
        value = copy.deepcopy(self.module.ROLES[role])
        value.update(pid=123, kernel_start="12345", boot_id="public-boot", readonly_app=True,
                     readonly_config=True, fixed_mounts=True, reader_uid=1234)
        if self.bad_before or (self.changed_after and self.reads):
            value["kernel_start"] = "changed"
        return value
    def read_process(self, role, identity, deadline):
        self.reads += 1
        return (b"KEY=private-sentinel\0OTHER=a=b\0",
                ("dotnet\0" + self.module.ROLES[role]["assembly"] + "\0--private=argument-sentinel\0").encode(), "/app")

class CaptureTests(unittest.TestCase):
    def test_missing_qualification_stops_before_any_metadata_or_private_read(self):
        m = load("capture")
        t = FixtureAuthority(m)
        with self.assertRaisesRegex(m.Refusal, "UNKNOWN_RUNTIME_PROFILE"):
            m.capture("web", None, t, 5)
        self.assertEqual((t.snapshots, t.reads), (0, 0))
    def test_null_duplicate_keys_unknown_fields_and_wrong_types_are_rejected(self):
        m = load("capture")
        for raw in [b'{"version":1,"version":1}', b'null', b'{}']:
            with self.assertRaises(m.Refusal):
                m.load_admission(raw, hashlib.sha256(raw).hexdigest())
        good = admission(m)
        for mutate in [lambda v: v.update(reader_uid=True),
                       lambda v: v["profile"].update(image_digest=None),
                       lambda v: v["roles"]["web"].update(pid=1)]:
            v = copy.deepcopy(good); mutate(v); raw = json.dumps(v).encode()
            with self.assertRaises(m.Refusal):
                m.load_admission(raw, hashlib.sha256(raw).hexdigest())
    def test_effective_process_env_and_arguments_are_only_private_payload(self):
        m = load("capture"); t = FixtureAuthority(m)
        raw = m.capture("web", admission(m), t, 5)
        value = json.loads(raw)
        self.assertEqual(set(value), {"Role", "ApplicationAssembly", "ContentRoot", "Environment", "Arguments"})
        self.assertEqual(value["Environment"], {"KEY": "private-sentinel", "OTHER": "a=b"})
        self.assertEqual(value["Arguments"], ["--private=argument-sentinel"])
        self.assertEqual(t.reads, 1)
    def test_actual_source_mismatch_prevents_private_reads(self):
        m = load("capture"); t = FixtureAuthority(m)
        original = t.snapshot
        def changed(role, deadline):
            v = original(role, deadline); v["source"] = "0" * 40; return v
        t.snapshot = changed
        with self.assertRaisesRegex(m.Refusal, "C934_CONTEXT_CHANGED"):
            m.capture("web", admission(m), t, 5)
        self.assertEqual(t.reads, 0)
    def test_pid_reuse_after_read_discards_capture(self):
        m = load("capture"); t = FixtureAuthority(m); t.changed_after = True
        with self.assertRaisesRegex(m.Refusal, "C934_CONTEXT_CHANGED"):
            m.capture("web", admission(m), t, 5)
        self.assertEqual(t.reads, 1)
    def test_missing_mount_witness_and_expired_supplier_refuse(self):
        m = load("capture"); t = FixtureAuthority(m)
        v = admission(m); v["roles"]["web"]["mounted_preimage_receipt_sha256"] = None
        with self.assertRaises(m.Refusal): m.capture("web", v, t, 5)
        self.assertEqual(t.reads, 0)
        with self.assertRaisesRegex(m.Refusal, "SUPPLIER_TIMEOUT"):
            m.capture("web", admission(m), t, 0)
    def test_duplicate_env_invalid_utf8_and_oversized_input_fail_closed(self):
        m = load("capture")
        for environ, argv in [(b"A=x\0A=y\0", b"dotnet\0Duora.Web.dll\0"),
                              (b"A=\xff\0", b"dotnet\0Duora.Web.dll\0"),
                              (b"A=" + b"x" * m.MAX_CAPTURE, b"dotnet\0Duora.Web.dll\0")]:
            t = FixtureAuthority(m); t.read_process = lambda *a: (environ, argv, "/app")
            with self.assertRaises(m.Refusal): m.capture("web", admission(m), t, 5)
    def test_declared_profile_runtime_controls_are_accepted_only_at_fixed_values(self):
        m = load("capture"); v = admission(m)
        controls = ["DOTNET_NOLOGO=true", "DOTNET_CLI_TELEMETRY_OPTOUT=true", "DOTNET_EnableDiagnostics=0", "COMPlus_DbgEnableMiniDump=0"]
        v["profile"]["public_environment"] += controls
        raw = json.dumps(v).encode()
        try: accepted = m.load_admission(raw, hashlib.sha256(raw).hexdigest())
        except m.Refusal: accepted = None
        self.assertIsNotNone(accepted, "declared benign Dockerfile runtime controls must be admissible")
        self.assertEqual(accepted["profile"]["public_environment"], v["profile"]["public_environment"])
        for bad in ["DOTNET_EnableDiagnostics=1", "COMPlus_DbgEnableMiniDump=1", "DOTNET_NOLOGO=false", "DOTNET_CLI_TELEMETRY_OPTOUT=false"]:
            changed = copy.deepcopy(v)
            name = bad.split("=", 1)[0]
            changed["profile"]["public_environment"] = [bad if item.startswith(name + "=") else item for item in changed["profile"]["public_environment"]]
            raw = json.dumps(changed).encode()
            with self.assertRaises(m.Refusal): m.load_admission(raw, hashlib.sha256(raw).hexdigest())

    def test_profile_public_environment_rejects_bootstrap_hooks_or_null(self):
        m = load("capture")
        for env in [None, ["PATH=/usr/bin:/bin", "LD_PRELOAD=/private"],
                    ["PATH=/usr/bin:/bin", "PYTHONPATH=/private"],
                    ["PATH=/usr/bin:/bin", "DOTNET_STARTUP_HOOKS=/private"],
                    ["PATH=/usr/bin:/bin", "PATH=/other"]]:
            v = admission(m); v["profile"]["public_environment"] = env
            raw = json.dumps(v).encode()
            with self.assertRaises(m.Refusal): m.load_admission(raw, hashlib.sha256(raw).hexdigest())

    def test_native_leaf_inspection_uses_full_fixed_repository_digest_reference(self):
        m = load("capture")
        for role in ("web", "worker"):
            seen = []
            class PublicFixture:
                def public(self, argv, deadline):
                    seen.append(argv)
                    if len(seen) == 2: raise m.Refusal("FIXTURE_STOP_BEFORE_PROC")
                    return json.dumps({"running": True, "readonly": True}).encode()
            reader = object.__new__(m.LinuxAuthority)
            reader.docker = PublicFixture(); reader.handles = {}; reader.clock = lambda: 0
            with self.assertRaises(m.Refusal): reader.snapshot(role, 5)
            self.assertEqual(len(seen), 2)
            self.assertEqual(seen[1][-1], "ghcr.io/bipoxdxd/duora-" + role + "@" + m.ROLES[role]["native"])

    def test_stat_parser_handles_spaces_parentheses_and_uses_kernel_start_field(self):
        m = load("capture")
        fields = ["S"] + ["0"] * 18 + ["67890"] + ["0"] * 3
        self.assertEqual(m.parse_start_stat(("123 (name ) with spaces) " + " ".join(fields)).encode(), 123), "67890")
        with self.assertRaises(m.Refusal): m.parse_start_stat(b"124 (other) S 0", 123)

if __name__ == "__main__": unittest.main()
