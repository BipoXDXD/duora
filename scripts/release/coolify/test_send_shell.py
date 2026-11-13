"""send.sh tells "not an index" (jq status 1) apart from a jq failure (any other status).

Runs the real send.sh with an isolated PATH. crane, ssh and python3 are stubs that record
their calls: no registry, no network, no Docker, no production data.
"""
import json
import os
import shutil
import stat
import subprocess
import tempfile
import unittest
from pathlib import Path

SEND_SH = Path(__file__).parent / 'send.sh'
COMMIT = 'c' * 40
REPOSITORY = 'ghcr.io/example/web'
SOURCE_DIGEST = 'sha256:' + 'a' * 64
LEAF_DIGEST = 'sha256:' + 'b' * 64
REFERENCE = REPOSITORY + '@' + SOURCE_DIGEST
FLAT_MANIFEST = json.dumps({'schemaVersion': 2, 'config': {}, 'layers': []})
INDEX_MANIFEST = json.dumps({'schemaVersion': 2, 'manifests': [
    {'digest': LEAF_DIGEST, 'platform': {'os': 'linux', 'architecture': 'amd64'}}]})
ONLY_THE_MANIFEST_READ = ['crane manifest ' + REFERENCE]
MISSING_COMMAND_STATUS = 127

CRANE_STUB = '''#!/bin/bash
printf 'crane %s\\n' "$*" >> "$STUB_LOG"
case "$1" in
  manifest) cat "$STUB_MANIFEST" ;;
  digest) printf '%s\\n' "$STUB_DIGEST" ;;
  pull)
    destination="${!#}"
    mkdir -p "$destination"
    printf '{"manifests":[]}' > "$destination/index.json"
    ;;
  *) exit 64 ;;
esac
'''
PYTHON_STUB = '''#!/bin/bash
case "$2" in
  requested) printf 'web\\t%s\\n' "$STUB_REFERENCE" ;;
  manifest) printf 'send.py manifest\\n' >> "$STUB_LOG" ;;
  *) exit 64 ;;
esac
'''
SSH_STUB = '''#!/bin/bash
printf 'ssh\\n' >> "$STUB_LOG"
'''
# Fails only the classification, so every later jq step would still let the release through.
FAILING_JQ_STUB = '''#!/bin/bash
if [[ $* == *'has("manifests")'* ]]; then
  exit {status}
fi
exec "{real_jq}" "$@"
'''


class SendShellTests(unittest.TestCase):
    def setUp(self):
        self.real_jq = shutil.which('jq')
        self.assertIsNotNone(self.real_jq, 'send.sh requires jq on the runner')
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.bin = self.root / 'bin'
        self.bin.mkdir()
        for tool in ('cat', 'dirname', 'mkdir', 'mktemp', 'mv', 'rm', 'tar'):
            (self.bin / tool).symlink_to(shutil.which(tool))
        self.stub('crane', CRANE_STUB)
        self.stub('python3', PYTHON_STUB)
        self.stub('ssh', SSH_STUB)

    def stub(self, name, body):
        path = self.bin / name
        path.write_text(body)
        path.chmod(path.stat().st_mode | stat.S_IXUSR)

    def use_real_jq(self):
        (self.bin / 'jq').symlink_to(self.real_jq)

    def use_jq_failing_with(self, status):
        self.stub('jq', FAILING_JQ_STUB.format(status=status, real_jq=self.real_jq))

    def send(self, manifest):
        """Run send.sh once and return its exit status and the recorded calls."""
        log = self.root / 'calls.log'
        log.write_text('')
        manifest_path = self.root / 'manifest.json'
        manifest_path.write_text(manifest)
        stage_parent = self.root / 'stage-parent'
        stage_parent.mkdir(exist_ok=True)
        environment = {
            'PATH': str(self.bin),
            'TMPDIR': str(stage_parent),
            'RELEASE_COMMIT': COMMIT,
            'RELEASE_PROJECT': 'duora',
            'DEPLOY_TARGET': 'cfrelease-duora@example.invalid',
            'DEPLOY_KEY': str(self.root / 'key'),
            'DEPLOY_KNOWN_HOSTS': str(self.root / 'known_hosts'),
            'STUB_LOG': str(log),
            'STUB_MANIFEST': str(manifest_path),
            'STUB_REFERENCE': REFERENCE,
            'STUB_DIGEST': SOURCE_DIGEST,
        }
        result = subprocess.run(['/bin/bash', str(SEND_SH)], env=environment, timeout=30,
                                capture_output=True, text=True, check=False)
        return result.returncode, log.read_text().splitlines()

    def test_index_is_resolved_to_its_linux_amd64_leaf(self):
        self.use_real_jq()
        status, calls = self.send(INDEX_MANIFEST)
        self.assertEqual(status, 0)
        self.assertEqual(calls[0], 'crane manifest ' + REFERENCE)
        self.assertTrue(calls[1].startswith(
            'crane pull --platform linux/amd64 --format oci ' + REPOSITORY + '@' + LEAF_DIGEST))
        self.assertEqual(calls[2:], ['send.py manifest', 'ssh'])

    def test_flat_manifest_answers_not_an_index_and_uses_the_registry_digest(self):
        self.use_real_jq()
        status, calls = self.send(FLAT_MANIFEST)
        self.assertEqual(status, 0)
        self.assertEqual(calls[:2], ['crane manifest ' + REFERENCE, 'crane digest ' + REFERENCE])
        self.assertTrue(calls[2].startswith(
            'crane pull --platform linux/amd64 --format oci ' + REFERENCE))
        self.assertEqual(calls[3:], ['send.py manifest', 'ssh'])

    def test_every_jq_failure_status_stops_with_that_status_before_any_transport(self):
        for jq_status in (2, 3, 4, 5, 124, 137):
            with self.subTest(jq_status=jq_status):
                self.use_jq_failing_with(jq_status)
                status, calls = self.send(FLAT_MANIFEST)
                self.assertEqual(status, jq_status)
                self.assertEqual(calls, ONLY_THE_MANIFEST_READ)

    def test_manifest_jq_cannot_classify_stops_before_any_transport(self):
        self.use_real_jq()
        unclassifiable = {'invalid JSON': '{not json', 'empty file': '', 'array': '[]',
                          'string': '"manifests"', 'null': 'null', 'false': 'false',
                          'index then flat': '{"manifests":[]}{"a":1}',
                          'flat then index': '{"a":1}{"manifests":[]}',
                          'two flat objects': '{"a":1}{"b":2}'}
        for name, manifest in unclassifiable.items():
            with self.subTest(name):
                status, calls = self.send(manifest)
                self.assertGreaterEqual(status, 2)
                self.assertEqual(calls, ONLY_THE_MANIFEST_READ)

    def test_missing_jq_stops_before_any_transport(self):
        self.assertFalse(os.path.lexists(self.bin / 'jq'))
        status, calls = self.send(FLAT_MANIFEST)
        self.assertEqual(status, MISSING_COMMAND_STATUS)
        self.assertEqual(calls, ONLY_THE_MANIFEST_READ)


if __name__ == '__main__':
    unittest.main()
