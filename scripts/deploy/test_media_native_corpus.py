"""Mandatory native corpus reports must cover the complete trusted synthetic matrix."""
from __future__ import annotations

import copy
import json
from pathlib import Path
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "media"))
import test_sanitizer_isolation as harness

CASES = ["jpeg-20mp-texture", "png-20mp-texture", "webp-20mp-texture", "png-dense-noise",
         "jpeg-metadata-icc", "png-metadata-icc", "webp-metadata-icc", "jpeg-gps-rotation",
         "png-html-polyglot", "jpeg-zip-polyglot", "png-animation", "webp-animation",
         "png-declared-jpeg", "jpeg-bomb", "png-bomb", "webp-bomb", "png-truncated", "jpeg-byte-limit"]
PIN = "a" * 64


class NativeCorpusReportTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.path = Path(self.directory.name) / "corpus.json"
        self.report = {"schema": 1, "profileSha256": PIN, "complete": True,
                       "cases": [{"name": name} for name in CASES]}

    def verify(self, report=None):
        self.path.write_text(json.dumps(self.report if report is None else report))
        validate = getattr(harness, "validate_native_corpus_report", None)
        assert validate is not None, "Native Release qualification lacks mandatory full corpus report validation"
        return validate(self.path, PIN)

    def test_complete_unique_corpus_bound_to_the_running_profile_qualifies(self):
        self.assertEqual(self.report, self.verify())

    def test_missing_repeated_or_unknown_case_cannot_qualify(self):
        for change in ("missing", "duplicate", "unknown"):
            report = copy.deepcopy(self.report)
            if change == "missing":
                report["cases"].pop()
            elif change == "duplicate":
                report["cases"][-1] = report["cases"][0]
            else:
                report["cases"][-1]["name"] = "unreviewed"
            with self.subTest(change=change), self.assertRaises(ValueError):
                self.verify(report)

    def test_incomplete_wrong_schema_or_foreign_profile_cannot_qualify(self):
        for key, value in [("complete", False), ("complete", 1), ("schema", 2), ("profileSha256", "b" * 64)]:
            report = copy.deepcopy(self.report)
            report[key] = value
            with self.subTest(key=key, value=value), self.assertRaises(ValueError):
                self.verify(report)

    def test_oversized_report_cannot_qualify(self):
        self.report["unexpected"] = "x" * (1024 * 1024)
        with self.assertRaises(ValueError):
            self.verify()
