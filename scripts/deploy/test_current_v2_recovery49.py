"""Proposed pure RED cases for the exact next pair. Not native or production proof.

Run only through the existing remote PR260 CI after its native selector is scoped
away from the already closed 35/48 lab. This module has not been executed locally.
"""
import copy
import hashlib
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import duora_deploy as deploy
from test_duora_deploy import FakeRunner, valid_manifest, parse

CATALOG = json.loads((Path(__file__).parent / "fixtures/recovery-48-49-catalog.json").read_text())
SQL = (Path(__file__).parent / "fixtures/recovery-49-privileges.sql").read_text()
PUBLICATION_IMAGES = json.loads((Path(__file__).parent / "fixtures/recovery-c934-publication-roots.json").read_text())
OLD_COMMIT = "c9341ab39a5e3133067eb579021efdd8c19b0033"
CURRENT_IMAGES = json.loads((Path(__file__).parent / "fixtures/recovery-current-c934-images.json").read_text())
SQL_HASH = "ee88da57b072243a02a1c71f8ee2c2c5a00190dc53f81bde34ca15fc70dc6685"


class _Exact48To49Fixture(unittest.TestCase):
    def setUp(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.root = Path(directory.name)
        old = valid_manifest()
        old.update(contractVersion="v2", releaseId="known-current48", commit=OLD_COMMIT,
                   migrations=CATALOG[:-1], schema={"min": CATALOG[-2], "max": CATALOG[-2]}, architecture="amd64")
        old["images"] = dict(CURRENT_IMAGES)
        new = dict(old, releaseId="incoming49", commit="b" * 40,
                   previousReleaseId=old["releaseId"], migrations=CATALOG,
                   schema={"min": CATALOG[-1], "max": CATALOG[-1]})
        self.old, self.new = parse(old), parse(new)
        self.bindings = {name: "a" * 64 for name in deploy.RECOVERY_BINDINGS}
        self.control = {name: "b" * 64 for name in deploy.RECOVERY_CONTROL}
        self.paths = deploy.Paths(self.root / "compose", self.root / "host.env", self.root)
        self.pin = {"manifest": "/opt/duora/releases/known-current48.json",
                    "manifest_sha256": "a" * 64, "compose": "/opt/duora/compose.coolify.json",
                    "compose_sha256": "b" * 64, "application_uuid": "testresource",
                    "observer_environment": {"DUORA_OBSERVER_IMAGE": self.old.images["edge"],
                                             "SOURCE_COMMIT": self.old.commit},
                    "caddy_sha256": "c" * 64}

    def attempt(self, old=None, new=None, sql_hash=SQL_HASH, expanded_origin=None):
        return deploy.make_recovery_attempt(old or self.old, new or self.new,
            sql_sha256=sql_hash, bindings=self.bindings, control=self.control, nonce="1" * 32, expanded_origin=expanded_origin)

    def baseline(self, old=None, pin=None):
        return deploy.make_recovery_control_baseline(old or self.old,
            (json.dumps(pin or self.pin, indent=2) + "\n").encode(), bindings=self.bindings,
            receiver_current_sha256="d" * 64, receiver_config_sha256="e" * 64)


class Exact48To49Tests(_Exact48To49Fixture):
    def test_exact_pair_only_admits_initial48_and_expanded49(self):
        self.assertEqual(len(CATALOG), 49)
        self.assertEqual(hashlib.sha256(SQL.encode()).hexdigest(), SQL_HASH)
        attempt = self.attempt()
        for ids, expected in ((CATALOG[:-1], "initial"), (CATALOG, "expanded")):
            self.assertEqual(deploy.qualify_recovery_attempt(attempt, current=self.old,
                incoming=self.new, bindings=self.bindings, control=self.control,
                applied_catalog=ids), expected)
        self.assertNotIn("success", attempt)
        self.assertNotIn("deployedAt", attempt)
        pointer = {"schema": 1, "releaseId": self.old.release_id, "nonce": "2" * 32,
                   "attemptSha256": "d" * 64, "applicationOutcomeSha256": "e" * 64,
                   "controlOutcomeSha256": "f" * 64}
        raw = (json.dumps(pointer, sort_keys=True, separators=(",", ":")) + "\n").encode()
        origin = {"catalogSha256": "56054aa8ca5b2ec32f9c687ddc57ab21c4251aa9fbc31c5fc86f9a2ee45de5d8",
                  "compatibilitySha256": hashlib.sha256(raw).hexdigest(),
                  "attemptSha256": "d" * 64, "pointer": pointer}
        expanded = self.attempt(expanded_origin=origin)
        self.assertEqual(deploy.qualify_recovery_attempt(expanded, current=self.old,
            incoming=self.new, bindings=self.bindings, control=self.control,
            applied_catalog=CATALOG), "expanded")
        with self.assertRaises(deploy.DeployError):
            deploy.qualify_recovery_attempt(expanded, current=self.old,
                incoming=self.new, bindings=self.bindings, control=self.control,
                applied_catalog=CATALOG[:-1])

    def test_another_app_unknown_catalog_or_payload_never_arms(self):
        for old in (parse(dict(self.old.raw, images=dict(PUBLICATION_IMAGES))),
                    parse(dict(self.old.raw, images=dict(self.old.images, web="ghcr.io/bipoxdxd/duora-web@sha256:" + "c" * 64))),
                    parse(dict(self.old.raw, commit="c" * 40)),
                    parse(dict(self.old.raw, contractVersion="v1"))):
            with self.subTest(old=old.commit), self.assertRaises(deploy.DeployError):
                self.attempt(old=old)
        for new in (parse(dict(self.new.raw, previousReleaseId=None)),
                    parse(dict(self.new.raw, migrations=CATALOG + ["20990101000000_Unknown"],
                               schema={"min": CATALOG[-1], "max": "20990101000000_Unknown"}))):
            with self.subTest(new=new.latest_migration), self.assertRaises(deploy.DeployError):
                self.attempt(new=new)
        with self.assertRaises(deploy.DeployError): self.attempt(sql_hash="f" * 64)
        self.assertEqual(list(self.root.iterdir()), [])

    def test_control_can_bind_exact_current48_without_mutating_pin(self):
        before = copy.deepcopy(self.pin)
        baseline = self.baseline()
        self.assertEqual(baseline["currentReleaseId"], self.old.release_id)
        self.assertEqual(baseline["bindings"], self.bindings)
        self.assertEqual(self.pin, before)
        self.assertNotIn("observer_environment", baseline)

    def test_foreign_current_or_noncanonical_pin_cannot_be_enrolled(self):
        wrong = parse(dict(self.old.raw, commit="c" * 40))
        with self.assertRaises(deploy.DeployError): self.baseline(old=wrong)
        pin = copy.deepcopy(self.pin)
        pin["manifest"] = "/opt/duora/releases/foreign.json"
        with self.assertRaises(deploy.DeployError): self.baseline(pin=pin)


    def test_missing_control_or_wrong49_payload_refuses_without_receipt(self):
        context = {"current": self.old, "bindings": self.bindings,
                   "control": self.control, "catalog": CATALOG[:-1], "postgresId": "1" * 64}
        def refused(): raise deploy.DeployError("No qualified current48 control")
        for payload, observe in ((SQL, refused), (SQL + "--drift", lambda: context)):
            with patch.object(deploy, "_recovery_require_cached_images", return_value={}):
                with self.assertRaises(deploy.DeployError):
                    deploy.arm_current_v2_deploy(self.new, self.paths, payload, observe=observe)
        self.assertEqual(list(self.root.iterdir()), [])

    def test_49_effective_acl_witness_covers_new_append_only_relation(self):
        for answer in ("t", "f"):
            with patch.object(deploy, "_query", return_value=answer) as query:
                if answer == "t":
                    deploy._recovery_acl_witness(["compose"], FakeRunner(), catalog=CATALOG)
                else:
                    with self.assertRaises(deploy.DeployError):
                        deploy._recovery_acl_witness(["compose"], FakeRunner(), catalog=CATALOG)
                sql = query.call_args.args[2]
                for privilege in ("UPDATE", "DELETE", "TRUNCATE"):
                    self.assertIn("('safety.photo_review_appeal_decision_commands','" + privilege + "')", sql)
                self.assertIn("has_any_column_privilege", sql)

    def test_49_unknown_catalog_never_reaches_acl_query(self):
        with patch.object(deploy, "_query") as query, self.assertRaises(deploy.DeployError):
            deploy._recovery_acl_witness(["compose"], FakeRunner(),
                catalog=CATALOG + ["20990101000000_Unknown"])
        query.assert_not_called()


class NormalLegacyUpgradeDoesNotEnrollV2RecoveryTests(_Exact48To49Fixture):
    def test_normal_v1_to_v2_does_not_request_v2_current_control(self):
        legacy_raw = dict(self.old.raw, contractVersion="v1", migrations=CATALOG[:35],
                          schema={"min": CATALOG[0], "max": CATALOG[34]})
        legacy = parse(legacy_raw)
        incoming_raw = dict(self.new.raw, migrations=CATALOG[:-1],
                            schema={"min": CATALOG[-2], "max": CATALOG[-2]})
        incoming = parse(incoming_raw)
        frames = {self.paths.state_dir / "current.json": json.dumps({"releaseId": legacy.release_id,
                    "commit": legacy.commit, "deployedAt": "2026-10-26T00:00:00+00:00"}).encode(),
                  self.paths.state_dir / "releases" / (legacy.release_id + ".json"): json.dumps(legacy.raw).encode()}
        def protected_input(path):
            if path not in frames:
                raise deploy.DeployError("Synthetic protected reader refuses an unknown path")
            return frames[path]
        with patch.object(deploy, "_recovery_root_read", side_effect=protected_input), \
             patch.object(deploy, "observe_current_v2_recovery", side_effect=AssertionError("v1 must use its normal guarded contract")):
            self.assertIsNone(deploy.arm_current_v2_deploy(incoming, self.paths,
                (Path(__file__).parent / "fixtures/recovery-48-privileges.sql").read_text()))
