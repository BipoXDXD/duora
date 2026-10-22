"""Verify the approved vendored CLDR subset and shipped notices without network."""
import hashlib
import json
from pathlib import Path
import re
import unittest
from check_licenses import Policy, check_components

ROOT = Path(__file__).resolve().parents[2]
CATALOG = ROOT / 'content/location/cldr-48.2'


class LocationCatalogTests(unittest.TestCase):
    def test_integrity_and_approved_scope(self):
        inventory = json.loads((CATALOG / 'inventory.json').read_text())
        raw = (CATALOG / 'us-subdivisions.json').read_bytes()
        self.assertEqual(inventory['name'], 'duora-location-catalog-cldr-48.2')
        self.assertEqual(inventory['version'], '48.2')
        self.assertEqual(inventory['license'], 'Unicode-3.0')
        policy = Policy.from_dict(json.loads((ROOT / 'scripts/supply-chain/license-policy.json').read_text()))
        component = {'type': 'library', 'name': inventory['name'], 'version': inventory['version'], 'licenses': [{'license': {'id': inventory['license']}}]}
        self.assertEqual(check_components([component], policy).violations, [])
        self.assertEqual(hashlib.sha256(raw).hexdigest(), inventory['snapshotSha256'])
        snapshot = json.loads(raw)
        self.assertEqual(snapshot['countries'], ['BR', 'US'])
        rows = snapshot['usSubdivisions']
        codes = {row['code'] for row in rows}
        self.assertEqual(len(rows), 51)
        self.assertEqual(len(codes), 51)
        self.assertIn('US-DC', codes)
        self.assertTrue(codes.isdisjoint({'US-PR', 'US-GU', 'US-VI', 'US-AS', 'US-MP', 'US-UM'}))
        core = (ROOT / 'backend/src/Duora.Core/Profiles/ProfileLocation.cs').read_text()
        self.assertEqual(set(re.findall(r'"(US-[A-Z]{2})"', core)), codes)
        for row in rows:
            self.assertTrue(row['en'].strip())
            self.assertTrue(row['pt-BR'].strip())
        notice = (CATALOG / 'LICENSE').read_bytes()
        self.assertEqual(hashlib.sha256(notice).hexdigest(), 'b49d0e9f8ead51ca8b7df6fec89cc3ae6809198b4b9d43c74216da0118f23f5b')
        self.assertEqual(notice, (ROOT / 'frontend/public/licenses/CLDR-48.2.txt').read_bytes())


if __name__ == '__main__':
    unittest.main()
