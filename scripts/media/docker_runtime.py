"""Narrow Docker CLI boundary, owned only by the dedicated rootless launcher user."""
from __future__ import annotations

import asyncio
import json
import os
import signal
import stat
from pathlib import Path

from launcher import LABEL, collect_bounded, job_name, private_directory, require


class DockerRuntime:
    def __init__(self, profile):
        self.profile = profile
        private_directory(Path(profile.journal_directory))
        self.config = Path(profile.journal_directory) / "cli"
        self.config.mkdir(mode=0o700, exist_ok=True)
        private_directory(self.config)
        # This directory never receives registry credentials or Docker contexts.
        require(not any(self.config.iterdir()))
        self.prefix = ["/usr/bin/docker", "--config", str(self.config), "--host", "unix://" + profile.runtime_socket]
        self.environment = {"PATH": "/usr/bin:/bin", "HOME": "/nonexistent", "LC_ALL": "C"}

    async def process(self, args, *, wire=b"", stdout_limit=65536, stderr_limit=65536):
        process = await asyncio.create_subprocess_exec(*self.prefix, *args, stdin=asyncio.subprocess.PIPE,
            stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE,
            env=self.environment, start_new_session=True, limit=65536)
        async def write():
            try:
                process.stdin.write(wire)
                await process.stdin.drain()
            except (BrokenPipeError, ConnectionResetError):
                pass
            finally:
                process.stdin.close()
                try:
                    await process.stdin.wait_closed()
                except (BrokenPipeError, ConnectionResetError):
                    pass
        tasks = [asyncio.create_task(write()),
                 asyncio.create_task(collect_bounded(process.stdout, stdout_limit)),
                 asyncio.create_task(collect_bounded(process.stderr, stderr_limit, discard=True)),
                 asyncio.create_task(process.wait())]
        try:
            await asyncio.gather(*tasks)
            return process.returncode, tasks[1].result()
        finally:
            for task in tasks:
                if not task.done():
                    task.cancel()
            if process.returncode is None:
                try:
                    os.killpg(process.pid, signal.SIGKILL)
                except ProcessLookupError:
                    pass
                await asyncio.wait_for(process.wait(), 5)
            await asyncio.gather(*tasks, return_exceptions=True)

    async def command(self, *args):
        async with asyncio.timeout(20):
            code, output = await self.process(list(args))
            require(code == 0)
            return output

    async def recover(self):
        socket_info = Path(self.profile.runtime_socket).lstat()
        require(stat.S_ISSOCK(socket_info.st_mode) and socket_info.st_uid == os.geteuid() and socket_info.st_mode & 0o077 == 0)
        info = json.loads(await self.command("info", "--format", "{{json .}}"))
        require(info["CgroupDriver"] == "systemd" and info["CgroupVersion"] == "2")
        security = info["SecurityOptions"]
        require(any("rootless" in option for option in security))
        require(any("seccomp" in option and "unconfined" not in option for option in security))
        image = json.loads(await self.command("image", "inspect", self.profile.image))
        require(len(image) == 1 and image[0]["Config"].get("Volumes") in (None, {}))
        # Reject a shared daemon instead of touching another application's objects.
        ids = (await self.command("ps", "--all", "--quiet", "--no-trunc")).decode().split()
        for container_id in ids:
            require(len(container_id) == 64 and all(c in "0123456789abcdef" for c in container_id))
            item = json.loads(await self.command("inspect", container_id))[0]
            require(item["Config"].get("Labels", {}).get(LABEL) == "1")
            name = item["Name"].removeprefix("/")
            job_id = name.removeprefix("duora-media-")
            require(job_name(job_id) == name)
            require(await self.remove(job_id))

    async def execute(self, job_id, wire, budget):
        await self.command(*self.profile.create_args(job_id))
        item = json.loads(await self.command("inspect", job_name(job_id)))[0]
        host = item["HostConfig"]
        require(host["NetworkMode"] == "none" and host["ReadonlyRootfs"] and not host["Privileged"])
        require(host["Memory"] == host["MemorySwap"] == 536870912 and host["NanoCpus"] == 1000000000)
        require(host["PidsLimit"] == 64 and host["CapDrop"] == ["ALL"] and not host.get("CapAdd"))
        require(not host.get("Binds") and not item.get("Mounts") and not host.get("Devices"))
        require(host["LogConfig"]["Type"] == "none" and host["SecurityOpt"] == ["no-new-privileges"])
        require(item["Config"]["User"] == "1654:1654" and item["Config"]["Entrypoint"] == ["dotnet"])
        code, output = await self.process(["start", "--attach", "--interactive", job_name(job_id)],
            wire=wire, stdout_limit=budget[1]+4096, stderr_limit=budget[2])
        require(code == 0)
        return output

    async def inspect_exit(self, job_id):
        item = json.loads(await self.command("inspect", job_name(job_id)))[0]
        state = item["State"]
        require(not state["Running"] and not state["OOMKilled"] and not state["Error"])
        return state["ExitCode"]

    async def remove(self, job_id):
        name = job_name(job_id)
        async with asyncio.timeout(8):
            # A missing object is harmless only if a subsequent healthy list proves absence.
            await self.process(["rm", "--force", name])
            remaining = await self.command("ps", "--all", "--filter", "name=^/" + name + "$", "--quiet")
            return not remaining.strip()
