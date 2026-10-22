import copy
import importlib
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
IMAGES = ('web', 'worker', 'migrator', 'edge', 'postgres')


class ReleaseIndexTests(unittest.TestCase):
    def setUp(self):
        self.assertTrue((ROOT / 'scripts/release/publish_indexes.py').is_file(),
                        'release precisa reunir os digests nativos num índice AMD64+ARM64')
        self.module = importlib.import_module('publish_indexes')
        self.commit = 'a' * 40
        self.ci = 'https://github.com/BipoXDXD/duora/actions/runs/101/attempts/1'
        self.run = 'https://github.com/BipoXDXD/duora/actions/runs/202/attempts/1'
        self.docs = {}
        for arch, char in [('amd64', 'b'), ('arm64', 'c')]:
            self.docs[arch] = dict(commit=self.commit, architecture=arch, ciRun=self.ci,
                                   publishRun=self.run, images={
                image: f'ghcr.io/bipoxdxd/duora-{image}@sha256:{char * 64}' for image in IMAGES})

    def validate(self, docs=None):
        return self.module.validate_publications(docs or self.docs, self.commit, '101', '1', '202')

    def test_valid_pair_and_retry_artifacts(self):
        result = self.validate()
        self.assertEqual(set(result), set(IMAGES))
        self.assertEqual(set(result['web']), {'amd64', 'arm64'})
        self.docs['arm64']['publishRun'] = self.run[:-1] + '2'
        self.validate()

    def test_exact_commit_architecture_ci_and_run(self):
        for field, value in [('commit', 'd' * 40), ('architecture', 'amd64'),
                             ('ciRun', self.ci[:-1] + '2'), ('publishRun', self.run.replace('/202/', '/999/'))]:
            docs = copy.deepcopy(self.docs)
            docs['arm64'][field] = value
            with self.subTest(field=field), self.assertRaises(ValueError):
                self.validate(docs)

    def test_missing_extra_images_and_foreign_digest(self):
        for transform in [lambda images: images.pop('worker'),
                          lambda images: images.update(extra='ignored'),
                          lambda images: images.update(web='ghcr.io/attacker/web@sha256:' + 'b' * 64),
                          lambda images: images.update(web='ghcr.io/bipoxdxd/duora-web:latest')]:
            docs = copy.deepcopy(self.docs)
            transform(docs['amd64']['images'])
            with self.assertRaises(ValueError):
                self.validate(docs)

    def test_missing_architecture_is_refused(self):
        with self.assertRaises(ValueError):
            self.validate({'amd64': self.docs['amd64']})

    def test_source_checks_real_config_before_publishing(self):
        manifest = {'schemaVersion': 2, 'config': {'digest': 'sha256:' + 'a' * 64}}
        config = {'os': 'linux', 'architecture': 'arm64'}
        with self.assertRaises(ValueError):
            self.module.source_digest(manifest, config, 'amd64', 'sha256:' + 'b' * 64)
        self.assertEqual(self.module.source_digest(manifest, config, 'arm64', 'sha256:' + 'b' * 64),
                         'sha256:' + 'b' * 64)

    def test_combined_index_requires_approved_native_digests(self):
        expected = {'amd64': 'sha256:' + 'b' * 64, 'arm64': 'sha256:' + 'c' * 64}
        index = {'manifests': [{'digest': digest, 'platform': {'os': 'linux', 'architecture': arch}}
                               for arch, digest in expected.items()]}
        self.module.validate_index(index, expected)
        for bad in [index['manifests'][:1], index['manifests'] * 2,
                    [dict(index['manifests'][0], digest='sha256:' + 'd' * 64), index['manifests'][1]]]:
            with self.assertRaises(ValueError):
                self.module.validate_index({'manifests': bad}, expected)
        index['manifests'].append({'digest': 'sha256:' + 'e' * 64,
                                  'platform': {'os': 'unknown', 'architecture': 'unknown'}})
        self.module.validate_index(index, expected)

    def test_artifacts_are_scoped_complete_and_choose_latest_retry(self):
        data = {'total_count': 3, 'artifacts': [
            {'id': 1, 'name': 'release-amd64-1', 'expired': False},
            {'id': 2, 'name': 'release-arm64-1', 'expired': False},
            {'id': 3, 'name': 'release-amd64-2', 'expired': False}]}
        self.assertEqual(self.module.select_artifacts(data),
                         {'amd64': 'release-amd64-2', 'arm64': 'release-arm64-1'})
        data['total_count'] = 4
        with self.assertRaises(ValueError):
            self.module.select_artifacts(data)
        data = {'total_count': 1, 'artifacts': [data['artifacts'][0]]}
        with self.assertRaises(ValueError):
            self.module.select_artifacts(data)


class IndexWorkflowTests(unittest.TestCase):
    def test_aggregate_job_is_after_native_jobs_and_has_evidence(self):
        workflow = (ROOT / '.github/workflows/publish-images.yml').read_text()
        self.assertIn('\n  indexes:\n', workflow, 'faltou publicar os índices multiplataforma')
        aggregate = workflow.split('\n  indexes:\n')[1]
        self.assertIn('needs: [verify, images]', aggregate)
        self.assertIn('publish_indexes.py', aggregate)
        self.assertIn('release-multiarch-', aggregate)
        self.assertIn('github.workflow_sha', aggregate)


if __name__ == '__main__':
    unittest.main()
