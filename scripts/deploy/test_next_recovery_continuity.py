"""One pure wrapper regression for two distinct observer proofs. No native or root claim."""
import copy
import hashlib
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import duora_deploy as deploy
from compatibility import current_recovery49_lab as driver


class NextObserverProofTests(unittest.TestCase):
    def test_next_release_stages_real_files_before_recording_and_uses_incoming_env(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            state = root / "state"
            old = state / "releases" / "old.json"
            old.parent.mkdir(parents=True)
            old.write_bytes(b"original immutable fixture release\n")
            images = {role: "local.example/" + role + "@sha256:" + "1" * 64
                      for role in ("web", "worker", "edge", "postgres", "migrator")}
            raw = {"releaseId": "next-fixture-only", "images": images}
            lab = object.__new__(driver.Native49Lab)
            lab.root, lab.state, lab.executor = root, state, deploy
            lab.paths = deploy.Paths(root / "compose.json", root / "host.env", state)
            lab.incoming = deploy.Release("next-fixture-only", "2" * 40, "amd64", images, "v2",
                "20261025120000_PhotoReviewAppealDecisions", None, raw)
            compose = lab.prepare_next_release()
            staged = state / "staging" / "next-fixture-only.env"
            self.assertEqual(compose, ["docker", "compose", "--file", str(lab.paths.compose_file),
                "--env-file", str(lab.paths.host_env), "--env-file", str(staged),
                "--env-file", str(root / "incoming-local-migrator.env")])
            self.assertIn("DUORA_RELEASE_ID=next-fixture-only\n", staged.read_text())
            for role, reference in images.items():
                self.assertIn("DUORA_" + role.upper() + "_IMAGE=" + reference + "\n", staged.read_text())
            env_before = staged.read_bytes()
            json_before = staged.with_suffix(".json").read_bytes()
            deploy._record_release(lab.incoming, state)
            self.assertEqual((state / "releases/next-fixture-only.env").read_bytes(), env_before)
            self.assertEqual((state / "releases/next-fixture-only.json").read_bytes(), json_before)
            self.assertFalse(staged.exists())
            self.assertFalse(staged.with_suffix(".json").exists())
            self.assertEqual(old.read_bytes(), b"original immutable fixture release\n")

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
