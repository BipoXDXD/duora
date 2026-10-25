"""New pure tests: real frozen public manifests, synthetic Docker metadata; no runtime evidence."""
import copy
import hashlib
import json
from pathlib import Path
import unittest

from old_image_identity import bind_old_image, validate_old_image_receipt

HERE = Path(__file__).resolve().parent
PINS = json.loads((HERE / "current_schema_pins.json").read_bytes())

def fixture(role="edge", store="containerd"):
    pin = PINS["old_images"][role]
    raw = (HERE / "old-oci" / (role + ".json")).read_bytes()
    leaf = pin["reference"].split("@")[1]
    labels = {"org.opencontainers.image.source": "https://github.com/BipoXDXD/duora",
              "org.opencontainers.image.revision": PINS["old_commit"],
              "org.opencontainers.image.title": "duora-" + role, "io.duora.postgres.version": "18.6"}
    return raw, {"Id": leaf if store == "containerd" else pin["config_digest"], "Os": "linux",
                 "Architecture": "amd64", "RepoDigests": [pin["reference"]], "Config": {"Labels": labels},
                 "Descriptor": {"digest": leaf}}, pin

def bind(raw, observed, pin, role="edge"):
    return bind_old_image(raw, observed, pin, role=role, revision=PINS["old_commit"])

class OldImageIdentityTests(unittest.TestCase):
    def test_both_known_docker_stores_keep_distinct_leaf_config_and_actual_id(self):
        for role in PINS["old_images"]:
            for store in ("containerd", "classic"):
                with self.subTest(role=role, store=store):
                    raw, observed, pin = fixture(role, store)
                    result = bind(raw, observed, pin, role)
                    self.assertEqual(result["docker_image_id"], observed["Id"])
                    self.assertEqual(result["config_digest"], pin["config_digest"])
                    self.assertEqual(result["manifest_sha256"], hashlib.sha256(raw).hexdigest())
                    self.assertEqual(result["config_digest_origin"], "frozen_manifest_hash_bound_to_reference")
                    validate_old_image_receipt(result, pin)

    def test_tampered_truncated_oversize_or_nonbytes_manifest_refused(self):
        raw, observed, pin = fixture()
        for bad in (raw + b" ", raw[:-1], b"x" * 65537, raw.decode()):
            with self.subTest(kind=type(bad).__name__, size=len(bad)), self.assertRaises(ValueError):
                bind(bad, observed, pin)

    def test_pin_config_or_leaf_mismatch_refused(self):
        raw, observed, pin = fixture()
        for key, value in (("config_digest", "sha256:" + "f" * 64), ("reference", "ghcr.io/bipoxdxd/duora-edge@sha256:" + "e" * 64)):
            changed = dict(pin); changed[key] = value
            with self.subTest(key=key), self.assertRaises(ValueError):
                bind(raw, observed, changed)

    def test_foreign_id_repo_digest_arch_os_or_descriptor_refused(self):
        raw, observed, pin = fixture()
        for key, value in (("Id", "sha256:" + "f" * 64), ("RepoDigests", []), ("Architecture", "arm64"),
                           ("Os", "windows"), ("Descriptor", {"digest": "sha256:" + "f" * 64})):
            changed = copy.deepcopy(observed); changed[key] = value
            with self.subTest(key=key), self.assertRaises(ValueError):
                bind(raw, changed, pin)

    def test_missing_or_wrong_source_role_revision_labels_refused(self):
        raw, observed, pin = fixture()
        for key in ("org.opencontainers.image.source", "org.opencontainers.image.title", "org.opencontainers.image.revision"):
            changed = copy.deepcopy(observed); changed["Config"]["Labels"][key] = "foreign"
            with self.subTest(key=key), self.assertRaises(ValueError):
                bind(raw, changed, pin)

    def test_pg_version_required_without_invented_pg_source_revision(self):
        raw, observed, pin = fixture("postgres")
        observed["Config"]["Labels"] = {"io.duora.postgres.version": "18.6"}
        self.assertEqual(bind(raw, observed, pin, "postgres")["docker_image_id"], observed["Id"])
        observed["Config"]["Labels"]["io.duora.postgres.version"] = "17"
        with self.assertRaises(ValueError): bind(raw, observed, pin, "postgres")

    def test_receipt_foreign_runtime_id_or_fabricated_config_origin_refused(self):
        raw, observed, pin = fixture(); result = bind(raw, observed, pin)
        for key, value in (("docker_image_id", "sha256:" + "a" * 64), ("config_digest_origin", "docker_observed"),
                           ("manifest_sha256", "a" * 64), ("config_digest", "sha256:" + "a" * 64)):
            changed = dict(result); changed[key] = value
            with self.subTest(key=key), self.assertRaises(ValueError): validate_old_image_receipt(changed, pin)

    def test_classic_store_may_omit_descriptor_but_not_immutable_repo_digest(self):
        raw, observed, pin = fixture(store="classic"); observed.pop("Descriptor")
        self.assertEqual(bind(raw, observed, pin)["docker_image_id"], pin["config_digest"])
        observed.pop("RepoDigests")
        with self.assertRaises(ValueError): bind(raw, observed, pin)

if __name__ == "__main__": unittest.main()
