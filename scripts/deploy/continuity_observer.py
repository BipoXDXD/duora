"""Bounded, challenge/response readiness channel for the disposable Actions laboratory only."""
import os
import secrets
import selectors
import subprocess
import time
from typing import NoReturn


# Starting the transport is separate from each readiness request. The positive window still requires its first
# sample within three seconds; a slow admission is not permission to omit any part of that window.
STARTUP_SECONDS = 5
PROBE_SECONDS = 2
MAX_FRAME_BYTES = 128

SCRIPT = r'''
printf 'DUORA_DB_OBSERVER_V1\n'
while IFS= read -r nonce; do
    case "$nonce" in *[!0-9a-f]*|'') exit 64 ;; esac
    [ "${#nonce}" = 32 ] || exit 64
    pg_isready --quiet --timeout=2 --host=127.0.0.1 --username=duora_app >/dev/null 2>&1
    result=$?
    printf 'P %s %s\n' "$nonce" "$result"
done
'''


def database_observer_command(container: str) -> list[str]:
    return ['docker', 'exec', '--interactive', container, 'sh', '-c', SCRIPT]


class ObserverError(RuntimeError):
    """Closed diagnostic code, never raw process output, arguments or credentials."""
    def __init__(self, code: str):
        self.code = code
        super().__init__('Database observer failed: ' + code)


class DatabaseReadinessObserver:
    def __init__(self, command: list[str]):
        self._command = command
        self._process: subprocess.Popen[bytes] | None = None
        self._closed = False
        self._startup: float | None = None
        self._maximum_probe = 0.0
        self._samples = 0
        self._last_error: str | None = None

    @property
    def process(self) -> subprocess.Popen[bytes]:
        if self._process is None:
            raise ObserverError('not-started')
        return self._process

    @property
    def diagnostics(self) -> dict:
        return {'transport': 'persistent-docker-exec', 'startupSeconds': self._startup,
                'startupBudgetSeconds': STARTUP_SECONDS, 'probeBudgetSeconds': PROBE_SECONDS,
                'maximumProbeSeconds': self._maximum_probe, 'sampleCount': self._samples,
                'lastError': self._last_error}

    def _fail(self, code: str) -> NoReturn:
        self._last_error = code
        raise ObserverError(code)

    def start(self) -> None:
        if self._process is not None or self._closed:
            self._fail('already-started')
        started = time.monotonic()
        try:
            self._process = subprocess.Popen(self._command, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                             stderr=subprocess.DEVNULL, bufsize=0)
            if self._line(started + STARTUP_SECONDS, 'startup-timeout') != b'DUORA_DB_OBSERVER_V1':
                self._fail('invalid-greeting')
        except OSError:
            self._fail('start-failed')
        except ObserverError:
            self.abort()
            raise
        finally:
            self._startup = time.monotonic() - started

    def _line(self, deadline: float, timeout_code: str) -> bytes:
        output = self.process.stdout
        assert output is not None
        data = bytearray()
        with selectors.DefaultSelector() as selector:
            selector.register(output, selectors.EVENT_READ)
            while True:
                remaining = deadline - time.monotonic()
                if remaining <= 0 or not selector.select(remaining):
                    self._fail(timeout_code)
                chunk = os.read(output.fileno(), MAX_FRAME_BYTES + 1 - len(data))
                if time.monotonic() > deadline:
                    self._fail(timeout_code)
                if not chunk:
                    self._fail('truncated-frame' if data else 'unexpected-eof')
                data.extend(chunk)
                if len(data) > MAX_FRAME_BYTES:
                    self._fail('oversized-frame')
                if b'\n' in data:
                    # One response per fresh request; do not silently discard a queued/unsolicited frame.
                    if data.count(b'\n') != 1 or data[-1:] != b'\n':
                        self._fail('invalid-frame')
                    return bytes(data[:-1])

    def sample(self) -> dict:
        if self._closed or self.process.poll() is not None:
            self._fail('unexpected-exit')
        nonce = secrets.token_hex(16).encode('ascii')
        started = time.monotonic()
        try:
            assert self.process.stdin is not None
            self.process.stdin.write(nonce + b'\n')
            self.process.stdin.flush()
            frame = self._line(started + PROBE_SECONDS, 'probe-timeout').split(b' ')
            if (len(frame) != 3 or frame[0] != b'P' or frame[1] != nonce
                    or frame[2] not in (b'0', b'1', b'2', b'3')):
                self._fail('invalid-frame')
            self._samples += 1
            # Receipt, not the start of a potentially delayed operation, timestamps the observed result.
            return {'at': time.time(), 'database_ready': frame[2] == b'0'}
        except OSError:
            self._fail('transport-failed')
        finally:
            self._maximum_probe = max(self._maximum_probe, time.monotonic() - started)

    def close(self) -> None:
        if self._closed:
            return
        try:
            if self.process.poll() is not None:
                self._fail('unexpected-exit')
            assert self.process.stdin is not None
            self.process.stdin.close()
            if self.process.wait(timeout=3) != 0:
                self._fail('shutdown-failed')
        except (OSError, subprocess.TimeoutExpired):
            self._fail('shutdown-failed')
        finally:
            self.abort()

    def abort(self) -> None:
        """Reap the host transport even on a failed proof; the laboratory owns/removes its disposable container."""
        process = self._process
        if process is not None:
            if process.poll() is None:
                try:
                    process.terminate()
                except ProcessLookupError:
                    pass
                try:
                    process.wait(timeout=3)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait(timeout=3)
            for stream in (process.stdin, process.stdout):
                if stream is not None:
                    try:
                        stream.close()
                    except OSError:
                        pass
        self._closed = True

    def __enter__(self):
        self.start()
        return self

    def __exit__(self, exception_type, _exception, _traceback):
        if exception_type is None:
            self.close()
        else:
            self.abort()
