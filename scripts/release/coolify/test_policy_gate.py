"""Receipt parser tests only; these never substitute Linux/root execution."""
import unittest
import xml.etree.ElementTree as ET


class PolicyGateReceiptTests(unittest.TestCase):
    def validate(self, summary=None, xml=None):
        try: from policy_native_gate import qualify_results
        except ImportError:self.fail('Missing strict native gate result parser')
        return qualify_results(summary if summary is not None else {'receiver_sha256':'e9af03f08162ecf2bda094d38511b44b810043863d3104f60ea1046ae687f3b3',
            'tests':10,'failures':0,'errors':0,'skipped':0,'successful':True},
            ET.fromstring(xml or '<testsuites><testsuite tests="13" failures="0" errors="0" skipped="0">'+''.join('<testcase name="n'+str(i)+'" />' for i in range(13))+'</testsuite></testsuites>'))

    def test_exact_two_native_suites_require_the_expected_case_counts_and_zero_skips(self):
        self.assertEqual(self.validate(),{'receiver_tests':10,'reader_tests':13,'skipped':0})

    def test_skipped_failed_missing_case_or_wrong_receiver_bytes_never_qualify(self):
        good={'receiver_sha256':'e9af03f08162ecf2bda094d38511b44b810043863d3104f60ea1046ae687f3b3',
            'tests':10,'failures':0,'errors':0,'skipped':0,'successful':True}
        for key,value in [('tests',9),('tests',True),('skipped',1),('successful',False),('receiver_sha256','f'*64)]:
            with self.subTest(key=key):
                with self.assertRaises(ValueError):self.validate(summary=dict(good,**{key:value}))
        for xml in ('<testsuites><testsuite tests="13" failures="0" errors="0" skipped="1" /></testsuites>',
                    '<testsuites><testsuite tests="13" failures="0" errors="0" skipped="0"><testcase name="fake" /></testsuite></testsuites>'):
            with self.assertRaises(ValueError):self.validate(xml=xml)

class PublicNativeArtifactTests(unittest.TestCase):
    def test_only_the_owned_public_result_directory_and_metadata_are_readable_to_the_upload_user(self):
        import tempfile,stat
        from pathlib import Path
        import policy_native_gate as gate
        self.assertTrue(hasattr(gate,'prepare_public_output'),'Missing explicit readable public metadata output')
        with tempfile.TemporaryDirectory() as area:
            root=Path(area);private=root/'private-fixtures';private.mkdir(mode=0o700)
            marker=private/'marker';marker.write_text('private synthetic fixture');marker.chmod(0o600)
            output=root/'public-proof/qualification.json'
            gate.prepare_public_output(output)
            xml=output.parent/'reader-native.xml';xml.write_text('<testsuites/>');xml.chmod(0o600)
            gate.publish_public_metadata(output,{'scope':'public-test-counts-only'},xml)
            self.assertEqual(stat.S_IMODE(output.parent.stat().st_mode),0o755)
            self.assertEqual(stat.S_IMODE(output.stat().st_mode),0o644)
            self.assertEqual(stat.S_IMODE(xml.stat().st_mode),0o644)
            self.assertEqual(stat.S_IMODE(private.stat().st_mode),0o700)
            self.assertEqual(stat.S_IMODE(marker.stat().st_mode),0o600)

    def test_a_preexisting_directory_or_foreign_leaf_is_not_chmodded_or_reused(self):
        import tempfile,stat
        from pathlib import Path
        import policy_native_gate as gate
        self.assertTrue(hasattr(gate,'prepare_public_output'),'Missing exclusive owned output creation')
        with tempfile.TemporaryDirectory() as area:
            root=Path(area);existing=root/'existing';existing.mkdir(mode=0o700)
            with self.assertRaises((ValueError,FileExistsError)):gate.prepare_public_output(existing/'qualification.json')
            self.assertEqual(stat.S_IMODE(existing.stat().st_mode),0o700)
            output=root/'public/qualification.json';gate.prepare_public_output(output)
            foreign=root/'foreign';foreign.write_text('preserve');foreign.chmod(0o600)
            xml=output.parent/'reader-native.xml';xml.symlink_to(foreign)
            with self.assertRaises(ValueError):gate.publish_public_metadata(output,{'scope':'counts'},xml)
            self.assertEqual(stat.S_IMODE(foreign.stat().st_mode),0o600)
