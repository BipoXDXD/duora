"""One real-root journal boundary. Data-only unit evidence, never binary/OCI recovery proof."""
import io
import json
import os
from pathlib import Path
import platform
import sys
import unittest
from unittest.mock import patch


def require_native_root():
    if not (sys.platform == "linux" and platform.machine() == "x86_64" and os.getuid() == os.geteuid() == 0
            and sys.version_info[:2] == (3, 12) and os.environ.get("GITHUB_ACTIONS") == "true"
            and os.environ.get("RUNNER_ENVIRONMENT") == "github-hosted"
            and os.environ.get("GITHUB_REPOSITORY") == "BipoXDXD/duora"):
        raise RuntimeError("Disposable hosted native root case only")


if __name__ == "__main__":
    require_native_root()

sys.path.insert(0, str(Path(__file__).resolve().parent))

import duora_deploy as deploy
from test_current_v2_recovery49 import _Exact48To49Fixture, CATALOG, SQL, SQL_HASH


class RootExact48To49DurabilityTests(_Exact48To49Fixture):
    def setUp(self):
        require_native_root()
        super().setUp()

    def test_exact49_attempt_is_durable_before_any_producer(self):
        context = {"current": self.old, "bindings": self.bindings,
                   "control": self.control, "catalog": CATALOG[:-1], "postgresId": "1" * 64}
        with patch.object(deploy, "_recovery_require_cached_images", return_value={}):
            store = deploy.arm_current_v2_deploy(self.new, self.paths, SQL, observe=lambda: context)
        self.assertIsInstance(store, deploy.RecoveryStore)
        self.assertEqual(store.load()["current"], self.old.raw)
        self.assertEqual(store.load()["incoming"], self.new.raw)
        self.assertEqual(store.load()["sqlSha256"], SQL_HASH)


if __name__ == "__main__":
    output = Path(sys.argv[1])
    temporary = Path(os.environ["RUNNER_TEMP"]).resolve()
    if not output.resolve().is_relative_to(temporary) or output.exists():
        raise RuntimeError("New owned runner output required")
    stream = io.StringIO()
    suite = unittest.TestSuite([RootExact48To49DurabilityTests("test_exact49_attempt_is_durable_before_any_producer")])
    result = unittest.TextTestRunner(stream=stream, verbosity=2).run(suite)
    receipt = {"scope": "native_root49_attempt_data_only_not_OCI_or_binary_proof", "tests": result.testsRun,
               "failures": len(result.failures), "errors": len(result.errors), "skipped": len(result.skipped),
               "native_root": True, "success": result.wasSuccessful(), "transcript": stream.getvalue()}
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(receipt, sort_keys=True) + "\n")
    print(json.dumps({key: value for key, value in receipt.items() if key != "transcript"}))
    sys.exit(0 if result.wasSuccessful() else 1)
