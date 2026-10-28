"""Discover only the new hermetic-profile/capture boundaries in the required ops gate."""
from __future__ import annotations

from pathlib import Path
import unittest


def load_tests(
    _loader: unittest.TestLoader, _tests: unittest.TestSuite, _pattern: str | None
) -> unittest.TestSuite:
    directory = Path(__file__).resolve().parents[2] / "tools" / "support-contact-runtime"
    return unittest.TestLoader().discover(
        str(directory), pattern="test_*.py", top_level_dir=str(directory)
    )
