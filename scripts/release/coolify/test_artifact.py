"""A drifting peer or installed receiver must fail read-only artifact verification."""

import io
import json
import shutil
import sys
import tempfile
import unittest
from contextlib import redirect_stdout
from pathlib import Path
from unittest.mock import patch

import receiver as r
import verify_artifact


class ArtifactTests(unittest.TestCase):
    def test_reviewed_receiver_matches_versioned_artifact(self) -> None:
        artifact = verify_artifact.verify(Path(__file__).parent)
        self.assertEqual(artifact["sha256"], r.receiver_digest())

    def test_peer_cli_rejects_modified_bytes_and_different_version(self) -> None:
        source = Path(__file__).parent
        with tempfile.TemporaryDirectory() as temp:
            peer = Path(temp)
            for name in ["receiver.py", "receiver-release.json"]:
                shutil.copyfile(source / name, peer / name)
            args = ["verify_artifact.py", "--peer", temp]
            with patch.object(sys, "argv", args), redirect_stdout(io.StringIO()):
                verify_artifact.main()
            with (peer / "receiver.py").open("a") as handle:
                handle.write("\n# drift\n")
            with patch.object(sys, "argv", args), self.assertRaises(ValueError):
                verify_artifact.main()
            shutil.copyfile(source / "receiver.py", peer / "receiver.py")
            artifact = json.loads((peer / "receiver-release.json").read_text())
            artifact["version"] = "different"
            (peer / "receiver-release.json").write_text(json.dumps(artifact))
            with patch.object(sys, "argv", args), self.assertRaises(ValueError):
                verify_artifact.main()


if __name__ == "__main__":
    unittest.main()
