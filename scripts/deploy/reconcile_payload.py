"""Bounded, authenticated SQL extraction; importing this module never touches Docker."""

import hashlib
import io
import json
import os
import re
import secrets
import selectors
import signal
import subprocess
import tarfile
import time
from collections.abc import Callable

MAX_CAPTURE_BYTES = 128 * 1024
MAX_SQL_BYTES = 64 * 1024
MAX_METADATA_BYTES = 4 * 1024
SQL_NAME = "reconcile-privileges.sql"
CHECKSUM_NAME = SQL_NAME + ".sha256"


class PayloadError(ValueError):
    """Refuse a release before worker shutdown or migrations."""


def capture_binary(command: list[str], *, max_bytes: int = MAX_CAPTURE_BYTES,
                   timeout_seconds: float = 30) -> bytes:
    """Drain both pipes within one budget, then observe exit; never decode the TAR."""
    if not 0 < max_bytes <= MAX_CAPTURE_BYTES or not 0 < timeout_seconds <= 30:
        raise PayloadError("Invalid capture limits")
    process = None
    complete = False
    selector = selectors.DefaultSelector()
    try:
        deadline = time.monotonic() + timeout_seconds
        process = subprocess.Popen(command, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE,
                                   stderr=subprocess.PIPE, bufsize=0, start_new_session=True,
                                   env={"PATH": "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin",
                                        "LC_ALL": "C", "HOME": "/root"})
        for pipe in (process.stdout, process.stderr):
            assert pipe is not None
            os.set_blocking(pipe.fileno(), False)
            selector.register(pipe, selectors.EVENT_READ)
        output = bytearray()
        consumed = 0
        while selector.get_map():
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise PayloadError("SQL capture deadline exceeded")
            for key, _ in selector.select(remaining):
                try:
                    chunk = os.read(key.fd, min(8192, max_bytes - consumed + 1))
                except BlockingIOError:
                    continue
                if not chunk:
                    selector.unregister(key.fileobj)
                    continue
                consumed += len(chunk)
                if consumed > max_bytes:
                    raise PayloadError("SQL capture budget exceeded")
                if key.fileobj is process.stdout:
                    output.extend(chunk)
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise PayloadError("SQL capture deadline exceeded")
        if process.wait(timeout=remaining) != 0:
            raise PayloadError("SQL capture command failed")
        complete = True
        return bytes(output)
    except (OSError, subprocess.SubprocessError) as error:
        raise PayloadError("SQL capture failed") from error
    finally:
        selector.close()
        if process is not None:
            if not complete:
                # The group may still own pipes even after the direct child exits.
                for sig in (signal.SIGTERM, signal.SIGKILL):
                    try:
                        os.killpg(process.pid, sig)
                    except ProcessLookupError:
                        pass
                    if sig == signal.SIGTERM:
                        try:
                            process.wait(timeout=0.5)
                        except subprocess.TimeoutExpired:
                            pass
                try:
                    process.wait(timeout=1)
                except subprocess.TimeoutExpired as error:
                    raise PayloadError("SQL capture process could not be reaped") from error
            for pipe in (process.stdout, process.stderr):
                if pipe is not None:
                    pipe.close()


def parse_regular_archive(data: bytes, *, expected_name: str, max_payload_bytes: int) -> bytes:
    """One bounded regular USTAR file, without extraction or extension records."""
    if (not 0 < max_payload_bytes <= MAX_SQL_BYTES or len(data) > MAX_CAPTURE_BYTES
            or len(data) < 1536 or len(data) % 512):
        raise PayloadError("Invalid SQL archive budget or framing")
    header = data[:512]
    # Check raw type/size BEFORE tarfile can consume extension metadata.
    size_field = header[124:136].rstrip(b"\0 ").lstrip(b" ")
    if header[156:157] not in (b"0", b"\0") or not re.fullmatch(rb"[0-7]{1,12}", size_field):
        raise PayloadError("SQL archive must contain a regular bounded file")
    size = int(size_field, 8)
    end = 512 + ((size + 511) // 512) * 512
    if size > max_payload_bytes or end + 1024 > len(data) or any(data[end:]):
        raise PayloadError("SQL archive has excess, truncated or trailing content")
    try:
        with tarfile.open(fileobj=io.BytesIO(data), mode="r:") as source:
            member = source.next()
            if (member is None or member.name != expected_name or not member.isreg()
                    or member.size != size or member.linkname or member.pax_headers
                    or member.sparse is not None or member.offset != 0):
                raise PayloadError("Unexpected SQL archive member")
            stream = source.extractfile(member)
            if stream is None:
                raise PayloadError("Missing SQL archive body")
            with stream:
                body = stream.read(max_payload_bytes + 1)
            if len(body) != size or source.next() is not None:
                raise PayloadError("Unexpected SQL archive contents")
            return body
    except (tarfile.TarError, OSError, ValueError) as error:
        raise PayloadError("Invalid SQL archive") from error


def verify_sql(sql_archive: bytes, checksum_archive: bytes) -> str:
    """Integrity within an already authenticated immutable migrator image."""
    sql = parse_regular_archive(sql_archive, expected_name=SQL_NAME, max_payload_bytes=MAX_SQL_BYTES)
    checksum = parse_regular_archive(checksum_archive, expected_name=CHECKSUM_NAME,
                                     max_payload_bytes=MAX_METADATA_BYTES)
    expected = hashlib.sha256(sql).hexdigest().encode("ascii") + b"  " + SQL_NAME.encode("ascii") + b"\n"
    if not sql or b"\0" in sql or checksum != expected:
        raise PayloadError("Invalid SQL checksum or body")
    try:
        return sql.decode("utf-8")
    except UnicodeDecodeError as error:
        raise PayloadError("SQL must be UTF-8") from error


def extract_sql(image_reference: str, *, revision: str, architecture: str,
                capture: Callable[[list[str]], bytes] = capture_binary) -> str:
    """Copy authenticated release files without starting the image or creating volumes."""
    if (not re.fullmatch(r"ghcr\.io/bipoxdxd/duora-migrator@sha256:[a-f0-9]{64}", image_reference)
            or not re.fullmatch(r"[a-f0-9]{40}", revision) or architecture not in ("amd64", "arm64")):
        raise PayloadError("Invalid migrator release identity")

    def pairs(items: list[tuple[str, object]]) -> dict:
        result = {}
        for key, value in items:
            if key in result:
                raise PayloadError("Duplicate daemon metadata field")
            result[key] = value
        return result

    def inspect(kind: str, reference: str) -> dict:
        raw = capture(["docker", kind, "inspect", "--format", "{{json .}}", reference])
        if len(raw) > MAX_CAPTURE_BYTES:
            raise PayloadError("Daemon metadata budget exceeded")
        try:
            value = json.loads(raw.decode("utf-8"), object_pairs_hook=pairs)
        except (ValueError, UnicodeError, RecursionError) as error:
            raise PayloadError("Invalid daemon metadata") from error
        if not isinstance(value, dict):
            raise PayloadError("Daemon metadata must be an object")
        return value

    image = inspect("image", image_reference)
    config = image.get("Config")
    if not isinstance(config, dict):
        raise PayloadError("Missing migrator configuration")
    volumes = config.get("Volumes")
    # Docker creates declared anonymous volumes even for a never-started container.
    if volumes is not None and (not isinstance(volumes, dict) or volumes):
        raise PayloadError("Migrator declares volumes")
    labels = config.get("Labels")
    image_id = image.get("Id")
    digests = image.get("RepoDigests")
    if (not isinstance(image_id, str) or not re.fullmatch(r"sha256:[a-f0-9]{64}", image_id)
            or not isinstance(digests, list) or image_reference not in digests
            or image.get("Os") != "linux" or image.get("Architecture") != architecture
            or not isinstance(labels, dict)
            or labels.get("org.opencontainers.image.revision") != revision
            or labels.get("org.opencontainers.image.source") != "https://github.com/BipoXDXD/duora"
            or labels.get("org.opencontainers.image.title") != "duora-migrator"):
        raise PayloadError("Migrator image differs from the authenticated release")

    nonce = secrets.token_hex(16)
    name = "duora-reconcile-" + nonce
    owner_key = "duora.reconcile-nonce"
    container_id = None

    def owned_container() -> str:
        item = inspect("container", name)
        identifier = item.get("Id")
        item_config = item.get("Config")
        state = item.get("State")
        host = item.get("HostConfig")
        if (not isinstance(identifier, str) or not re.fullmatch(r"[a-f0-9]{64}", identifier)
                or (container_id is not None and identifier != container_id)
                or item.get("Name") != "/" + name or item.get("Image") != image_id
                or not isinstance(item_config, dict) or not isinstance(item_config.get("Labels"), dict)
                or item_config["Labels"].get(owner_key) != nonce
                or not isinstance(state, dict) or state.get("Running") is not False
                or state.get("Status") != "created" or item.get("Mounts") != []
                or not isinstance(host, dict) or host.get("NetworkMode") != "none"):
            raise PayloadError("Refusing a foreign or non-isolated extraction container")
        return identifier

    try:
        # An ambiguous create failure still reaches ownership-checked cleanup by nonce.
        created = capture(["docker", "create", "--name", name, "--label", owner_key + "=" + nonce,
                           "--network", "none", "--read-only", "--cap-drop", "ALL",
                           "--entrypoint", "/bin/false", image_reference])
        try:
            candidate_id = created.decode("ascii").strip()
        except UnicodeError as error:
            raise PayloadError("Invalid extraction container identity") from error
        if not re.fullmatch(r"[a-f0-9]{64}", candidate_id):
            raise PayloadError("Invalid extraction container identity")
        container_id = candidate_id
        owned_container()
        sql = capture(["docker", "cp", container_id + ":/app/" + SQL_NAME, "-"])
        checksum = capture(["docker", "cp", container_id + ":/app/" + CHECKSUM_NAME, "-"])
        return verify_sql(sql, checksum)
    finally:
        # Never force-remove, prune, or touch a different/running container.
        identifier = owned_container()
        capture(["docker", "rm", identifier])
