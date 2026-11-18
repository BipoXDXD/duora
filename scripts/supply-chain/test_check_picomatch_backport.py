"""Synthetic failure cases for the proposed verifier. No vulnerable code is run."""

from pathlib import Path
from tempfile import TemporaryDirectory
from unittest import TestCase, mock
import subprocess

import check_picomatch_backport as gate


class BackportGateTests(TestCase):
    def setUp(self):
        temp = TemporaryDirectory()
        self.addCleanup(temp.cleanup)
        self.temp = Path(temp.name).resolve()
        self.root = self.temp / 'repo'
        self.modules = self.root / 'frontend/node_modules'
        self.package = self.modules / '.pnpm/picomatch@4.0.7/node_modules/picomatch'
        self.create_package(self.package)
        self.patch = self.put(f'frontend/{gate.PATCH}', 'reviewed patch\n')
        patch_hash = gate.sha256(self.patch)
        self.lock = self.put(gate.TARGET, f'''---
lockfileVersion: '9.0'
importers:
  .:
    packageManagerDependencies:
      pnpm: 12.11.2
---
lockfileVersion: '9.0'
patchedDependencies:
  picomatch@4.0.7: {patch_hash}
packages:
  picomatch@4.0.7:
    resolution: {{integrity: {gate.SRI}}}
snapshots:
  'picomatch@4.0.7(patch_hash={patch_hash})': {{}}
''')
        workspace = self.put('frontend/pnpm-workspace.yaml',
                             f'patchedDependencies:\n  picomatch@4.0.7: {gate.PATCH}\n')
        regression = self.put(gate.REGRESSION, '// approved regression sentinel\n')
        manifest = self.root / gate.MANIFEST
        gate.write_json(manifest, {
            'schema': 1, 'package': gate.PACKAGE, 'version': gate.VERSION, 'purl': gate.PURL,
            'vulnerabilityIds': sorted(gate.ADVISORY_IDS), 'patch': gate.PATCH,
            'upstreamSource': gate.UPSTREAM, 'upstreamIntegrity': gate.SRI,
            'patchSha256': patch_hash, 'lockSha256': gate.sha256(self.lock),
            'workspaceSha256': gate.sha256(workspace), 'regressionSha256': gate.sha256(regression),
            'files': gate.package_tree_sha256(self.package),
        })
        self.raw = self.temp / 'trivy-raw.json'
        self.policy = self.temp / 'trivy-policy.json'
        self.vex = self.temp / 'picomatch.openvex.json'
        self.proof = self.temp / 'picomatch-proof.json'
        self.advisory = {'VulnerabilityID': 'CVE-2026-112366', 'PkgIdentifier': {'PURL': gate.PURL},
                         'PkgName': gate.PACKAGE, 'InstalledVersion': gate.VERSION}
        self.other = {**self.advisory, 'VulnerabilityID': 'CVE-2099-12345'}

    def put(self, path, contents):
        file = self.root / path
        file.parent.mkdir(parents=True, exist_ok=True)
        file.write_text(contents, encoding='utf-8')
        return file

    def create_package(self, path):
        (path / 'lib').mkdir(parents=True)
        gate.write_json(path / 'package.json', {'name': gate.PACKAGE, 'version': gate.VERSION})
        (path / 'LICENSE').write_text('synthetic license', encoding='utf-8')
        (path / 'lib/parse.js').write_text('synthetic bytes', encoding='utf-8')

    def report(self, path, vulnerabilities, *, target=gate.TARGET, modified=()):
        gate.write_json(path, {'SchemaVersion': 2, 'ArtifactName': '.', 'ArtifactType': 'filesystem',
                               'Trivy': {'Version': '0.75.0'}, 'Results': [{
            'Target': target, 'Class': 'lang-pkgs', 'Type': 'pnpm', 'Vulnerabilities': vulnerabilities,
            'Packages': [{'Name': gate.PACKAGE, 'Version': gate.VERSION,
                          'Identifier': {'PURL': gate.PURL}}],
            'ExperimentalModifiedFindings': list(modified),
        }]})

    def fixed(self, finding):
        return {'Type': 'vulnerability', 'Status': 'fixed', 'Source': gate.VEX_SOURCE, 'Finding': finding}

    def test_accepts_single_physical_copy_with_an_alias(self):
        (self.modules / 'alias').symlink_to(self.package, target_is_directory=True)
        self.assertEqual(gate.verify(self.root)[1], self.package)

    def test_archived_installations_do_not_belong_to_the_frontend(self):
        archive = self.root / '.claude/archived/frontend'
        archive.mkdir(parents=True)
        old_modules = self.temp / 'old-node_modules'
        self.create_package(old_modules / 'picomatch')
        (archive / 'node_modules').symlink_to(old_modules, target_is_directory=True)
        self.assertEqual(gate.verify(self.root)[1], self.package)

    def test_rejects_linked_frontend_installation_root(self):
        moved = self.root / 'archived-node_modules'
        self.modules.rename(moved)
        self.modules.symlink_to(moved, target_is_directory=True)
        with self.assertRaisesRegex(ValueError, 'physical frontend dependency directory'):
            gate.verify(self.root)

    def test_rejects_broken_picomatch_link_in_active_installation(self):
        (self.modules / 'picomatch').symlink_to(self.modules / 'missing', target_is_directory=True)
        with self.assertRaisesRegex(ValueError, 'expected a dependency directory'):
            gate.verify(self.root)

    def test_rejects_additional_physical_copy_even_under_an_alias_name(self):
        self.create_package(self.modules / 'alias-copy')
        with self.assertRaisesRegex(ValueError, 'found 2'):
            gate.verify(self.root)

    def test_rejects_alias_from_frontend_into_archived_checkout(self):
        outside = self.root / '.claude/archived/picomatch'
        self.create_package(outside)
        (self.modules / 'alias').symlink_to(outside, target_is_directory=True)
        with self.assertRaisesRegex(ValueError, 'link escapes'):
            gate.verify(self.root)

    def test_rejects_unknown_package_file(self):
        (self.package / 'shim.js').write_text('extra bytes', encoding='utf-8')
        with self.assertRaisesRegex(ValueError, 'unknown or absent package file'):
            gate.verify(self.root)

    def test_rejects_patch_or_lockfile_drift(self):
        for path, field in [(self.patch, 'patchSha256'), (self.lock, 'lockSha256')]:
            with self.subTest(field=field):
                original = path.read_bytes()
                path.write_bytes(original + b'\n')
                with self.assertRaisesRegex(ValueError, f'{field} mismatch'):
                    gate.verify(self.root)
                path.write_bytes(original)

    def test_rejects_manifest_that_differs_from_commit(self):
        manifest = self.root / gate.MANIFEST
        committed_hash = gate.sha256(manifest)
        gate.verify_committed_manifest(self.root, committed_hash)
        manifest.write_bytes(manifest.read_bytes() + b'\n')
        with self.assertRaisesRegex(ValueError, 'manifest differs from the checked-out commit'):
            gate.verify_committed_manifest(self.root, committed_hash)

    def test_ignores_patch_hash_in_the_env_document(self):
        patch_hash = gate.sha256(self.patch)
        lock = self.lock.read_text(encoding='utf-8')
        entry = f'patchedDependencies:\n  picomatch@4.0.7: {patch_hash}\n'
        lock = lock.replace(entry, 'patchedDependencies:\n  something-else@4.0.7: abc\n')
        lock = lock.replace('importers:', entry + 'importers:', 1)
        with self.assertRaisesRegex(ValueError, 'missing approved pnpm patch hash'):
            gate.check_lock(lock, patch_hash)

    def test_vex_is_absent_when_regression_fails(self):
        self.report(self.raw, [self.advisory])
        failed = subprocess.CalledProcessError(1, 'node')
        with mock.patch.object(gate.subprocess, 'run', side_effect=failed):
            with self.assertRaises(subprocess.CalledProcessError):
                gate.emit(self.root, self.raw, self.vex, self.proof)
        self.assertFalse(self.vex.exists())
        self.assertFalse(self.proof.exists())

    def test_rejects_change_between_emit_and_scan(self):
        self.report(self.raw, [self.advisory])
        with mock.patch.object(gate.subprocess, 'run'), \
             mock.patch.object(gate.subprocess, 'check_output', return_value='a' * 40):
            gate.emit(self.root, self.raw, self.vex, self.proof)
            gate.check_evidence(self.root, self.raw, self.vex, self.proof)
            original = self.vex.read_bytes()
            self.vex.write_bytes(original + b'\n')
            with self.assertRaisesRegex(ValueError, 'evidence no longer matches'):
                gate.check_evidence(self.root, self.raw, self.vex, self.proof)
            self.vex.write_bytes(original)
            (self.package / 'lib/parse.js').write_text('mutated bytes', encoding='utf-8')
            with self.assertRaisesRegex(ValueError, 'installed package digest mismatch'):
                gate.check_evidence(self.root, self.raw, self.vex, self.proof)

    def test_rejects_same_advisory_in_another_target(self):
        self.report(self.raw, [])
        raw = gate.read_json(self.raw)
        raw['Results'].append({'Target': 'another/pnpm-lock.yaml', 'Type': 'pnpm',
                               'Vulnerabilities': [self.advisory]})
        self.raw.unlink()
        gate.write_json(self.raw, raw)
        self.report(self.policy, [], modified=[self.fixed(self.advisory)])
        with self.assertRaisesRegex(ValueError, 'outside permitted scope'):
            gate.audit(self.raw, self.policy)

    def test_preserves_new_advisory_and_rejects_when_it_disappears(self):
        self.report(self.raw, [self.advisory, self.other])
        self.report(self.policy, [self.other], modified=[self.fixed(self.advisory)])
        gate.audit(self.raw, self.policy)
        self.policy.unlink()
        self.report(self.policy, [], modified=[self.fixed(self.advisory)])
        with self.assertRaisesRegex(ValueError, 'raw finding is missing'):
            gate.audit(self.raw, self.policy)

    def test_rejects_report_without_picomatch_inventory(self):
        self.report(self.raw, [self.advisory])
        raw = gate.read_json(self.raw)
        del raw['Results'][0]['Packages']
        with self.assertRaisesRegex(ValueError, 'must inventory exactly one'):
            gate.expected_findings(raw)

    def test_rejects_vex_for_any_other_advisory(self):
        self.report(self.raw, [self.advisory, self.other])
        self.report(self.policy, [], modified=[self.fixed(self.advisory), self.fixed(self.other)])
        with self.assertRaisesRegex(ValueError, 'unexpected VEX suppression'):
            gate.audit(self.raw, self.policy)
