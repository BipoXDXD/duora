"""Read-only identity checks; no enrollment, Docker access or repair on import."""

import hashlib
import json
import re
from collections.abc import Callable

GUARD_VERSION = 1
BASELINE_NAME = "executor-runtime-baseline.json"
LEGACY_SQL_SHA256 = "d79a157b843cdc9b7deac21760b6f67218e1155b056f39922b450033beeec757"
LEGACY_SCHEMA_CEILING = "20261022143000_OriginalCleanup"
LEGACY_DIGESTS = {
    "amd64": "ghcr.io/bipoxdxd/duora-postgres@sha256:d32f8c4408e804bf8b57583c6fdfb8935380e6326bcc7ccb7397b1f386ada06c",
    "arm64": "ghcr.io/bipoxdxd/duora-postgres@sha256:a626e902dfc8165bf5b866ea03fd4b6c2eec739ec7b76872154504bee99dd930",
}


class GuardError(ValueError):
    """Refuse before worker shutdown or migration without echoing host metadata."""


def container_identity(metadata: dict) -> dict:
    try:
        state = metadata["State"]
        if (state["Status"] != "running" or state["Running"] is not True or state["Restarting"] is not False
                or state["Health"]["Status"] != "healthy" or type(metadata["RestartCount"]) is not int
                or metadata["RestartCount"] < 0):
            raise GuardError("Runtime is not stably healthy")
        _hex(metadata["Id"])
        _hex(metadata["Image"], prefix="sha256:")
        _timestamp(metadata["Created"])
        _timestamp(state["StartedAt"])
        if (not all(isinstance(metadata[k], dict) for k in ("Config", "HostConfig", "NetworkSettings"))
                or not isinstance(metadata["Mounts"], list)
                or not isinstance(metadata["NetworkSettings"]["Networks"], dict)):
            raise GuardError("Missing runtime configuration")
        fingerprint = {key: metadata[key] for key in ("Config", "HostConfig", "Mounts")}
        fingerprint["Networks"] = metadata["NetworkSettings"]["Networks"]
        return {"id": metadata["Id"], "createdAt": metadata["Created"], "startedAt": state["StartedAt"],
                "restartCount": metadata["RestartCount"], "imageId": metadata["Image"],
                "runtimeSha256": digest(fingerprint)}
    except (KeyError, TypeError, ValueError) as error:
        raise GuardError("Invalid or unhealthy container identity") from error


def volume_identity(metadata: dict) -> dict:
    try:
        _timestamp(metadata["CreatedAt"])
        for key in ("Name", "Driver", "Scope", "Mountpoint"):
            if not isinstance(metadata[key], str) or not metadata[key]:
                raise GuardError("Missing volume configuration")
        value = {key: metadata[key] for key in ("Name", "CreatedAt", "Driver", "Scope", "Mountpoint", "Options", "Labels")}
        return {"name": metadata["Name"], "createdAt": metadata["CreatedAt"], "runtimeSha256": digest(value)}
    except (KeyError, TypeError, ValueError) as error:
        raise GuardError("Invalid named volume identity") from error


def require_preserved(baseline: dict, observed: dict) -> None:
    _profile(baseline)
    _profile(observed)
    if baseline != observed:
        raise GuardError("Reviewed runtime baseline differs; administrative review required")


def digest(value: object) -> str:
    return hashlib.sha256(json.dumps(value, sort_keys=True, separators=(",", ":"), allow_nan=False).encode()).hexdigest()


def _hex(value: object, *, prefix: str = "") -> None:
    if not isinstance(value, str) or not re.fullmatch(re.escape(prefix) + r"[a-f0-9]{64}", value):
        raise GuardError("Invalid identity digest")


def _timestamp(value: object) -> None:
    if (not isinstance(value, str) or not re.fullmatch(r"20\d\d-\d\d-\d\dT[0-9:.]+(?:Z|[+-]\d\d:\d\d)", value)):
        raise GuardError("Invalid runtime timestamp")


def _profile(value: dict) -> None:
    try:
        if (set(value) != {"schema", "context", "services", "volumes"}
                or type(value["schema"]) is not int or value["schema"] != 1):
            raise GuardError("Unknown baseline schema")
        context = value["context"]
        if (set(context) != {"hostIdSha256", "project", "guardVersion", "dockerVersion", "dockerApi",
                            "composeVersion", "composeSha256", "configHashes"}
                or context["project"] != "duora" or type(context["guardVersion"]) is not int
                or context["guardVersion"] != GUARD_VERSION):
            raise GuardError("Unexpected baseline context")
        for key in ("hostIdSha256", "composeSha256"):
            _hex(context[key])
        for key in ("dockerVersion", "dockerApi", "composeVersion"):
            if not isinstance(context[key], str) or not re.fullmatch(r"[v0-9][0-9a-zA-Z.+-]{0,63}", context[key]):
                raise GuardError("Unknown runtime tool version")
        if set(context["configHashes"]) != {"postgres", "backup"}:
            raise GuardError("Unknown service configuration")
        for item in context["configHashes"].values():
            _hex(item)
        if set(value["services"]) != {"postgres", "backup"}:
            raise GuardError("Missing preserved service")
        for item in value["services"].values():
            if (set(item) != {"id", "imageId", "createdAt", "startedAt", "restartCount", "runtimeSha256"}
                    or type(item["restartCount"]) is not int or item["restartCount"] < 0):
                raise GuardError("Invalid preserved service")
            for key in ("id", "runtimeSha256"):
                _hex(item[key])
            _hex(item["imageId"], prefix="sha256:")
            _timestamp(item["createdAt"])
            _timestamp(item["startedAt"])
        if not isinstance(value["volumes"], list) or len(value["volumes"]) != 1:
            raise GuardError("Missing preserved data volume")
        item = value["volumes"][0]
        if set(item) != {"name", "createdAt", "runtimeSha256"} or item["name"] != "duora_pgdata":
            raise GuardError("Unknown data volume")
        _timestamp(item["createdAt"])
        _hex(item["runtimeSha256"])
    except (KeyError, TypeError, AttributeError) as error:
        raise GuardError("Invalid reviewed baseline") from error


def collect_runtime(compose: list[str], *, postgres_reference: str, compose_sha256: str,
                    host_id_sha256: str, capture: Callable[[list[str]], bytes]) -> dict:
    raise NotImplementedError("Read effective runtime, desired Compose hashes and sanitized audit without writes")


def require_contract(release: dict, *, schema_head: str, legacy_sql_sha256: str) -> None:
    raise NotImplementedError("Explicit bounded legacy-v1 and mandatory external-v2 contract")
