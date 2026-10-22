"""CI-only protocol fixture: real S2 control/journal, standalone decoder process.

This is NOT a sandbox or production runtime. Native containment remains in the
separate rootless matrix. All files and images here are synthetic test data.
"""
import asyncio
import json
import os
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[4] / "scripts" / "media"))
from launcher import ControlServer, Journal, Profile, Supervisor, collect_bounded


class ProcessRuntime:
    def __init__(self, root, dotnet, decoder):
        self.root, self.dotnet, self.decoder = root, dotnet, decoder
        self.process = None
        self.mode = "normal"

    def current_mode(self):
        return (self.root / "mode").read_text().strip()

    async def recover(self):
        pass

    async def execute(self, job_id, wire, budget):
        self.mode = self.current_mode()
        (self.root / "started").write_text(job_id)
        while self.current_mode() == "hold":
            await asyncio.sleep(0.01)
        if self.mode == "crash":
            raise OSError("Synthetic crash")
        self.process = await asyncio.create_subprocess_exec(
            self.dotnet, self.decoder, stdin=asyncio.subprocess.PIPE,
            stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE)

        async def write():
            self.process.stdin.write(wire)
            await self.process.stdin.drain()
            self.process.stdin.close()

        _, payload, _ = await asyncio.gather(
            write(), collect_bounded(self.process.stdout, 24*1024*1024+4096),
            collect_bounded(self.process.stderr, 65536, discard=True))
        await self.process.wait()
        if self.mode == "wrong-nonce":
            payload = payload[:5] + bytes([payload[5] ^ 1]) + payload[6:]
        elif self.mode == "wrong-hash":
            payload = payload[:21] + bytes([payload[21] ^ 1]) + payload[22:]
        elif self.mode == "wrong-version":
            payload = payload[:4] + b"\x02" + payload[5:]
        elif self.mode == "extra-output":
            payload += b"\x01"
        elif self.mode == "truncated":
            payload = payload[:-1]
        return payload

    async def inspect_exit(self, job_id):
        return 1 if self.mode == "after-output" else self.process.returncode

    async def remove(self, job_id):
        if self.process is not None and self.process.returncode is None:
            self.process.kill()
            await self.process.wait()
        (self.root / "cleanup").write_text(job_id)
        while self.current_mode() == "cleanup-delay":
            await asyncio.sleep(0.01)
        return self.mode != "cleanup-blocked"


class PausedInput:
    """Fault injection before S2 admits a job, while the original is still in transit."""
    def __init__(self, reader, root):
        self.reader, self.root = reader, root
        self.reads = 0

    async def readexactly(self, count):
        self.reads += 1
        if self.reads == 3 and (self.root / "mode").read_text().strip() == "transfer-hold":
            first = await self.reader.readexactly(1)
            (self.root / "transferring").write_text("1")
            try:
                while (self.root / "mode").read_text().strip() == "transfer-hold":
                    await asyncio.sleep(0.01)
                return first + await self.reader.readexactly(count - 1)
            finally:
                (self.root / "transfer-ended").write_text("1")
        return await self.reader.readexactly(count)

    async def read(self, count):
        return await self.reader.read(count)


class TestControlServer(ControlServer):
    async def handle(self, reader, writer):
        await super().handle(PausedInput(reader, Path(self.profile.journal_directory)), writer)


async def main():
    root = Path(sys.argv[1])
    profile = Profile("sha256:" + "11"*32, "/unused-test-runtime",
                      str(root / "control.sock"), str(root), os.getuid(), os.getgid())
    runtime = ProcessRuntime(root, sys.argv[2], sys.argv[3])
    supervisor = Supervisor(profile, runtime, Journal(root, profile.digest))
    await supervisor.recover()
    server = TestControlServer(profile, supervisor)
    await server.start()
    print(json.dumps({"digest": profile.digest, "uid": os.getuid()}), flush=True)
    try:
        await asyncio.Event().wait()
    finally:
        await server.close()


if __name__ == "__main__":
    asyncio.run(main())
