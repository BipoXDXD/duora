"""Single static executor installer; apply never invokes product deployment."""
from collections.abc import Callable
from dataclasses import dataclass
from pathlib import Path

FILES = ("duora_deploy.py", "reconcile_payload.py", "runtime_guard.py", "install_executor.py")


class InstallError(ValueError):
    pass


@dataclass(frozen=True)
class InstallPaths:
    entry: Path
    versions: Path
    preimages: Path
    state: Path
    ingress: Path


def validate_bundle(source: Path, *, expected_commit: str, manifest_sha256: str) -> dict:
    raise NotImplementedError("Closed immutable bundle identity and source hashes")


def install_bundle(source: Path, *, expected_commit: str, manifest_sha256: str,
                   reviewed_inventory_sha256: str, expected_executor_sha256: str,
                   paths: InstallPaths, inspect_runtime: Callable[[], dict],
                   require_idle: Callable[[], None], apply: bool = False) -> dict:
    raise NotImplementedError("Lock-ordered static preflight, atomic activation and own-file recovery")
