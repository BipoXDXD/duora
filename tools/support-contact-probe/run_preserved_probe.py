#!/usr/bin/env python3
"""Bounded private invocation of the already qualified PR250 helper, not live-cache proof."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import selectors
import signal
import subprocess
import sys
import time
import uuid


ARTIFACT_HEAD = "4da134f32f3cc0a2c699f776365f647549858f1a"
PINNED_FILES = {
    "Duora.IntegrationTests.dll": "11419c7d93a94666d535b5b5cfa2205d2ac0d1f8d0c840297f3eaa2489b044c3",
    "Duora.IntegrationTests.runtimeconfig.json": "fe68161356f78eea34bf996675608947d949623e5e3aded5a5afff9a796185d4",
    "SHA256SUMS": "14a253b4e6c596534f5fde37c8e7be67c1253c7480c15e1916423cd7b914f03a",
}
MAX_CAPTURE = 256 * 1024
MAX_OUTPUT = 16 * 1024
TIMEOUT = 15
CLEAN_ENV = {
    "PATH": "/usr/bin:/bin",
    "DOTNET_NOLOGO": "true",
    "DOTNET_CLI_TELEMETRY_OPTOUT": "true",
    "DOTNET_EnableDiagnostics": "0",
    "COMPlus_DbgEnableMiniDump": "0",
}


class Refusal(Exception):
    def __init__(self, reason):
        self.reason = reason


class Parser(argparse.ArgumentParser):
    def error(self, _message):
        raise Refusal("arguments_invalid")


def verify_artifact(directory):
    try:
        if {entry.name for entry in directory.iterdir()} != set(PINNED_FILES):
            raise Refusal("artifact_untrusted")
        for name, expected in PINNED_FILES.items():
            path = directory / name
            if path.is_symlink() or not path.is_file() or path.stat().st_size > 1024 * 1024:
                raise Refusal("artifact_untrusted")
            with path.open("rb") as stream:
                raw = stream.read(1024 * 1024 + 1)
            if len(raw) > 1024 * 1024 or hashlib.sha256(raw).hexdigest() != expected:
                raise Refusal("artifact_untrusted")
    except OSError:
        raise Refusal("artifact_untrusted") from None


def unique_object(pairs):
    value = {}
    for key, item in pairs:
        if key in value:
            raise ValueError()
        value[key] = item
    return value


def validate_capture(raw):
    try:
        value = json.loads(raw, object_pairs_hook=unique_object)
        if not isinstance(value, dict) or set(value) != {
            "Role", "ApplicationAssembly", "ContentRoot", "Environment", "Arguments",
        }:
            raise ValueError()
        if value["Role"] not in ("web", "worker"):
            raise ValueError()
        for name in ("ApplicationAssembly", "ContentRoot"):
            if not isinstance(value[name], str) or not Path(value[name]).is_absolute() or "\0" in value[name]:
                raise ValueError()
        environment, arguments = value["Environment"], value["Arguments"]
        if not isinstance(environment, dict) or not isinstance(arguments, list):
            raise ValueError()
        if not all(isinstance(arg, str) and "\0" not in arg for arg in arguments):
            raise ValueError()
        for key, item in environment.items():
            if not key or "=" in key or "\0" in key or (item is not None and not isinstance(item, str)):
                raise ValueError()
            if item and ("\0" in item or key.upper() in {
                "DOTNET_STARTUP_HOOKS", "ASPNETCORE_HOSTINGSTARTUPASSEMBLIES",
            }):
                raise ValueError()
    except (ValueError, TypeError, UnicodeError):
        raise Refusal("capture_invalid") from None


def run_bounded(command, payload, timeout):
    """Drain both pipes with a shared byte budget; never inherit ambient auth or print diagnostics."""
    if os.name != "posix":
        raise Refusal("platform_unsupported")
    process = None
    selector = selectors.DefaultSelector()
    output, total, sent, diagnostics = bytearray(), 0, 0, False
    deadline = time.monotonic() + timeout
    try:
        process = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                   stderr=subprocess.PIPE, env=CLEAN_ENV, start_new_session=True,
                                   close_fds=True)
        for stream, action in [(process.stdin, selectors.EVENT_WRITE),
                               (process.stdout, selectors.EVENT_READ),
                               (process.stderr, selectors.EVENT_READ)]:
            os.set_blocking(stream.fileno(), False)
            selector.register(stream, action)
        while selector.get_map():
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise Refusal("probe_timeout")
            for key, _ in selector.select(min(remaining, 0.1)):
                stream = key.fileobj
                if stream is process.stdin:
                    if sent < len(payload):
                        sent += os.write(stream.fileno(), payload[sent:sent + 4096])
                    if sent == len(payload):
                        selector.unregister(stream)
                        stream.close()
                else:
                    data = os.read(stream.fileno(), 4096)
                    if not data:
                        selector.unregister(stream)
                        stream.close()
                        continue
                    total += len(data)
                    if total > MAX_OUTPUT:
                        raise Refusal("output_too_large")
                    if stream is process.stdout:
                        output.extend(data)
                    else:
                        diagnostics = True
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise Refusal("probe_timeout")
        code = process.wait(timeout=remaining)
        if diagnostics:
            raise Refusal("probe_diagnostics")
        return code, bytes(output)
    except subprocess.TimeoutExpired:
        raise Refusal("probe_timeout") from None
    except OSError:
        raise Refusal("probe_failed") from None
    finally:
        selector.close()
        if process is not None:
            # The leader may already have exited while a descendant still owns a pipe.
            try:
                os.killpg(process.pid, signal.SIGKILL)
            except ProcessLookupError:
                pass
            process.wait()
            for stream in (process.stdin, process.stdout, process.stderr):
                stream.close()


def sanitize_reply(raw):
    try:
        value = json.loads(raw, object_pairs_hook=unique_object)
        if not isinstance(value, dict) or set(value) != {
            "status", "configuration_matches_approved", "ioptions_matches_approved", "source",
            "cached_runtime_options_observed", "infrastructure_module_id",
        } or value["status"] != "replayed":
            raise ValueError()
        for key in ("configuration_matches_approved", "ioptions_matches_approved"):
            if type(value[key]) is not bool:
                raise ValueError()
        if value["cached_runtime_options_observed"] is not False:
            raise ValueError()
        if value["source"] not in ("json", "environment", "command_line", "key_per_file"):
            raise Refusal("source_unsupported")
        module_id = str(uuid.UUID(value["infrastructure_module_id"]))
        return {"configuration_matches_approved": value["configuration_matches_approved"],
                "ioptions_matches_approved": value["ioptions_matches_approved"],
                "source": value["source"], "infrastructure_module_id": module_id}
    except (ValueError, TypeError, AttributeError, UnicodeError):
        raise Refusal("output_invalid") from None


def main(argv=None, input_stream=None, output_stream=None):
    output_stream = output_stream if output_stream is not None else sys.stdout
    result = {"artifact_head": ARTIFACT_HEAD, "cached_runtime_options_observed": False,
              "runtime_effective_proven": False, "application_readiness_observed": False}
    code = 2
    try:
        parser = Parser(description=__doc__)
        parser.add_argument("--artifact-dir", required=True, type=Path)
        parser.add_argument("--dotnet", type=Path)
        parser.add_argument("--verify-only", action="store_true")
        args = parser.parse_args(argv)
        verify_artifact(args.artifact_dir)
        if args.verify_only:
            result["status"], code = "artifact_ready", 0
        else:
            if args.dotnet is None or not args.dotnet.is_absolute() or not args.dotnet.is_file() or not os.access(args.dotnet, os.X_OK):
                raise Refusal("runtime_unavailable")
            source = input_stream if input_stream is not None else sys.stdin.buffer
            private = source.read(MAX_CAPTURE + 1)
            if len(private) > MAX_CAPTURE:
                raise Refusal("capture_too_large")
            validate_capture(private)
            status, raw = run_bounded([str(args.dotnet), str(args.artifact_dir.resolve() / "Duora.IntegrationTests.dll")], private, TIMEOUT)
            if status != 0:
                raise Refusal("probe_failed")
            result.update(sanitize_reply(raw))
            matches = result["configuration_matches_approved"] and result["ioptions_matches_approved"]
            result["status"], code = ("replay_verified", 0) if matches else ("replay_mismatch", 3)
    except Refusal as error:
        result.update(status="unknown", reason=error.reason)
    except (Exception, KeyboardInterrupt):
        result.update(status="unknown", reason="runner_failed")
    output_stream.write(json.dumps(result, separators=(",", ":")) + "\n")
    return code


if __name__ == "__main__":
    raise SystemExit(main())
