"""Offline admission checks for publication artifacts; no provider/API calls."""
import copy
import hashlib
import json
from pathlib import Path
import struct
import tempfile
import unittest
from package import package_catalog, approval_digest


def fixture(folder):
    raw = struct.pack('<4sI4s4sIHHIIHH4sIh', b'RIFF', 38, b'WAVE', b'fmt ', 16, 1, 1, 8000, 16000, 2, 16, b'data', 2, 10)
    (folder / 'voice.wav').write_bytes(raw)
    h = hashlib.sha256(raw).hexdigest()
    en = {'title': 'Choices', 'objective': 'Choose together.', 'cards': [{'cardId': 'one', 'prompt': 'Choose?', 'options': [{'optionId': 'a', 'label': 'A'}, {'optionId': 'b', 'label': 'B'}]}]}
    variant = {'assetId': 'intro-en', 'transcript': en['objective'], 'accessibleDescription': en['title'], 'provenance': 'Test-only source', 'sha256': h, 'path': 'voice.wav'}
    return {'kind': 'constellation', 'contentVersion': 1, 'review': {'status': 'pending_human_review'}, 'enUS': en, 'ptBR': copy.deepcopy(en), 'audio': [{'cueId': 'intro', 'enUS': variant, 'ptBR': variant | {'assetId': 'intro-pt'}}]}


class PackageTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.data = fixture(self.root)

    def test_review_payload_never_infers_editorial_approval(self):
        request = package_catalog(self.data, self.root)
        self.assertFalse(request['editorialApproved'])
        self.assertNotIn('review', request)
        self.assertNotIn('kind', request)
        self.assertNotIn('path', request['audio'][0]['enUS'])
        self.assertTrue(request['audio'][0]['enUS']['bytes'])

    def test_explicit_approval_is_bound_to_exact_content_and_audio_hashes(self):
        approval = {'reviewer': 'Human reviewer', 'approvedAt': '2026-10-21T12:00:00Z', 'catalogSha256': approval_digest(self.data), 'editorialApproved': True, 'rightsVerified': True, 'audioListened': True}
        self.assertTrue(package_catalog(self.data, self.root, approval)['editorialApproved'])
        self.data['enUS']['objective'] = 'Changed after review.'
        with self.assertRaisesRegex(ValueError, 'approval'):
            package_catalog(self.data, self.root, approval)

    def test_incomplete_approval_never_publishes(self):
        for approval in [{}, {'editorialApproved': True}, {'reviewer': 'X', 'editorialApproved': True, 'catalogSha256': approval_digest(self.data)}]:
            with self.subTest(approval=approval), self.assertRaisesRegex(ValueError, 'approval'):
                package_catalog(self.data, self.root, approval)

    def test_changed_missing_or_silent_audio_is_rejected(self):
        for kind in ['hash', 'missing', 'silent']:
            with self.subTest(kind=kind):
                data = copy.deepcopy(self.data)
                if kind == 'hash':
                    data['audio'][0]['enUS']['sha256'] = '0' * 64
                elif kind == 'missing':
                    data['audio'][0]['enUS']['path'] = 'missing.wav'
                else:
                    raw = bytearray((self.root / 'voice.wav').read_bytes()); raw[44:] = b'\0\0'
                    (self.root / 'silent.wav').write_bytes(raw)
                    data['audio'][0]['enUS'].update(path='silent.wav', sha256=hashlib.sha256(raw).hexdigest())
                with self.assertRaises(ValueError):
                    package_catalog(data, self.root)

    def test_paths_cannot_escape_package_or_follow_external_symlinks(self):
        outside = self.root.parent / (self.root.name + '-outside.wav')
        outside.write_bytes((self.root / 'voice.wav').read_bytes())
        self.addCleanup(outside.unlink)
        (self.root / 'link.wav').symlink_to(outside)
        for path in ['../outside.wav', str(outside), 'link.wav']:
            with self.subTest(path=path):
                data = copy.deepcopy(self.data); data['audio'][0]['enUS']['path'] = path
                with self.assertRaises(ValueError):
                    package_catalog(data, self.root)

    def test_missing_locale_mismatched_ids_duplicate_assets_and_long_speech_rejected(self):
        mutations = [lambda d: d.pop('ptBR'), lambda d: d['ptBR']['cards'][0].update(cardId='other'), lambda d: d['audio'][0]['ptBR'].update(assetId='intro-en'), lambda d: d['audio'][0]['enUS'].update(transcript='a' * 121)]
        for mutate in mutations:
            data = copy.deepcopy(self.data); mutate(data)
            with self.subTest(data=data), self.assertRaises(ValueError):
                package_catalog(data, self.root)

    def test_routes_cannot_put_private_clues_in_shared_audio(self):
        data = copy.deepcopy(self.data); data['kind'] = 'unlikely-routes'
        for locale in ['enUS', 'ptBR']:
            data[locale].pop('cards')
            data[locale].update(example='Imagine together.', stops=[{'stepId': 'one', 'prompt': 'Next?', 'options': [{'optionId': 'a', 'label': 'A'}, {'optionId': 'b', 'label': 'B'}]}], clueSets=[[{'text': 'Private A'}], [{'text': 'Private B'}]])
        self.assertFalse(package_catalog(data, self.root)['editorialApproved'])
        data['audio'][0]['enUS']['transcript'] = 'Private A'
        with self.assertRaises(ValueError):
            package_catalog(data, self.root)


class ActualEditorialArtifactsTests(unittest.TestCase):
    root = Path(__file__).resolve().parents[2] / 'content' / 'experiences' / '2026-10-review'

    def test_real_packages_are_complete_and_unapproved_with_exact_bilingual_structure(self):
        for kind in ['constellation', 'unlikely-routes']:
            data = json.loads((self.root / (kind + '.json')).read_text())
            with self.subTest(kind=kind):
                request = package_catalog(data, self.root)
                self.assertFalse(request['editorialApproved'])
                self.assertEqual(data['review']['status'], 'pending_human_review')
                self.assertIsNone(data['review'].get('approvedBy'))
                for locale in ['enUS', 'ptBR']:
                    self.assertNotIn('AI-generated', request[locale]['objective'])
                    self.assertNotIn('gerada por IA', request[locale]['objective'])

    def test_final_media_and_generation_metadata_match_every_checked_in_file(self):
        manifest = json.loads((self.root / 'media-manifest.json').read_text())
        self.assertEqual(4, len(manifest['assets']))
        self.assertLessEqual(manifest['reportedCredits'], manifest['totalCreditCap'])
        self.assertIn('Creator active', manifest['planAtGeneration'])
        for asset in manifest['assets']:
            with self.subTest(key=asset['key']):
                source = next(take for take in manifest['allTakes'] if take['generationId'] == asset['generationId'])
                self.assertEqual(source['take'], asset['selectedTake'])
                self.assertEqual(source['prompt'], asset['transcript'])
                for codec in ['wav', 'aac']:
                    file = self.root / asset[codec]['path']
                    self.assertEqual(asset[codec]['bytes'], file.stat().st_size)
                    self.assertEqual(asset[codec]['sha256'], hashlib.sha256(file.read_bytes()).hexdigest())
                    self.assertLess(file.stat().st_size, 3 * 1024 * 1024)


    def test_automatic_transcript_matches_each_current_public_intro(self):
        checks = json.loads((self.root / 'transcription-check.json').read_text())
        self.assertEqual(4, len(checks['results']))
        for row in checks['results']:
            self.assertTrue(row['exactMatch'])
            self.assertEqual(row['expected'], row['transcript'])
            kind = 'constellation' if row['key'].startswith('constellation-') else 'unlikely-routes'
            locale = 'enUS' if row['key'].endswith('en-US') else 'ptBR'
            data = json.loads((self.root / (kind + '.json')).read_text())
            self.assertEqual(data[locale]['objective'], row['transcript'])


if __name__ == '__main__':
    unittest.main()
