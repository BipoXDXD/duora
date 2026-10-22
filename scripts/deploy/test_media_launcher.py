"""Behavioral launcher boundaries; Docker/cgroup proofs run separately on hosted runners."""
from __future__ import annotations

import asyncio
import hashlib
import json
import os
import struct
import sys
import tempfile
import time
import unittest
from pathlib import Path
from unittest.mock import patch

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
    def test_candidate_preserves_decoder_heap_policy(self):
        # P174-4 preserves ImageSharp's allocator limit; it does not add a CLR heap limit.
        # A whole-heap cap of 256 MiB can reject an input that the current decoder accepts.
        args = Profile.from_dict(configuration(), 1002).create_args(JOB)
        self.assertFalse(any("GCHeapHardLimit" in value for value in args))

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
    def test_restart_removes_only_private_recognized_journal_temporaries(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            temporary = root/("journal-"+"a"*32+".tmp")
            temporary.write_text("[]"); temporary.chmod(0o600)
            unknown = root/"unrelated.tmp"; unknown.write_text("preserve")
            journal = Journal(root, "c"*64)
            self.assertFalse(temporary.exists())
            self.assertEqual("preserve", unknown.read_text())
            self.assertEqual({}, journal.records)

    def test_recognized_temporary_symlink_never_removes_its_target(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory); target = root/"other"; target.write_text("preserve")
            (root/("journal-"+"a"*32+".tmp")).symlink_to(target)
            with self.assertRaises(ValueError): Journal(root, "c"*64)
            self.assertEqual("preserve", target.read_text())

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
        # Exercise deadline cancellation while leaving a realistic cleanup reserve.
        # A 10ms total can legitimately become blocked when disk/scheduling consumes it.
        result = await asyncio.wait_for(self.supervisor.run(JOB, request(), (16777216,25165824,65536,1000)), 3)
        self.assertEqual("failed", result.code)
        self.assertEqual(b"", result.payload)

    async def test_cleanup_cannot_turn_an_expired_attempt_into_success(self):
        class SlowCleanupRuntime(RuntimeDouble):
            async def remove(self, job_id):
                await asyncio.sleep(0.1)
                return True

        self.supervisor.runtime = SlowCleanupRuntime()
        result = await asyncio.wait_for(self.supervisor.run(JOB, request(), (16777216,25165824,65536,10)), 1)
        self.assertNotEqual("exited", result.code)
        self.assertEqual(b"", result.payload)

    async def test_durable_finish_crossing_deadline_never_releases_bytes(self):
        class SlowFinishJournal(Journal):
            def finish(self, job_id, state):
                time.sleep(0.05)  # Reproduce blocking fsync; asyncio timeout cannot preempt it.
                super().finish(job_id, state)
        self.supervisor.journal = SlowFinishJournal(Path(self.directory.name), "c"*64)
        result = await self.supervisor.run(JOB, request(), (16777216,25165824,65536,20))
        self.assertNotEqual("exited", result.code)
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


class ControlSocketTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        from launcher import ControlServer, Supervisor
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        config = configuration()
        config.update(control_socket=str(Path(self.directory.name)/"control.sock"),
                      journal_directory=self.directory.name, worker_uid=os.geteuid(), control_gid=os.getegid())
        self.profile = Profile.from_dict(config, 1002)
        self.runtime = RuntimeDouble()
        self.supervisor = Supervisor(self.profile, self.runtime, Journal(Path(self.directory.name), "c"*64))
        await self.supervisor.recover()
        self.server = ControlServer(self.profile, self.supervisor)
        await self.server.start()
        self.addAsyncCleanup(self.server.close)

    async def exchange(self, data):
        reader, writer = await asyncio.open_unix_connection(self.profile.control_socket)
        writer.write(data); await writer.drain(); writer.write_eof()
        response = await asyncio.wait_for(reader.read(), 2)
        writer.close(); await writer.wait_closed()
        return response

    async def test_start_response_is_framed_and_contains_only_confirmed_exit_bytes(self):
        response = await self.exchange(b"DMLC\x01\x01" + bytes.fromhex(JOB) + request())
        self.assertEqual(b"DMLR\x01\x00" + bytes.fromhex(JOB), response[:22])
        self.assertEqual(bytes.fromhex(self.profile.digest), response[22:54])
        self.assertEqual(b"synthetic response", response[54:])

    async def test_unknown_command_and_trailing_control_bytes_are_refused(self):
        self.assertEqual(b"", await self.exchange(b"DMLC\x01\x04" + bytes.fromhex(JOB)))
        self.assertEqual(b"", await self.exchange(b"DMLC\x01\x02" + bytes.fromhex(JOB) + b"x"))
        self.assertFalse(self.runtime.started.is_set())

    async def test_peer_identity_is_checked_before_reading_any_request(self):
        from launcher import ControlServer
        await self.server.close()
        config = configuration(); config.update(control_socket=self.profile.control_socket,
            journal_directory=self.directory.name, worker_uid=os.geteuid()+100, control_gid=os.getegid())
        self.server = ControlServer(Profile.from_dict(config,1002), self.supervisor)
        await self.server.start()
        reader, writer = await asyncio.open_unix_connection(self.profile.control_socket)
        self.assertEqual(b"", await asyncio.wait_for(reader.read(1), 1))
        writer.close(); await writer.wait_closed()
        self.assertFalse(self.runtime.started.is_set())
        await self.server.close()


class RuntimeSafetyTests(unittest.TestCase):
    def test_authority_lock_uses_fixed_state_directory_independent_of_profile_paths(self):
        from launcher import acquire_runtime_lock
        with tempfile.TemporaryDirectory() as directory, tempfile.TemporaryDirectory() as runtime:
            with patch("launcher.AUTHORITY_DIRECTORY", Path(directory)):
                descriptor = acquire_runtime_lock(str(Path(runtime)/"docker.sock"))
                try:
                    self.assertTrue((Path(directory)/"duora-media-launcher.lock").exists())
                    self.assertFalse((Path(runtime)/"duora-media-launcher.lock").exists())
                finally: os.close(descriptor)

    def test_only_reviewed_builtin_seccomp_and_rootless_systemd_are_accepted(self):
        from docker_runtime import validate_runtime_security
        valid = {"CgroupDriver": "systemd", "CgroupVersion": "2",
                 "SecurityOptions": ["name=rootless", "name=seccomp,profile=builtin"]}
        validate_runtime_security(valid)
        for field, value in [
            ("CgroupDriver", "none"), ("CgroupDriver", "cgroupfs"), ("CgroupVersion", "1"),
            ("SecurityOptions", ["name=rootless", "name=seccomp,profile=/custom/allow-all.json"]),
            ("SecurityOptions", ["name=rootless", "name=seccomp,profile=unconfined"]),
            ("SecurityOptions", ["name=seccomp,profile=builtin"]),
        ]:
            with self.subTest(field=field, value=value), self.assertRaises(ValueError):
                validate_runtime_security({**valid, field: value})

    def test_authority_lock_is_shared_by_all_profiles_for_the_same_daemon(self):
        from launcher import acquire_runtime_lock
        with tempfile.TemporaryDirectory() as directory, patch("launcher.AUTHORITY_DIRECTORY", Path(directory)):
            runtime_socket = str(Path(directory)/"docker.sock")
            first = acquire_runtime_lock(runtime_socket)
            try:
                with self.assertRaises(BlockingIOError):
                    acquire_runtime_lock(runtime_socket)
            finally:
                os.close(first)
            # Releasing the first authority permits a new launcher after recovery.
            os.close(acquire_runtime_lock(runtime_socket))

    def test_launcher_has_no_fallback_to_shared_or_rootful_runtime(self):
        from docker_runtime import DockerRuntime
        with tempfile.TemporaryDirectory() as directory:
            cfg=configuration(); cfg['journal_directory']=directory
            runtime=DockerRuntime(Profile.from_dict(cfg,1002))
            self.assertEqual(["/usr/bin/docker","--config",str(Path(directory)/"cli"),"--host","unix:///run/user/1002/docker.sock"],runtime.prefix)
            self.assertNotIn("DOCKER_HOST",runtime.environment)
            self.assertNotIn("AWS_ACCESS_KEY_ID",runtime.environment)
            self.assertEqual("/nonexistent",runtime.environment['HOME'])
