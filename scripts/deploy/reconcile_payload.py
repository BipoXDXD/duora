"""Bounded, authenticated SQL extraction; importing this module never touches Docker."""

MAX_CAPTURE_BYTES = 128 * 1024
MAX_SQL_BYTES = 64 * 1024
MAX_METADATA_BYTES = 4 * 1024
SQL_NAME = "reconcile-privileges.sql"
CHECKSUM_NAME = SQL_NAME + ".sha256"


class PayloadError(ValueError):
    """Refuse a release before worker shutdown or migrations."""


def capture_binary(command: list[str], *, max_bytes: int = MAX_CAPTURE_BYTES,
                   timeout_seconds: float = 30) -> bytes:
    raise NotImplementedError("Bounded binary capture with observed process termination")


def parse_regular_archive(data: bytes, *, expected_name: str, max_payload_bytes: int) -> bytes:
    raise NotImplementedError("Closed regular-file SQL archive")


def verify_sql(sql_archive: bytes, checksum_archive: bytes) -> str:
    raise NotImplementedError("Authenticated canonical reconciliation SQL")
