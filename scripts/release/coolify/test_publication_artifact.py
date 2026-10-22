import unittest
import publication_artifact as publication


class PublicationArtifactTests(unittest.TestCase):
    def test_partial_rerun_reuses_last_successful_image_attempt(self):
        artifacts = [
            {'id': 10, 'name': 'release-amd64-1', 'expired': False},
            {'id': 20, 'name': 'release-amd64-2', 'expired': False},
            {'id': 30, 'name': 'release-arm64-3', 'expired': False},
            {'id': 40, 'name': 'release-amd64-4', 'expired': True}]
        self.assertEqual(publication.select(artifacts), 'release-amd64-2')

    def test_missing_expired_or_similarly_named_artifacts_refused(self):
        for artifacts in [[], [{'id': 1, 'name': 'release-amd64-1', 'expired': True}],
                          [{'id': 1, 'name': 'release-amd64-1\ninjected=true', 'expired': False}]]:
            with self.subTest(artifacts=artifacts), self.assertRaises(ValueError):
                publication.select(artifacts)
