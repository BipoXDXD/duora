"""Explicit Linux launcher entry point. Reads a private administrator-owned profile only."""
from __future__ import annotations

import asyncio
import fcntl
import json
import os
import signal
import stat
import sys
from pathlib import Path

from docker_runtime import DockerRuntime
from launcher import ControlServer, Journal, Profile, private_directory, require


async def serve(profile):
    private_directory(Path(profile.journal_directory))
    lock = os.open(Path(profile.journal_directory)/"launcher.lock", os.O_WRONLY | os.O_CREAT | os.O_NOFOLLOW, 0o600)
    try:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        from launcher import Supervisor
        supervisor = Supervisor(profile, DockerRuntime(profile), Journal(Path(profile.journal_directory),profile.digest))
        await supervisor.recover()
        server = ControlServer(profile, supervisor)
        await server.start()
        stopped = asyncio.Event()
        for event in (signal.SIGTERM, signal.SIGINT):
            asyncio.get_running_loop().add_signal_handler(event, stopped.set)
        try:
            await stopped.wait()
        finally:
            await server.close()
    finally:
        os.close(lock)


def main():
    try:
        require(sys.platform == "linux" and os.getuid() == os.geteuid() and os.geteuid() > 0 and len(sys.argv) == 2)
        path = Path(sys.argv[1])
        require(path.is_absolute() and path.resolve() == path)
        fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW)
        with os.fdopen(fd, "rb") as source:
            info = os.fstat(source.fileno())
            require(stat.S_ISREG(info.st_mode) and info.st_uid in (0, os.geteuid()) and info.st_mode & 0o022 == 0)
            data = source.read(4097)
            require(len(data) <= 4096)
        profile = Profile.from_dict(json.loads(data), os.geteuid())
        asyncio.run(serve(profile))
        return 0
    except (ValueError, OSError, KeyError, TypeError, json.JSONDecodeError, TimeoutError):
        # Only the exit status leaves the process. Detailed diagnostics could contain input or paths.
        return 70


if __name__ == "__main__":
    raise SystemExit(main())
