"""Linux-only fixed-profile sanitizer launcher. No deployment or service activation."""
from __future__ import annotations

import asyncio
import hashlib
import json
import math
import os
import re
import stat
import struct
import time
from dataclasses import asdict, dataclass
from pathlib import Path

JOB_PATTERN = re.compile(r"[0-9a-f]{32}")
MAX_INPUT = 5 * 1024 * 1024
MAX_OUTPUT = 24 * 1024 * 1024 + 4096
LABEL = "io.duora.media.managed"


def require(condition: bool) -> None:
    if not condition:
        raise ValueError("Invalid launcher input or state.")


def job_name(job_id: str) -> str:
    require(isinstance(job_id, str) and JOB_PATTERN.fullmatch(job_id) is not None)
    return "duora-media-" + job_id


def private_directory(path: Path) -> None:
    info = path.lstat()
    require(stat.S_ISDIR(info.st_mode) and info.st_uid == os.geteuid() and info.st_mode & 0o077 == 0)


@dataclass(frozen=True)
class Profile:
    image: str
    runtime_socket: str
    control_socket: str
    journal_directory: str
    worker_uid: int
    control_gid: int

    @classmethod
    def from_dict(cls, value: dict, uid: int) -> Profile:
        require(type(uid) is int and uid > 0)
        require(set(value) == {"schema", "image", "runtime_socket", "control_socket", "journal_directory", "worker_uid", "control_gid"})
        require(type(value["schema"]) is int and value["schema"] == 1)
        require(type(value["worker_uid"]) is int and value["worker_uid"] > 0 and value["worker_uid"] != uid)
        require(type(value["control_gid"]) is int and value["control_gid"] > 0)
        require(isinstance(value["image"], str) and re.fullmatch(r"(?:sha256:|ghcr\.io/bipoxdxd/duora-media-sanitizer@sha256:)[0-9a-f]{64}", value["image"]) is not None)
        require(value["runtime_socket"] == f"/run/user/{uid}/docker.sock")
        for key in ["control_socket", "journal_directory"]:
            path = value[key]
            require(isinstance(path, str) and path.startswith("/") and "\x00" not in path and ".." not in Path(path).parts)
        require(len(value["control_socket"].encode()) < 100)
        return cls(**{key: item for key, item in value.items() if key != "schema"})

    @property
    def digest(self) -> str:
        return hashlib.sha256(json.dumps({"policy": "s2-candidate-v1", **asdict(self)}, sort_keys=True).encode()).hexdigest()

    def create_args(self, job_id: str) -> list[str]:
        name = job_name(job_id)
        return ["create", "--name", name, "--label", LABEL + "=1", "--label", "io.duora.media.profile=" + self.digest,
                "--network", "none", "--pull", "never", "--read-only", "--user", "1654:1654", "--cap-drop", "ALL",
                "--security-opt", "no-new-privileges", "--memory", "536870912", "--memory-swap", "536870912",
                "--cpus", "1", "--pids-limit", "64", "--log-driver", "none", "--ipc", "none", "--cgroupns", "private",
                "--tmpfs", "/tmp:rw,noexec,nosuid,nodev,size=16777216,uid=1654,gid=1654,mode=700",
                "--env", "DOTNET_EnableDiagnostics=0", "--env", "DOTNET_GCHeapHardLimit=0x10000000",
                "--interactive", "--entrypoint", "dotnet", self.image, "Duora.MediaSanitizer.dll"]


async def read_start(reader):
    try:
        header = await reader.readexactly(74)
        require(header[:5] == b"DMRQ\x01" and header[21] in (1, 2, 3))
        length = struct.unpack_from("<i", header, 22)[0]
        budget = struct.unpack_from("<iiii", header, 58)
        require(0 < length <= MAX_INPUT)
        require(0 < budget[0] <= 16777216 and 0 < budget[1] <= 25165824)
        require(0 <= budget[2] <= 65536 and 0 < budget[3] <= 50000)
        payload = await reader.readexactly(length)
        require(hashlib.sha256(payload).digest() == header[26:58])
        require(not await reader.read(1))
        return header + payload, budget
    except asyncio.IncompleteReadError as error:
        raise ValueError("Truncated launcher request.") from error


class Journal:
    """Bounded admission ledger; never contains input/output bytes or exception details."""
    def __init__(self, directory: Path, digest: str):
        private_directory(directory)
        self.directory = directory
        self.digest = digest
        self.path = directory / "journal.json"
        self.records: dict[str, dict] = {}
        if self.path.exists() or self.path.is_symlink():
            info = self.path.lstat()
            require(stat.S_ISREG(info.st_mode) and info.st_uid == os.geteuid() and info.st_mode & 0o077 == 0)
            require(info.st_size <= 2 * 1024 * 1024)
            try:
                records = json.loads(self.path.read_text())
                require(isinstance(records, list) and len(records) <= 4096)
                for row in records:
                    require(set(row) == {"id", "state", "deadline", "digest"})
                    job_name(row["id"])
                    require(row["id"] not in self.records)
                    require(row["state"] in {"accepted", "finished", "failed", "cancelled", "blocked"})
                    require(type(row["deadline"]) in (int, float) and math.isfinite(row["deadline"]))
                    require(isinstance(row["digest"], str) and re.fullmatch(r"[0-9a-f]{64}", row["digest"]) is not None)
                    self.records[row["id"]] = row
            except (KeyError, TypeError, json.JSONDecodeError) as error:
                raise ValueError("Invalid launcher journal.") from error

    def _save(self):
        temporary = self.directory / ("journal-" + os.urandom(16).hex() + ".tmp")
        fd = os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
        try:
            with os.fdopen(fd, "w") as output:
                json.dump(list(self.records.values()), output, separators=(",", ":"))
                output.flush()
                os.fsync(output.fileno())
            os.replace(temporary, self.path)
            directory_fd = os.open(self.directory, os.O_RDONLY | os.O_DIRECTORY)
            try:
                os.fsync(directory_fd)
            finally:
                os.close(directory_fd)
        finally:
            temporary.unlink(missing_ok=True)

    def reserve(self, job_id: str, deadline: float) -> None:
        job_name(job_id)
        require(job_id not in self.records and len(self.records) < 4096 and math.isfinite(deadline) and deadline > 0)
        self.records[job_id] = {"id": job_id, "state": "accepted", "deadline": deadline, "digest": self.digest}
        self._save()

    def finish(self, job_id: str, state: str) -> None:
        require(job_id in self.records and state in {"finished", "failed", "cancelled", "blocked"})
        self.records[job_id]["state"] = state
        self._save()


@dataclass(frozen=True)
class Outcome:
    code: str
    payload: bytes = b""


class Supervisor:
    def __init__(self, profile, runtime, journal):
        self.profile = profile
        self.runtime = runtime
        self.journal = journal
        self.blocked = True
        self.lock = asyncio.Lock()
        self.current = None

    async def recover(self):
        self.blocked = True
        await self.runtime.recover()
        for job_id, record in self.journal.records.items():
            if record["state"] in {"accepted", "blocked"}:
                self.journal.finish(job_id, "failed")
        self.blocked = False

    async def run(self, job_id, wire, budget):
        job_name(job_id)
        if self.blocked:
            return Outcome("blocked")
        if job_id in self.journal.records:
            return Outcome("duplicate")
        if self.lock.locked():
            return Outcome("busy")
        require(wire[5:21].hex() == job_id)
        async with self.lock:
            try:
                self.journal.reserve(job_id, time.time() + budget[3]/1000)
            except (OSError, ValueError):
                self.blocked = True
                return Outcome("blocked")
            self.current = (job_id, asyncio.current_task())
            payload = b""
            state = "failed"
            try:
                async with asyncio.timeout(budget[3]/1000):
                    payload = await self.runtime.execute(job_id, wire, budget)
                    if await self.runtime.inspect_exit(job_id) == 0:
                        state = "finished"
            except asyncio.CancelledError:
                state = "cancelled"
            except (OSError, ValueError, TimeoutError):
                state = "failed"
            finally:
                # The independent cleanup budget is never the already-cancelled job deadline.
                try:
                    async with asyncio.timeout(10):
                        removed = await self.runtime.remove(job_id)
                except (OSError, ValueError, TimeoutError, asyncio.CancelledError):
                    removed = False
                if not removed:
                    state = "blocked"
                    self.blocked = True
                try:
                    self.journal.finish(job_id, state)
                except (OSError, ValueError):
                    self.blocked = True
                    state = "blocked"
                self.current = None
            if state == "finished":
                return Outcome("exited", payload)
            return Outcome(state)

    def cancel(self, job_id):
        if self.current and self.current[0] == job_id:
            self.current[1].cancel()
            return True
        return False

    def status(self, job_id):
        if self.blocked:
            return "blocked"
        if self.current and self.current[0] == job_id:
            return "running"
        record = self.journal.records.get(job_id)
        return "finished" if record else "unknown"


async def collect_bounded(stream, limit, *, discard=False):
    size = 0
    output = bytearray()
    while True:
        chunk = await stream.read(min(65536, limit + 1 - size))
        if not chunk:
            return bytes(output)
        size += len(chunk)
        require(size <= limit)
        if not discard:
            output.extend(chunk)


class ControlServer:
    def __init__(self, profile, supervisor):
        raise NotImplementedError

    async def start(self):
        raise NotImplementedError

    async def close(self):
        raise NotImplementedError
