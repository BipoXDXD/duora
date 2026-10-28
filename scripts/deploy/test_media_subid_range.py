"""Subordinate ID planning for the dedicated rootless sanitizer account (role media_sanitizer)."""
from __future__ import annotations

import io
import sys
import tempfile
import unittest
from contextlib import redirect_stderr, redirect_stdout
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "infra/ansible/roles/media_sanitizer/files"))
from subid_range import SubidConflict, SubidRange, main, parse, plan

# Shape observed on the current host: two accounts already own the first two ranges.
HOST = "goblinshift:100000:65536\ndeploy:165536:65536\n"
WANTED = SubidRange("duora-media-launcher", 231072, 65536)


class PlanTests(unittest.TestCase):
    def test_free_range_after_existing_accounts_is_absent(self):
        self.assertEqual("absent", plan(parse(HOST), WANTED))

    def test_identical_entry_is_present_so_reapplying_changes_nothing(self):
        self.assertEqual("present", plan(parse(HOST + "duora-media-launcher:231072:65536\n"), WANTED))

    def test_empty_file_is_absent(self):
        self.assertEqual("absent", plan(parse(""), WANTED))

    def test_adjacent_ranges_do_not_overlap(self):
        self.assertEqual("absent", plan(parse("a:165536:65536\nb:296608:1\n"), WANTED))

    def test_overlap_with_another_account_is_refused(self):
        for line in ["a:231071:2\n", "a:296607:10\n", "a:200000:200000\n", "a:231072:65536\n"]:
            with self.subTest(line=line), self.assertRaises(SubidConflict):
                plan(parse(line), WANTED)

    def test_account_with_a_different_range_is_refused_instead_of_replaced(self):
        for line in ["duora-media-launcher:400000:65536\n", "duora-media-launcher:231072:65537\n"]:
            with self.subTest(line=line), self.assertRaises(SubidConflict):
                plan(parse(line), WANTED)

    def test_duplicate_entries_for_the_account_are_refused(self):
        twice = "duora-media-launcher:231072:65536\n" * 2
        with self.assertRaises(SubidConflict):
            plan(parse(twice), WANTED)

    def test_ranges_smaller_than_a_user_namespace_are_refused(self):
        for wanted in [SubidRange("u", 231072, 65535), SubidRange("u", 0, 65536)]:
            with self.subTest(wanted=wanted), self.assertRaises(SubidConflict):
                plan([], wanted)

    def test_malformed_lines_are_refused(self):
        for text in ["a:1\n", "a:x:65536\n", ":1:2\n", "a:1:2:3\n", "a:-1:65536\n"]:
            with self.subTest(text=text), self.assertRaises(SubidConflict):
                parse(text)

    def test_comments_and_blank_lines_are_ignored(self):
        self.assertEqual([SubidRange("a", 1, 2)], parse("# c\n\na:1:2\n"))


class CommandTests(unittest.TestCase):
    def run_main(self, subuid: str, subgid: str) -> tuple[int, str, str]:
        with tempfile.TemporaryDirectory() as directory:
            uid, gid = Path(directory) / "subuid", Path(directory) / "subgid"
            uid.write_text(subuid)
            gid.write_text(subgid)
            out, err = io.StringIO(), io.StringIO()
            with redirect_stdout(out), redirect_stderr(err):
                code = main(["--user", WANTED.owner, "--start", "231072", "--count", "65536", str(uid), str(gid)])
            return code, out.getvalue(), err.getvalue()

    def test_reports_absent_when_both_files_lack_the_range(self):
        self.assertEqual((0, "absent\n", ""), self.run_main(HOST, HOST))

    def test_reports_present_when_both_files_have_the_range(self):
        both = HOST + "duora-media-launcher:231072:65536\n"
        self.assertEqual((0, "present\n", ""), self.run_main(both, both))

    def test_files_that_disagree_fail_without_a_decision(self):
        code, out, err = self.run_main(HOST + "duora-media-launcher:231072:65536\n", HOST)
        self.assertEqual((2, ""), (code, out))
        self.assertIn("disagree", err)

    def test_conflict_exits_2_and_prints_nothing_on_stdout(self):
        code, out, err = self.run_main("x:231072:65536\n", HOST)
        self.assertEqual((2, ""), (code, out))
        self.assertIn("overlaps", err)


if __name__ == "__main__":
    unittest.main()
