"""Disposable-qualification wrapper around the installed executor's real main(); never a production entry point.

The argument parser, root check, the three locks and the whole swap protocol are the installed bundle's own code. Only
what a laboratory cannot satisfy is injected, each one explicitly: architecture (arm64 rounds), a clock inside the
window, the host image prefix (loopback registry) and the receiver gate, which keeps the real pending scan but skips
the Coolify API. Optional fault injection kills this process with SIGKILL while a child outlives it.
"""
import argparse
import dataclasses
from datetime import datetime, timezone
import importlib.machinery
import importlib.util
import json
import os
from pathlib import Path
import signal
import subprocess
import sys
import time
from typing import Any

WINDOW = datetime(2026, 11, 10, 12, 0, tzinfo=timezone.utc)
KILL_CHILD_GRACE_SECONDS = 0.5
KILL_COMMIT_ARTIFACT = 2


def load(path: Path, name: str):
    loader = importlib.machinery.SourceFileLoader(name, str(path))
    spec = importlib.util.spec_from_loader(name, loader)
    assert spec is not None
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    loader.exec_module(module)
    return module


def current_phase(state: Path) -> str | None:
    try:
        return json.loads((state / "postgres-swap/phase.json").read_text())["phase"]
    except (OSError, ValueError, KeyError):
        return None


def arm_kill(swap, state: Path, phase: str) -> None:
    """A child in its own session, without PDEATHSIG, survives the parent: the orphan the design worries about."""
    original_run, original_write = swap.run_in_own_group, swap._swap_write_atomic
    written = 0

    def run(command, *, capture=False, input_text=None, timeout_seconds=None):
        if current_phase(state) != phase:
            return original_run(command, capture=capture, input_text=input_text, timeout_seconds=timeout_seconds)
        subprocess.Popen(command, start_new_session=True, stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL,
                         stderr=subprocess.DEVNULL, env={"PATH": "/usr/bin:/bin", "LC_ALL": "C"})
        time.sleep(KILL_CHILD_GRACE_SECONDS)
        os.kill(os.getpid(), signal.SIGKILL)

    def write(path, raw, **arguments):
        nonlocal written
        if phase == "commit" and path.parent.name != "postgres-swap" and current_phase(state) == "commit":
            written += 1
            if written > KILL_COMMIT_ARTIFACT:
                os.kill(os.getpid(), signal.SIGKILL)
        return original_write(path, raw, **arguments)

    swap.run_in_own_group, swap._swap_write_atomic = run, write


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--entry", type=Path, required=True, help="installed executor entry point")
    parser.add_argument("--receiver", type=Path, required=True)
    parser.add_argument("--ingress", type=Path, required=True)
    parser.add_argument("--architecture", required=True)
    parser.add_argument("--image-prefix", default="")
    parser.add_argument("--state", type=Path, required=True)
    parser.add_argument("--kill-at-phase")
    arguments, executor_arguments = parser.parse_known_args()
    sys.dont_write_bytecode = True
    sys.path.insert(0, str(arguments.entry.resolve().parent))
    module: Any = load(arguments.entry, "duora_swap_installed_executor")
    receiver = load(arguments.receiver, "duora_swap_receiver")
    setattr(receiver, "STATE", arguments.ingress)
    production_host, parse_host = module._production_swap_host, module.parse_host_config

    def host(prefix: str, paths, check_host):
        return dataclasses.replace(production_host(prefix, paths, check_host), architecture=arguments.architecture, now=lambda: WINDOW,
                                   require_ingress_idle=lambda _uuid: receiver.require_host_idle())

    module._production_swap_host = host
    module.parse_host_config = lambda text: dataclasses.replace(parse_host(text), image_prefix=arguments.image_prefix)
    if arguments.kill_at_phase:
        arm_kill(sys.modules["postgres_swap"], arguments.state, arguments.kill_at_phase)
    return module.main(executor_arguments)


if __name__ == "__main__":
    sys.exit(main())
