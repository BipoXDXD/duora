"""Real index/attestation regressions at the sender's pre-transport boundary.

The unchanged OCI layer validator is spied on here: no layers, Docker, network,
install, SQL or production data. Accepted roles must still reach that validator;
invalid metadata must fail before it and before a release envelope is written.
"""
import copy
import hashlib
import json
import os
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

import receiver
import send

FIXTURE = json.loads((Path(__file__).parent / 'fixtures/scanned_native_indices.json').read_text())
SOURCE = FIXTURE['publication']['commit']


class SenderBindingTests(unittest.TestCase):
    def invoke(self, mutate=None):
        publication = copy.deepcopy(FIXTURE['publication'])
        images = {role: receiver.PROJECTS['duora'][role] + '@' + digest
                  for role, digest in FIXTURE['transportDigests'].items()}
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            oci = root / 'images'
            oci.mkdir()
            witnesses = root / 'source-manifests'
            witnesses.mkdir()
            for role, raw in FIXTURE['sourceManifests'].items():
                (witnesses / (role + '.json')).write_text(raw)
            if mutate:
                mutate(publication, images, witnesses, root)
            pub = root / 'publication.json'
            pub.write_text(json.dumps(publication))
            resolved = root / 'resolved.tsv'
            resolved.write_text(''.join(role + '\t' + reference + '\n'
                                        for role, reference in images.items()))
            env = {'RELEASE_PROJECT': 'duora', 'RELEASE_COMMIT': SOURCE,
                   'PUBLICATION_FILE': str(pub), 'SOURCE_MANIFESTS_DIR': str(witnesses)}
            with patch.dict(os.environ, env), patch.object(send.sys, 'argv',
                    ['send.py', 'manifest', str(resolved), str(oci)]), patch.object(
                    send, 'make_duora_manifest', side_effect=lambda p, i, c:
                    {'commit': c, 'architecture': 'amd64', 'images': i, 'contractVersion': 'v1'}), patch.object(
                    send, 'validate_oci', return_value=('sha256:' + 'a' * 64, 0)) as validator:
                try:
                    send.main()
                except (ValueError, OSError):
                    self.assertFalse((oci / 'release.json').exists())
                    validator.assert_not_called()
                    raise
                result = json.loads((oci / 'release.json').read_text())
                self.assertEqual(result['images'], images)
                self.assertEqual(result['duora']['contractVersion'], 'v1')
                self.assertEqual(validator.call_count, len(images))
                for role, reference in images.items():
                    validator.assert_any_call(oci / role, reference.split('@')[1])
                self.assertFalse((oci / 'source-manifests').exists())
                return result

    def reject(self, mutate):
        with self.assertRaises((ValueError, OSError)):
            self.invoke(mutate)

    def replace_source(self, publication, witnesses, change):
        path = witnesses / 'web.json'
        document = json.loads(path.read_text())
        change(document)
        raw = json.dumps(document).encode()
        path.write_bytes(raw)
        publication['images']['web'] = receiver.PROJECTS['duora']['web'] + '@sha256:' + hashlib.sha256(raw).hexdigest()

    def test_real_four_attested_indices_and_direct_postgres_preserve_strict_binding(self):
        result = self.invoke()
        for role in ('web', 'worker', 'migrator', 'edge'):
            self.assertNotEqual(result['images'][role], FIXTURE['publication']['images'][role])
        self.assertEqual(result['images']['postgres'], FIXTURE['publication']['images']['postgres'])

    def test_current_sender_emits_the_reviewed_e9_envelope_accepted_by_the_receiver(self):
        result = self.invoke()
        self.assertEqual(result['receiver_sha256'],
                         'e9af03f08162ecf2bda094d38511b44b810043863d3104f60ea1046ae687f3b3')
        self.assertEqual(receiver.validate_release(result, 'duora', SOURCE), result)

    def test_historical_1ccd_envelope_is_refused_by_the_new_receiver(self):
        result = self.invoke()
        result['receiver_sha256'] = '1ccd1ada4777c80081c4f9bb2a64a500b1e221f794406319504d9e66cbb975cb'
        with self.assertRaises(ValueError):
            receiver.validate_release(result, 'duora', SOURCE)

    def test_source_bytes_tampered_without_changing_scanned_reference_are_refused(self):
        def mutate(p, i, w, r):
            path = w / 'web.json'
            path.write_bytes(path.read_bytes() + b' ')
        self.reject(mutate)

    def test_foreign_leaf_not_in_the_approved_index_is_refused(self):
        self.reject(lambda p, i, w, r: i.update(web=receiver.PROJECTS['duora']['web'] + '@sha256:' + 'f' * 64))

    def test_cross_role_witness_is_refused(self):
        self.reject(lambda p, i, w, r: (w / 'web.json').write_bytes((w / 'worker.json').read_bytes()))

    def test_foreign_repository_is_refused(self):
        self.reject(lambda p, i, w, r: p['images'].update(web='ghcr.io/attacker/duora-web@sha256:' + 'f' * 64))

    def test_index_without_linux_amd64_is_refused_even_with_valid_source_hash(self):
        def mutate(p, i, w, r):
            self.replace_source(p, w, lambda d: d['manifests'][0]['platform'].update(architecture='arm64'))
        self.reject(mutate)

    def test_ambiguous_amd64_descriptors_are_refused_even_with_valid_source_hash(self):
        def mutate(p, i, w, r):
            self.replace_source(p, w, lambda d: d['manifests'].append(copy.deepcopy(d['manifests'][0])))
        self.reject(mutate)

    def test_duplicate_json_keys_are_refused_even_with_valid_source_hash(self):
        def mutate(p, i, w, r):
            path = w / 'web.json'
            raw = path.read_bytes().rstrip()[:-1] + b',"schemaVersion":2}'
            path.write_bytes(raw)
            p['images']['web'] = receiver.PROJECTS['duora']['web'] + '@sha256:' + hashlib.sha256(raw).hexdigest()
        self.reject(mutate)

    def test_source_symlink_and_hardlink_are_refused(self):
        def symlink(p, i, w, r):
            path = w / 'web.json'
            other = r / 'foreign'
            path.rename(other)
            path.symlink_to(other)
        def hardlink(p, i, w, r):
            os.link(w / 'web.json', r / 'foreign')
        for mutate in (symlink, hardlink):
            with self.subTest(kind=mutate.__name__):
                self.reject(mutate)

    def test_missing_or_oversized_source_is_refused(self):
        for mutate in (lambda p, i, w, r: (w / 'web.json').unlink(),
                       lambda p, i, w, r: (w / 'web.json').write_bytes(b' ' * (256 * 1024 + 1))):
            with self.subTest(mutate=mutate):
                self.reject(mutate)

    def test_wrong_transport_role_is_refused(self):
        self.reject(lambda p, i, w, r: i.update(web=i['worker']))


if __name__ == '__main__':
    unittest.main()
