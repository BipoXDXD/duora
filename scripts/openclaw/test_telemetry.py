import json
from pathlib import Path
import tempfile
import unittest
import telemetry
from telemetry_receiver import Store


def sample(at: float = 100):
    return {'collected_at': at, 'checks': dict.fromkeys(telemetry.CHECKS, True)}


class TelemetryTests(unittest.TestCase):
    def test_exact_schema_and_no_untrusted_content(self):
        good = sample()
        self.assertEqual(telemetry.snapshot(good, 110), good)
        invalid = [sample(True), sample(float('nan')), sample(float('inf')), sample(111), sample(-1),
                   {**good, 'logs': 'secret'}, {'collected_at': 100, 'checks': {}},
                   {**good, 'checks': {**good['checks'], 'exec:sudo': True}}]
        for value in (1, 'true', {}, None):
            invalid.append({**good, 'checks': {**good['checks'], 'service:web': value}})
        for value in invalid:
            with self.subTest(value=value), self.assertRaises(ValueError):
                telemetry.snapshot(value, 110)
        with self.assertRaises(ValueError):
            telemetry.snapshot(good, 221)

    def test_duplicate_keys_and_bounded_json(self):
        for raw in (b'{"checks":{},"checks":{}}', b' ' * (telemetry.MAX_BYTES + 1), b'[]', b'\xff'):
            with self.assertRaises(ValueError):
                telemetry.decode(raw)

    def test_replay_persists_and_invalid_packet_never_overwrites(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'host.json'
            store = Store(path, ['a' * 64])
            raw = json.dumps(sample()).encode()
            self.assertEqual(store.accept(raw, 'a' * 64, 110), 204)
            self.assertEqual(Store(path, ['a' * 64]).accept(raw, 'a' * 64, 111), 204)
            for value, identity in ((sample(99), 'a' * 64), (sample(100), 'b' * 64),
                                    ({**sample(100), 'checks': dict.fromkeys(telemetry.CHECKS, False)}, 'a' * 64)):
                with self.assertRaises(ValueError):
                    Store(path, ['a' * 64]).accept(json.dumps(value).encode(), identity, 111)
                self.assertEqual(json.loads(path.read_text()), sample())

    def test_recovery_after_outage_keeps_old_watermark_but_accepts_new_sample(self):
        with tempfile.TemporaryDirectory() as directory:
            store = Store(Path(directory) / 'host.json', ['a' * 64])
            store.accept(json.dumps(sample(100)).encode(), 'a' * 64, 110)
            self.assertEqual(Store(store.path, ['a' * 64]).accept(
                json.dumps(sample(1000)).encode(), 'a' * 64, 1001), 204)

    def test_lost_directory_sync_is_not_acknowledged_by_retry(self):
        import os
        import stat
        from unittest.mock import patch
        sync = os.fsync
        def fail_directory(fd):
            if stat.S_ISDIR(os.fstat(fd).st_mode):
                raise OSError('synthetic disk error')
            sync(fd)
        with tempfile.TemporaryDirectory() as directory:
            store = Store(Path(directory) / 'host.json', ['a' * 64])
            raw = json.dumps(sample()).encode()
            with patch('telemetry.os.fsync', side_effect=fail_directory):
                with self.assertRaises(OSError):
                    store.accept(raw, 'a' * 64, 110)
                self.assertTrue(store.path.exists())
                with self.assertRaises(OSError):
                    store.accept(raw, 'a' * 64, 111)
            self.assertEqual(store.accept(raw, 'a' * 64, 112), 204)
