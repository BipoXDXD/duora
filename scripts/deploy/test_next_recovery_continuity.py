"""One pure wrapper regression for two distinct observer proofs. No native or root claim."""
import copy
import hashlib
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

from compatibility import current_recovery49_lab as driver


class NextObserverProofTests(unittest.TestCase):
    def test_next_observer_retires_only_owned_old_ids_and_preserves_first_events(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            first_bytes = b"synthetic first observation, never native evidence\n"
            first_path = root / "events.ndjson"
            first_path.write_bytes(first_bytes)
            old_ids = {"start": "1" * 64, "end": "2" * 64}
            new_ids = {"start": "3" * 64, "end": "4" * 64}
            report = {"continuity": {"events_sha256": hashlib.sha256(first_bytes).hexdigest(),
                "event_barriers": {"reader_complete": True, "end_consumed": True,
                    "start": {"id": old_ids["start"]}, "end": {"id": old_ids["end"]}}}}
            frozen = copy.deepcopy(report["continuity"])
            calls = []
            class DataOnlyObserver:
                def __init__(self, owner):
                    self.owner = owner
                @staticmethod
                def require_owned_marker(owner, identifier, phase):
                    if identifier != old_ids[phase]:
                        raise AssertionError("Only the exact first proof identifiers may retire")
                    calls.append(("verify", phase, identifier))
                def start(self):
                    if [x for x in calls if x[0] == "remove"] != [("remove", "start", old_ids["start"]), ("remove", "end", old_ids["end"])]:
                        raise AssertionError("Both owned old names must be released before the next start")
                    self.events_path = self.owner.root / "events.ndjson"
                    self.start_marker, self.end_marker = {"id": new_ids["start"]}, {"id": new_ids["end"]}
            lab = object.__new__(driver.Native49Lab)
            lab.root, lab.report = root, report
            def docker(*args):
                self.assertEqual(args[:2], ("container", "rm"))
                self.assertEqual(len(args), 3)
                phase = next(k for k, v in old_ids.items() if v == args[2])
                self.assertIn(("verify", phase, args[2]), calls)
                calls.append(("remove", phase, args[2]))
                return args[2]
            with patch.object(lab, "docker", side_effect=docker), \
                 patch.object(driver.shared, "Continuity", DataOnlyObserver):
                observer = lab.prepare_next_continuity()
            self.assertEqual(observer.events_path, root / "next-events.ndjson")
            self.assertNotEqual(observer.events_path, first_path)
            assert observer.start_marker is not None and observer.end_marker is not None
            self.assertTrue(set(old_ids.values()).isdisjoint({observer.start_marker["id"], observer.end_marker["id"]}))
            with observer.events_path.open("xb") as handle:
                handle.write(b"synthetic second distinct observation\n")
            self.assertEqual(first_path.read_bytes(), first_bytes)
            self.assertEqual(report["continuity"], frozen)
