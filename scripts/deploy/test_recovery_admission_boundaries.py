"""Only newly changed enrollment-selection boundaries. Pure I/O seams, not native/root evidence."""
import copy
import json
from unittest.mock import patch

import duora_deploy as deploy
from test_current_v2_recovery49 import _Exact48To49Fixture, CATALOG, SQL, CURRENT_IMAGES
from test_duora_deploy import parse


class ChangedArmingBoundariesTests(_Exact48To49Fixture):
    def frame(self, raw):
        record = {"releaseId": raw["releaseId"], "commit": raw["commit"], "deployedAt": "2026-10-26T00:00:00+00:00"}
        return {self.paths.state_dir / "current.json": (json.dumps(record) + "\n").encode(),
                self.paths.state_dir / "releases" / (raw["releaseId"] + ".json"): (json.dumps(raw) + "\n").encode()}

    def read(self, values):
        def actual(path):
            if path not in values:
                raise deploy.DeployError("synthetic protected reader refuses missing input")
            return values[path]
        return actual

    def local48(self):
        raw = copy.deepcopy(self.old.raw)
        raw["images"] = {role: "localhost:5050/duora/" + role + "@" + ref.split("@")[1]
                         if role != "postgres" else ref for role, ref in raw["images"].items()}
        incoming = dict(raw, releaseId="next-local48", previousReleaseId=raw["releaseId"], commit="d" * 40)
        return raw, deploy.parse_manifest(json.dumps(incoming), image_prefix="")

    def test_legacy_v1_normal_upgrade_does_not_request_v2_control(self):
        raw = dict(self.old.raw, contractVersion="v1", migrations=CATALOG[:35],
                   schema={"min": CATALOG[0], "max": CATALOG[34]})
        incoming = parse(dict(self.new.raw, migrations=CATALOG[:-1], schema={"min": CATALOG[-2], "max": CATALOG[-2]}))
        with patch.object(deploy, "_recovery_root_read", side_effect=self.read(self.frame(raw))), \
             patch.object(deploy, "observe_current_v2_recovery", side_effect=AssertionError("no enrollment for legacy normal flow")):
            self.assertIsNone(deploy.arm_current_v2_deploy(incoming, self.paths, "unused normal payload"))

    def test_same_exact48_local_registry_stays_in_normal_flow(self):
        for catalog in (CATALOG[:-1], CATALOG):
            raw, _ = self.local48()
            raw.update(migrations=catalog, schema={"min": catalog[-1], "max": catalog[-1]})
            incoming = deploy.parse_manifest(json.dumps(dict(raw, releaseId="next-local", previousReleaseId=raw["releaseId"], commit="d" * 40)), image_prefix="")
            with self.subTest(length=len(catalog)), patch.object(deploy, "_recovery_root_read", side_effect=self.read(self.frame(raw))), \
                 patch.object(deploy, "observe_current_v2_recovery", side_effect=AssertionError("same-schema must not request finite control")), \
                 patch.object(deploy, "RecoveryStore", side_effect=AssertionError("same-schema must not create any attempt")):
                self.assertIsNone(deploy.arm_current_v2_deploy(incoming, self.paths, "unused same-schema payload"))

    def test_exact_c93448_to49_still_arms_finite_protocol(self):
        captured = []
        class DataOnlyStore:
            def __init__(self, path):
                self.path = path
            def arm(self, value):
                captured.append(value)
        context = {"current": self.old, "bindings": self.bindings, "control": self.control,
                   "catalog": CATALOG[:-1], "postgresId": "1" * 64}
        with patch.object(deploy, "_recovery_require_cached_images", return_value={}), \
             patch.object(deploy, "RecoveryStore", DataOnlyStore):
            result = deploy.arm_current_v2_deploy(self.new, self.paths, SQL, observe=lambda: context)
        self.assertIsInstance(result, DataOnlyStore)
        self.assertEqual(len(captured), 1)
        self.assertEqual(captured[0]["current"]["images"], CURRENT_IMAGES)
        self.assertEqual(captured[0]["incoming"]["migrations"], CATALOG)
        self.assertEqual(captured[0]["sqlSha256"], deploy.RECOVERY_SQL_49_SHA256)

    def test_missing_corrupt_or_mismatched_current_never_skips_admission(self):
        raw, incoming = self.local48()
        valid = self.frame(raw)
        manifest = self.paths.state_dir / "releases" / (raw["releaseId"] + ".json")
        cases = []
        cases.append({})
        cases.append(dict(valid))
        cases[-1][manifest] = b'{"contractVersion":"v2","contractVersion":"v2"}'
        for field, value in (("releaseId", "another-current"), ("commit", "f" * 40),
                             ("architecture", "arm64"), ("migrations", list(reversed(CATALOG[:-1])))):
            changed = copy.deepcopy(raw)
            changed[field] = value
            values = dict(valid)
            values[manifest] = json.dumps(changed).encode()
            cases.append(values)
        for number, values in enumerate(cases):
            with self.subTest(case=number), patch.object(deploy, "_recovery_root_read", side_effect=self.read(values)), \
                 patch.object(deploy, "observe_current_v2_recovery", side_effect=AssertionError("unknown must not be admitted")), \
                 self.assertRaises(deploy.DeployError):
                deploy.arm_current_v2_deploy(incoming, self.paths, "untrusted synthetic payload")
