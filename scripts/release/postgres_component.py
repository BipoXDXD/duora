"""Select a reviewed PostgreSQL runtime without rebuilding it during application releases."""
from pathlib import Path
from typing import Any


def fingerprint_inputs(root: Path) -> str:
    raise NotImplementedError("PostgreSQL component input fingerprint")


def read_lock(path: Path) -> dict[str, Any]:
    raise NotImplementedError("Strict PostgreSQL component lock")


def select_image(root: Path, lock: dict[str, Any], architecture: str) -> str:
    raise NotImplementedError("Reviewed immutable PostgreSQL component selection")
