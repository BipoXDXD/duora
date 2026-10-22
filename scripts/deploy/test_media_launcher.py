"""Behavioral launcher boundaries; Docker/cgroup proofs run separately on hosted runners."""
from __future__ import annotations

import asyncio
import hashlib
import json
import os
import struct
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "media"))
from launcher import Journal, Profile, read_start


IMAGE = "sha256:" + "a" * 64
JOB = "b" * 32


def configuration():
    return {"schema": 1, "image": IMAGE, "runtime_socket": "/run/user/1002/docker.sock",
            "control_socket": "/run/duora-media/control.sock", "journal_directory": "/var/lib/duora-media",
            "worker_uid": 1654, "control_gid": 1654}


def request(content=b"synthetic", *, length=None, variant=16777216, total=25165824, stderr=65536, deadline=50000):
    return (b"DMRQ\x01" + bytes.fromhex(JOB) + b"\x01"
            + struct.pack("<i", len(content) if length is None else length) + hashlib.sha256(content).digest()
            + struct.pack("<iiii", variant, total, stderr, deadline) + content)


class ProfileTests(unittest.TestCase):
    def test_job_cannot_choose_runtime_authority_or_resource_flags(self):
        for field in ["command", "mounts", "cpu", "memory", "environment", "image_override"]:
            with self.subTest(field=field), self.assertRaises(ValueError):
                Profile.from_dict({**configuration(), field: "malicious"}, 1002)

    def test_rootful_socket_mutable_image_and_invalid_identity_are_refused(self):
        for field, value in [("runtime_socket", "/var/run/docker.sock"), ("image", "ubuntu:latest"),
                             ("worker_uid", 0), ("worker_uid", 1002), ("schema", True)]:
            with self.subTest(field=field), self.assertRaises(ValueError):
                Profile.from_dict({**configuration(), field: value}, 1002)
        with self.assertRaises(ValueError):
            Profile.from_dict(configuration(), 0)

    def test_candidate_cannot_mount_storage_pull_images_or_gain_network(self):
        args = Profile.from_dict(configuration(), 1002).create_args(JOB)
        for name, value in [("--network", "none"), ("--pull", "never"), ("--user", "1654:1654"),
                            ("--memory", "536870912"), ("--memory-swap", "536870912"),
                            ("--cpus", "1"), ("--pids-limit", "64"), ("--log-driver", "none")]:
            self.assertEqual(value, args[args.index(name) + 1])
        self.assertIn("--read-only", args)
        self.assertIn("no-new-privileges", args)
        self.assertIn("ALL", args)
        self.assertNotIn("--volume", args)
        self.assertNotIn("--mount", args)
        self.assertNotIn("unconfined", args)
        self.assertEqual([IMAGE, "Duora.MediaSanitizer.dll"], args[-2:])
        with self.assertRaises(ValueError):
            Profile.from_dict(configuration(), 1002).create_args("../../docker.sock")


class RequestTests(unittest.IsolatedAsyncioTestCase):
    async def parse(self, data):
        reader = asyncio.StreamReader()
        reader.feed_data(data)
        reader.feed_eof()
        return await read_start(reader)

    async def test_valid_wire_preserves_exact_bytes_and_restricted_budget(self):
        data = request(deadline=1000, stderr=0)
        wire, budget = await self.parse(data)
        self.assertEqual(data, wire)
        self.assertEqual((16777216, 25165824, 0, 1000), budget)

    async def test_oversized_length_fails_before_waiting_for_missing_payload(self):
        reader = asyncio.StreamReader()
        reader.feed_data(request(length=5242881)[:74])
        with self.assertRaises(ValueError):
            await asyncio.wait_for(read_start(reader), 1)

    async def test_wire_cannot_expand_quotas_or_accept_unknown_fields(self):
        cases = [request(length=-1), request(variant=16777217), request(total=25165825),
                 request(stderr=65537), request(deadline=50001), request(deadline=0),
                 request()+b"x", b"BAD!"+request()[4:], request()[:-1]]
        bad_hash = bytearray(request()); bad_hash[26] ^= 1; cases.append(bytes(bad_hash))
        for data in cases:
            with self.subTest(data=data[:26]), self.assertRaises(ValueError):
                await self.parse(data)


class JournalTests(unittest.TestCase):
    def test_replay_after_restart_never_launches_the_same_id_again(self):
        with tempfile.TemporaryDirectory() as directory:
            state = Journal(Path(directory), "c" * 64)
            state.reserve(JOB, 123.0)
            state.finish(JOB, "failed")
            restarted = Journal(Path(directory), "c" * 64)
            with self.assertRaises(ValueError):
                restarted.reserve(JOB, 124.0)
            persisted = json.loads((Path(directory)/"journal.json").read_text())
            self.assertEqual({"id", "state", "deadline", "digest"}, set(persisted[0]))
            self.assertEqual("failed", persisted[0]["state"])
            self.assertEqual(0, (Path(directory)/"journal.json").stat().st_mode & 0o077)

    def test_corrupt_or_linked_journal_fails_closed(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory)/"journal.json"
            path.write_text("not-json")
            with self.assertRaises(ValueError): Journal(Path(directory), "c"*64)
            path.unlink(); outside = Path(directory)/"other"; outside.write_text("[]")
            path.symlink_to(outside)
            with self.assertRaises(ValueError): Journal(Path(directory), "c"*64)


class RuntimeDouble:
    """Only the external runtime boundary is replaced; supervisor/journal execute real code."""
    def __init__(self):
        self.started = asyncio.Event()
        self.release = asyncio.Event()
        self.wait = False
        self.exit_code = 0
        self.cleanup_ok = True
        self.orphan = False

    async def recover(self):
        self.orphan = False

    async def execute(self, job_id, wire, budget):
        self.started.set()
        if self.wait:
            await self.release.wait()
        return b"synthetic response"

    async def inspect_exit(self, job_id):
        return self.exit_code

    async def remove(self, job_id):
        return self.cleanup_ok


class SupervisorTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        from launcher import Supervisor
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.runtime = RuntimeDouble()
        self.journal = Journal(Path(self.directory.name), "c"*64)
        self.supervisor = Supervisor(Profile.from_dict(configuration(), 1002), self.runtime, self.journal)
        await self.supervisor.recover()

    async def test_exit_zero_and_confirmed_cleanup_are_both_required_for_bytes(self):
        result = await self.supervisor.run(JOB, request(), (16777216,25165824,65536,50000))
        self.assertEqual("exited", result.code)
        self.assertEqual(b"synthetic response", result.payload)
        self.assertEqual("finished", self.supervisor.status(JOB))
        replay = await self.supervisor.run(JOB, request(), (16777216,25165824,65536,50000))
        self.assertEqual("duplicate", replay.code)
        self.assertEqual(b"", replay.payload)

    async def test_success_bytes_followed_by_crash_never_escape(self):
        self.runtime.exit_code = 137
        result = await self.supervisor.run(JOB, request(), (16777216,25165824,65536,50000))
        self.assertEqual("failed", result.code)
        self.assertEqual(b"", result.payload)

    async def test_unknown_termination_blocks_all_later_attempts(self):
        self.runtime.cleanup_ok = False
        result = await self.supervisor.run(JOB, request(), (16777216,25165824,65536,50000))
        self.assertEqual("blocked", result.code)
        result = await self.supervisor.run("d"*32, request(), (16777216,25165824,65536,50000))
        self.assertEqual("blocked", result.code)
        self.assertEqual(b"", result.payload)

    async def test_slot_is_not_reused_while_first_container_is_running(self):
        self.runtime.wait = True
        first = asyncio.create_task(self.supervisor.run(JOB, request(), (16777216,25165824,65536,50000)))
        await self.runtime.started.wait()
        self.assertEqual("running", self.supervisor.status(JOB))
        second = await self.supervisor.run("d"*32, request(), (16777216,25165824,65536,50000))
        self.assertEqual("busy", second.code)
        self.supervisor.cancel(JOB)
        self.assertEqual("cancelled", (await first).code)

    async def test_external_deadline_cancels_runtime_and_never_returns_response(self):
        self.runtime.wait = True
        result = await asyncio.wait_for(self.supervisor.run(JOB, request(), (16777216,25165824,65536,10)), 1)
        self.assertEqual("failed", result.code)
        self.assertEqual(b"", result.payload)

    async def test_startup_recovers_orphans_before_admitting_an_attempt(self):
        self.runtime.orphan = True
        await self.supervisor.recover()
        self.assertFalse(self.runtime.orphan)
        self.assertEqual("exited", (await self.supervisor.run(JOB, request(), (16777216,25165824,65536,50000))).code)

    async def test_output_and_stderr_collectors_stop_at_the_first_excess_byte(self):
        from launcher import collect_bounded
        reader = asyncio.StreamReader()
        reader.feed_data(b"x"*65)
        # No EOF: overflow must fail without waiting for a producer to close.
        with self.assertRaises(ValueError):
            await asyncio.wait_for(collect_bounded(reader,64), 1)
        reader = asyncio.StreamReader(); reader.feed_data(b"x"*64); reader.feed_eof()
        self.assertEqual(b"x"*64, await collect_bounded(reader,64))
