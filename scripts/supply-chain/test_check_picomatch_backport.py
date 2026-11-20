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
        self.consumers = {}
        for name, version in [('fdir', '6.5.0'), ('tinyglobby', '0.2.17'),
                              ('vite', '8.3.5'), ('vitest', '5.1.0')]:
            self.consumers[name] = self.install_consumer(name, version)
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
importers:
  .: {{}}
packages:
  picomatch@4.0.7:
    resolution: {{integrity: {gate.SRI}}}
snapshots:
  'picomatch@4.0.7(patch_hash={patch_hash})': {{}}
  'fdir@6.5.0(picomatch@4.0.7(patch_hash={patch_hash}))':
    optionalDependencies:
      picomatch: 4.0.7(patch_hash={patch_hash})
  tinyglobby@0.2.17:
    dependencies:
      picomatch: 4.0.7(patch_hash={patch_hash})
  vite@8.3.5:
    dependencies:
      picomatch: 4.0.7(patch_hash={patch_hash})
  vitest@5.1.0(vite@8.3.5):
    dependencies:
      picomatch: 4.0.7(patch_hash={patch_hash})
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

    def approve_fixture_change(self, field, path):
        manifest = self.root / gate.MANIFEST
        metadata = gate.read_json(manifest)
        metadata[field] = gate.sha256(path)
        manifest.unlink()
        gate.write_json(manifest, metadata)

    def create_package(self, path):
        (path / 'lib').mkdir(parents=True)
        gate.write_json(path / 'package.json', {'name': gate.PACKAGE, 'version': gate.VERSION})
        (path / 'index.js').write_text('module.exports = {};', encoding='utf-8')
        (path / 'LICENSE').write_text('synthetic license', encoding='utf-8')
        (path / 'lib/parse.js').write_text('synthetic bytes', encoding='utf-8')

    def install_consumer(self, name, version, suffix=''):
        consumer = self.modules / f'.pnpm/{name}-fixture{suffix}/node_modules' / name
        consumer.mkdir(parents=True)
        gate.write_json(consumer / 'package.json', {'name': name, 'version': version})
        (consumer.parent / 'picomatch').symlink_to(self.package, target_is_directory=True)
        return consumer

    def report(self, path, vulnerabilities, *, target=gate.TARGET, modified=(),
               misconfigurations=(), artifact_type='filesystem'):
        gate.write_json(path, {'SchemaVersion': 2, 'ArtifactName': '.', 'ArtifactType': artifact_type,
                               'Trivy': {'Version': '0.75.0'}, 'Results': [{
            'Target': target, 'Class': 'lang-pkgs', 'Type': 'pnpm', 'Vulnerabilities': vulnerabilities,
            'Misconfigurations': list(misconfigurations),
            'Packages': [{'Name': gate.PACKAGE, 'Version': gate.VERSION,
                          'Identifier': {'PURL': gate.PURL}}],
            'ExperimentalModifiedFindings': list(modified),
        }]})

    def fixed(self, finding):
        return {'Type': 'vulnerability', 'Status': 'fixed', 'Source': gate.VEX_SOURCE, 'Finding': finding}

    def ignore(self, category, finding):
        return {'Type': category, 'Status': 'ignored', 'Source': '.trivyignore', 'Finding': finding}

    def test_refuses_to_emit_when_the_only_patched_copy_is_orphaned(self):
        for consumer in self.consumers.values():
            (consumer.parent / 'picomatch').unlink()
        self.report(self.raw, [self.advisory])
        with mock.patch.object(gate.subprocess, 'check_output', return_value='a' * 40):
            with self.assertRaisesRegex(ValueError, 'missing.*Picomatch.*edge'):
                gate.emit(self.root, self.raw, self.vex, self.proof)
        self.assertFalse(self.vex.exists())
        self.assertFalse(self.proof.exists())

    def test_rejects_missing_optional_consumer(self):
        self.consumers['fdir'].rename(self.temp / 'missing-consumer')
        with self.assertRaisesRegex(ValueError, 'unexpected installed consumer count.*fdir'):
            gate.verify(self.root)

    def test_rejects_consumer_that_resolves_a_shadow_module(self):
        self.put('frontend/node_modules/.pnpm/vite-fixture/node_modules/vite/node_modules/picomatch.js',
                 'module.exports = {};')
        with self.assertRaisesRegex(ValueError, 'vite.*outside the verified Picomatch package'):
            gate.verify(self.root)

    def test_accepts_two_installed_copies_for_distinct_consumer_snapshots(self):
        patch_hash = gate.sha256(self.patch)
        lock = self.lock.read_text(encoding='utf-8')
        self.lock.write_text(lock + f'  vitest@5.1.0(vite@8.4.0):\n'
                             f'    dependencies:\n      picomatch: 4.0.7(patch_hash={patch_hash})\n',
                             encoding='utf-8')
        self.approve_fixture_change('lockSha256', self.lock)
        with self.assertRaisesRegex(ValueError, 'unexpected installed consumer count.*vitest'):
            gate.verify(self.root)
        self.install_consumer('vitest', '5.1.0', suffix='-second')
        self.assertEqual(gate.verify(self.root)[1], self.package)

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

    def test_rejects_direct_picomatch_importer_edges(self):
        patch_hash = gate.sha256(self.patch)
        lock = self.lock.read_text(encoding='utf-8')
        for section, name, specifier in [
            ('dependencies', 'picomatch', '4.0.7'),
            ('optionalDependencies', "'picomatch'", '4.0.7'),
            ('devDependencies', 'pattern-matcher', 'npm:picomatch@4.0.7'),
        ]:
            with self.subTest(section=section, name=name):
                importer = (f'  .:\n    {section}:\n      {name}:\n'
                            f'        specifier: {specifier}\n'
                            f'        version: 4.0.7(patch_hash={patch_hash})\n')
                with self.assertRaisesRegex(ValueError, 'direct Picomatch importer dependency'):
                    gate.check_lock(lock.replace('  .: {}\n', importer), patch_hash)

    def test_vex_is_absent_when_regression_fails(self):
        self.report(self.raw, [self.advisory])
        regression = self.put(gate.REGRESSION, 'process.exitCode = 1;\n')
        self.approve_fixture_change('regressionSha256', regression)
        with self.assertRaises(subprocess.CalledProcessError):
            gate.emit(self.root, self.raw, self.vex, self.proof)
        self.assertFalse(self.vex.exists())
        self.assertFalse(self.proof.exists())

    def test_rejects_change_between_emit_and_scan(self):
        self.report(self.raw, [self.advisory])
        with mock.patch.object(gate.subprocess, 'check_output', return_value='a' * 40):
            gate.emit(self.root, self.raw, self.vex, self.proof)
            gate.check_evidence(self.root, self.raw, self.vex, self.proof)
            original = self.vex.read_bytes()
            self.vex.write_bytes(original + b'\n')
            with self.assertRaisesRegex(ValueError, 'evidence no longer matches'):
                gate.check_evidence(self.root, self.raw, self.vex, self.proof)
            self.vex.write_bytes(original)
            edge = self.consumers['vitest'].parent / 'picomatch'
            edge.unlink()
            with self.assertRaisesRegex(ValueError, 'missing.*Picomatch.*edge'):
                gate.check_evidence(self.root, self.raw, self.vex, self.proof)
            edge.symlink_to(self.package, target_is_directory=True)
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

    def test_accepts_ignored_vulnerability_duplicates_retaining_the_raw_winner(self):
        earlier = {**self.other, 'PkgID': 'synthetic@1.0', 'PkgPath': 'some/path', 'FixedVersion': '2.0'}
        winner = {**earlier, 'FixedVersion': '3.0'}
        self.report(self.raw, [self.advisory, winner])
        self.report(self.policy, [], modified=[self.fixed(self.advisory),
                                               self.ignore('vulnerability', earlier),
                                               self.ignore('vulnerability', winner)])
        gate.audit(self.raw, self.policy)

    def test_rejects_duplicate_with_a_different_pkg_path(self):
        winner = {**self.other, 'PkgID': 'synthetic@1.0', 'PkgPath': 'some/path', 'FixedVersion': '3.0'}
        other_path = {**winner, 'PkgPath': 'another/path', 'FixedVersion': '2.0'}
        self.report(self.raw, [winner])
        self.report(self.policy, [], modified=[self.ignore('vulnerability', winner),
                                               self.ignore('vulnerability', other_path)])
        with self.assertRaisesRegex(ValueError, 'raw finding is missing or changed'):
            gate.audit(self.raw, self.policy)

    def test_rejects_ignored_duplicates_without_exact_raw_winner(self):
        changed = {**self.other, 'FixedVersion': '3.0', 'Title': 'changed'}
        earlier = {**self.other, 'FixedVersion': '2.0'}
        self.report(self.raw, [{**self.other, 'FixedVersion': '3.0'}])
        self.report(self.policy, [], modified=[self.ignore('vulnerability', changed),
                                               self.ignore('vulnerability', earlier)])
        with self.assertRaisesRegex(ValueError, 'raw finding is missing or changed'):
            gate.audit(self.raw, self.policy)

    def test_rejects_ignored_duplicate_that_should_have_won_in_raw(self):
        winner = {**self.other, 'FixedVersion': '3.0'}
        invalid = {**self.other, 'FixedVersion': '4.0'}
        self.report(self.raw, [winner])
        self.report(self.policy, [], modified=[self.ignore('vulnerability', winner),
                                               self.ignore('vulnerability', invalid)])
        with self.assertRaisesRegex(ValueError, 'raw finding is missing or changed'):
            gate.audit(self.raw, self.policy)

    def test_rejects_ignored_duplicate_alongside_an_active_winner(self):
        winner = {**self.other, 'FixedVersion': '3.0'}
        earlier = {**self.other, 'FixedVersion': '2.0'}
        self.report(self.raw, [winner])
        self.report(self.policy, [winner], modified=[self.ignore('vulnerability', earlier)])
        with self.assertRaisesRegex(ValueError, 'raw finding is missing or changed'):
            gate.audit(self.raw, self.policy)

    def test_accepts_ignored_nonfailing_misconfiguration_omitted_from_raw(self):
        self.report(self.raw, [])
        for status in ['PASS', 'EXCEPTION']:
            with self.subTest(status=status):
                finding = {'ID': 'SYNTH001', 'Status': status}
                self.policy.unlink(missing_ok=True)
                self.report(self.policy, [], modified=[self.ignore('misconfiguration', finding)])
                gate.audit(self.raw, self.policy)

    def test_rejects_unknown_or_unexpected_failing_misconfiguration(self):
        self.report(self.raw, [])
        for status in ['NOT_A_STATUS', 'FAIL']:
            with self.subTest(status=status):
                finding = {'ID': 'SYNTH001', 'Status': status}
                self.report(self.policy, [], modified=[self.ignore('misconfiguration', finding)])
                with self.assertRaises(ValueError):
                    gate.audit(self.raw, self.policy)
                self.policy.unlink()

    def test_rejects_failing_misconfiguration_when_it_disappears(self):
        fail = {'ID': 'SYNTH001', 'Status': 'FAIL'}
        self.report(self.raw, [], misconfigurations=[fail])
        self.report(self.policy, [], modified=[self.ignore('misconfiguration', {**fail, 'Status': 'PASS'})])
        with self.assertRaisesRegex(ValueError, 'raw finding is missing or changed'):
            gate.audit(self.raw, self.policy)

    def test_rejects_report_without_picomatch_inventory(self):
        self.report(self.raw, [self.advisory])
        raw = gate.read_json(self.raw)
        del raw['Results'][0]['Packages']
        with self.assertRaisesRegex(ValueError, 'must inventory exactly one'):
            gate.expected_findings(raw)

    def test_accepts_repository_reports_and_rejects_changed_artifact_type(self):
        self.report(self.raw, [self.advisory], artifact_type='repository')
        self.report(self.policy, [], modified=[self.fixed(self.advisory)], artifact_type='repository')
        gate.audit(self.raw, self.policy)
        self.policy.unlink()
        self.report(self.policy, [], modified=[self.fixed(self.advisory)])
        with self.assertRaisesRegex(ValueError, 'artifact type changed'):
            gate.audit(self.raw, self.policy)

    def test_rejects_vex_for_any_other_advisory(self):
        self.report(self.raw, [self.advisory, self.other])
        self.report(self.policy, [], modified=[self.fixed(self.advisory), self.fixed(self.other)])
        with self.assertRaisesRegex(ValueError, 'unexpected VEX suppression'):
            gate.audit(self.raw, self.policy)

    def test_rejects_changed_artifact_identity(self):
        self.report(self.raw, [self.advisory], artifact_type='repository')
        self.report(self.policy, [], modified=[self.fixed(self.advisory)], artifact_type='repository')
        policy = gate.read_json(self.policy)
        policy['ArtifactID'] = 'sha256:changed-checkout'
        self.policy.unlink()
        gate.write_json(self.policy, policy)
        with self.assertRaisesRegex(ValueError, 'artifact identity changed'):
            gate.audit(self.raw, self.policy)
