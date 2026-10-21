"""Integration with the existing reviewed schema rollback predicate."""
import importlib.util
import json
import sys
import tempfile
import unittest
from pathlib import Path
from receiver import link_previous

class PredecessorExecutorTests(unittest.TestCase):
    def test_trusted_link_preserves_recorded_schema_rollback(self):
        source=Path(__file__).resolve().parents[2]/'deploy/duora_deploy.py'
        spec=importlib.util.spec_from_file_location('reviewed_duora',source)
        module=importlib.util.module_from_spec(spec);sys.modules[spec.name]=module;spec.loader.exec_module(module)
        previous='20260101000000_Initial';latest='20260201000000_Next'
        old={'schemaVersion':1,'releaseId':'old','commit':'a'*40,'createdAt':'2026-10-21T00:00:00+00:00',
             'architecture':'amd64','images':{role:'ghcr.io/bipoxdxd/duora-'+role+'@sha256:'+'b'*64 for role in ['web','worker','migrator','edge','postgres']},
             'contractVersion':'v1','schema':{'min':previous,'max':previous},'migrations':[previous],
             'sbom':{role:'https://example.com/sbom' for role in ['web','worker','migrator','edge','postgres']},
             'gates':{'status':'passed','runUrl':'https://example.com/ci'},'previousReleaseId':None}
        release=module.parse_manifest(json.dumps(old),image_prefix='ghcr.io/bipoxdxd/duora-')
        current={**old,'releaseId':'new','schema':{'min':latest,'max':latest},'migrations':[previous,latest]}
        with tempfile.TemporaryDirectory() as temp:
            state=Path(temp);(state/'releases').mkdir();(state/'current.json').write_text('{"releaseId":"new"}')
            (state/'releases/old.json').write_text(json.dumps(old))
            (state/'releases/new.json').write_text(json.dumps(current))
            with self.assertRaises(module.DeployError):module._is_rollback(release,latest,state)
            linked=link_previous(current,'old');module.parse_manifest(json.dumps(linked),image_prefix='ghcr.io/bipoxdxd/duora-')
            (state/'releases/new.json').write_text(json.dumps(linked))
            self.assertTrue(module._is_rollback(release,latest,state))
if __name__=='__main__':unittest.main()
