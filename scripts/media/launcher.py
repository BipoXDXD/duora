"""Linux-only, fixed-profile sanitizer launcher. No deployment or service activation."""
from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path


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
        raise NotImplementedError

    def create_args(self, job_id: str) -> list[str]:
        raise NotImplementedError


async def read_start(reader):
    raise NotImplementedError


class Journal:
    def __init__(self, directory: Path, digest: str):
        raise NotImplementedError

    def reserve(self, job_id: str, deadline: float) -> None:
        raise NotImplementedError

    def finish(self, job_id: str, state: str) -> None:
        raise NotImplementedError


@dataclass(frozen=True)
class Outcome:
    code: str
    payload: bytes = b""


class Supervisor:
    def __init__(self, profile, runtime, journal):
        raise NotImplementedError

    async def recover(self):
        raise NotImplementedError

    async def run(self, job_id, wire, budget):
        raise NotImplementedError

    def cancel(self, job_id):
        raise NotImplementedError

    def status(self, job_id):
        raise NotImplementedError


async def collect_bounded(stream, limit):
    raise NotImplementedError
