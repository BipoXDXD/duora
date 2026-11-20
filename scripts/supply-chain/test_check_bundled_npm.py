"""Exercita o gate de licenças com componentes que vêm dentro do tarball npm."""

import hashlib
import io
import json
import tempfile
import unittest
from contextlib import redirect_stderr, redirect_stdout
from pathlib import Path
from urllib.parse import quote

from check_licenses import main


OXIDE = "@tailwindcss/oxide-wasm32-wasi"
EMNAPI = "@emnapi/core"
VERSION = "4.3.5"
INTEGRITY = "sha512-" + "A" * 86 + "=="


def component(package, version, license=None):
    group, _, name = package.rpartition("/")
    value = {
        "type": "library",
        "name": name,
        "version": version,
        "purl": f"pkg:npm/{quote(package, safe='/')}@{version}",
    }
    if group:
        value["group"] = group
    if license is not None:
        value["licenses"] = [{"license": {"id": license}}]
    return value


class BundledNpmGateTests(unittest.TestCase):
    def setUp(self):
        temp = tempfile.TemporaryDirectory()
        self.addCleanup(temp.cleanup)
        self.root = Path(temp.name)
        self.policy = self.root / "scripts/supply-chain/license-policy.json"
        self.policy.parent.mkdir(parents=True)
        self.policy.write_text(json.dumps({
            "allowed": ["MIT", "0BSD"],
            "missingLicenseAllowed": [
                {"pattern": "@tailwindcss/oxide-*", "reason": "fixture"},
                {"pattern": "@emnapi/*", "reason": "fixture"},
            ],
            "npmBundledInventory": "npm-bundled-inventory.json",
        }))
        self.inventory_path = self.policy.with_name("npm-bundled-inventory.json")
        self.frontend = self.root / "frontend"
        self.bundle = (
            self.frontend / "node_modules/.pnpm"
            / f"@tailwindcss+oxide-wasm32-wasi@{VERSION}" / "node_modules" / OXIDE
        )
        self.core = self.bundle / "node_modules" / EMNAPI / "package.json"
        self.tslib = self.bundle / "node_modules/tslib/package.json"
        self.parent = self.bundle / "package.json"
        self.inventory = {
            "package": OXIDE,
            "license": "MIT",
            "versions": {
                VERSION: {"integrity": INTEGRITY, "manifestSha256": self.write_manifest(self.parent, {
                    "name": OXIDE,
                    "version": VERSION,
                    "license": "MIT",
                    "bundledDependencies": [EMNAPI, "tslib"],
                })},
            },
            "dependencies": {
                EMNAPI: {"version": "1.11.3", "license": "MIT", "manifestSha256": self.write_manifest(self.core, {
                    "name": EMNAPI, "version": "1.11.3", "license": "MIT",
                })},
                "tslib": {"version": "2.8.1", "license": "0BSD", "manifestSha256": self.write_manifest(self.tslib, {
                    "name": "tslib", "version": "2.8.1", "license": "0BSD",
                })},
            },
        }
        self.inventory_path.write_text(json.dumps(self.inventory))
        self.lock = self.frontend / "pnpm-lock.yaml"
        self.lock.write_text(f"packages:\n  '{OXIDE}@{VERSION}':\n    resolution: {{integrity: {INTEGRITY}}}\n")
        self.sbom = self.root / "sbom-frontend.cdx.json"
        self.components = [
            component(OXIDE, VERSION, "MIT"),
            component(EMNAPI, "2.0.0-beta.2", "MIT"),
            component("tslib", "2.8.1", "0BSD"),
        ]

    def write_manifest(self, path, data):
        path.parent.mkdir(parents=True, exist_ok=True)
        raw = json.dumps(data).encode()
        path.write_bytes(raw)
        return hashlib.sha256(raw).hexdigest()

    def invoke(self):
        self.sbom.write_text(json.dumps({"components": self.components}))
        before = self.sbom.read_bytes()
        log = io.StringIO()
        with redirect_stdout(log), redirect_stderr(log):
            result = main(["check_licenses.py", str(self.policy), str(self.sbom)])
        self.assertEqual(before, self.sbom.read_bytes())
        return result, log.getvalue()

    def test_adds_the_bundled_version_when_sbom_lists_a_different_version(self):
        status, report = self.invoke()
        self.assertEqual(0, status, report)
        self.assertIn("`@emnapi/core@1.11.3`", report)
        self.assertIn("`@tailwindcss/oxide-wasm32-wasi@4.3.5`", report)
        self.assertIn(INTEGRITY, report)
        self.assertIn("| `0BSD` | 1 |", report)

    def test_package_absent_from_bundle_fails_despite_missing_license_exception(self):
        self.core.unlink()
        status, report = self.invoke()
        self.assertEqual(1, status, report)
        self.assertIn(EMNAPI, report)

    def test_absent_wasi_component_fails_even_when_installed(self):
        self.components.pop(0)
        status, report = self.invoke()
        self.assertEqual(1, status, report)
        self.assertIn(OXIDE, report)

    def test_accepts_explicit_spdx_id_with_additional_license_metadata(self):
        self.components[0]["licenses"][0]["license"]["url"] = "https://spdx.org/licenses/MIT.html"
        status, report = self.invoke()
        self.assertEqual(0, status, report)

    def test_wasi_component_without_license_fails_despite_exception(self):
        self.components[0].pop("licenses")
        status, report = self.invoke()
        self.assertEqual(1, status, report)
        self.assertIn(OXIDE, report)

    def test_tampered_bundled_manifest_fails(self):
        self.write_manifest(self.core, {"name": EMNAPI, "version": "1.11.3"})
        status, report = self.invoke()
        self.assertEqual(1, status, report)
        self.assertIn(EMNAPI, report)

    def test_inventory_cannot_omit_a_dependency_declared_in_the_tarball(self):
        self.inventory["dependencies"].pop(EMNAPI)
        self.inventory_path.write_text(json.dumps(self.inventory))
        status, report = self.invoke()
        self.assertEqual(1, status, report)
        self.assertIn(OXIDE, report)

    def test_tarball_integrity_must_match_the_lock(self):
        self.lock.write_text(self.lock.read_text().replace("sha512-AAAA", "sha512-AAAB"))
        status, report = self.invoke()
        self.assertEqual(1, status, report)
        self.assertIn("pnpm-lock.yaml", report)

    def test_bundle_declaration_is_still_checked_against_the_allowlist(self):
        data = json.loads(self.policy.read_text())
        data["allowed"].remove("MIT")
        self.policy.write_text(json.dumps(data))
        status, report = self.invoke()
        self.assertEqual(1, status, report)
        self.assertIn("licença fora da allowlist", report)
        self.assertIn("@emnapi/core@1.11.3", report)
