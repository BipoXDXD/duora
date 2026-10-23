"""Read-only identity checks; no enrollment, Docker access or repair on import."""

import hashlib
import json
import re
from collections.abc import Callable
from decimal import Decimal

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
                            "composeVersion", "composeSha256", "configHashes", "dockerServerSha256"}
                or context["project"] != "duora" or type(context["guardVersion"]) is not int
                or context["guardVersion"] != GUARD_VERSION):
            raise GuardError("Unexpected baseline context")
        for key in ("hostIdSha256", "composeSha256", "dockerServerSha256"):
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
    """A sanitized review candidate, not automatic enrollment or permission to install."""
    def read(command: list[str]) -> dict:
        return decode_json(capture(command))

    try:
        config = read(compose + ["config", "--format", "json"])
        if config["name"] != "duora" or config["volumes"]["pgdata"]["name"] != "duora_pgdata":
            raise GuardError("Unexpected Compose project or data volume")
        server = read(["docker", "version", "--format", "{{json .Server}}"])
        image = read(["docker", "image", "inspect", "--format", "{{json .}}", postgres_reference])
        _hex(image["Id"], prefix="sha256:")
        profile: dict = {"schema": 1, "context": {"hostIdSha256": host_id_sha256, "project": "duora",
            "guardVersion": GUARD_VERSION, "dockerVersion": server["Version"], "dockerApi": server["ApiVersion"],
            "dockerServerSha256": digest(server), "composeVersion": capture(["docker", "compose", "version", "--short"]).decode().strip(),
            "composeSha256": compose_sha256, "configHashes": {}}, "services": {}, "volumes": []}
        audit: dict = {"desired": {}, "effective": {}}
        for role in ("postgres", "backup"):
            line = capture(compose + ["config", "--hash", role]).decode("ascii").strip().split()
            if len(line) != 2 or line[0] != role:
                raise GuardError("Unknown Compose hash output")
            _hex(line[1])
            identifier = capture(compose + ["ps", "--all", "--quiet", role]).decode("ascii").strip()
            _hex(identifier)
            item = read(["docker", "container", "inspect", "--format", "{{json .}}", identifier])
            identity = container_identity(item)
            labels = item["Config"]["Labels"]
            wanted = config["services"][role]
            if (labels.get("com.docker.compose.project") != "duora"
                    or labels.get("com.docker.compose.service") != role
                    or labels.get("com.docker.compose.config-hash") != line[1]
                    or item["Image"] != image["Id"] or item["Config"]["Image"] != postgres_reference
                    or wanted["image"] != postgres_reference):
                raise GuardError("Runtime differs from the desired immutable component")
            mounts = [m for m in item["Mounts"] if m["Type"] == "volume"]
            if (len(mounts) != 1 or mounts[0]["Name"] != "duora_pgdata"
                    or mounts[0]["Destination"] != "/var/lib/postgresql"
                    or mounts[0]["RW"] is not (role == "postgres")):
                raise GuardError("Unexpected data volume mount")
            wanted_networks = {config["networks"][n]["name"] for n in wanted["networks"]}
            if set(item["NetworkSettings"]["Networks"]) != wanted_networks:
                raise GuardError("Effective networks differ from Compose")
            actual = item["HostConfig"]
            limits = wanted.get("deploy", {}).get("resources", {}).get("limits", {})
            expected = {"Memory": int(limits.get("memory", 0)), "PidsLimit": int(limits.get("pids", 0)),
                        "NanoCpus": int(Decimal(str(limits.get("cpus", "0"))) * 1_000_000_000),
                        "CpuShares": int(wanted.get("cpu_shares", 0)), "Privileged": wanted.get("privileged", False),
                        "ReadonlyRootfs": wanted.get("read_only", False)}
            for key, value in expected.items():
                if (actual.get(key) or 0) != value:
                    raise GuardError("Effective resource or isolation setting differs from Compose")
            for key, field in [("CapDrop", "cap_drop"), ("CapAdd", "cap_add"), ("SecurityOpt", "security_opt")]:
                normalize = lambda values: sorted(v.upper().removeprefix("CAP_").removesuffix(":TRUE") for v in values or [])
                if normalize(actual.get(key)) != normalize(wanted.get(field)):
                    raise GuardError("Effective capabilities or security options differ from Compose")
            if actual["RestartPolicy"]["Name"] != wanted.get("restart", "no"):
                raise GuardError("Effective restart policy differs from Compose")
            profile["context"]["configHashes"][role] = line[1]
            profile["services"][role] = identity
            # Immutable creation settings are bound by config-hash and the full fingerprint. Never expose Env.
            desired = {k: wanted.get(k) for k in ("image", "restart", "read_only", "privileged", "cap_drop", "cap_add",
                                                 "security_opt", "deploy", "cpu_shares", "volumes", "networks")}
            desired["environmentSha256"] = digest(wanted.get("environment", {}))
            audit["desired"][role] = desired
            audit["effective"][role] = {"image": item["Config"]["Image"], "user": item["Config"].get("User"),
                "hostConfig": {k: actual.get(k) for k in ("Memory", "MemorySwap", "MemoryReservation", "NanoCpus",
                "CpuShares", "CpuPeriod", "CpuQuota", "CpusetCpus", "PidsLimit", "Privileged", "ReadonlyRootfs",
                "CapDrop", "CapAdd", "SecurityOpt", "RestartPolicy", "NetworkMode", "ShmSize", "IpcMode")},
                "mounts": item["Mounts"], "networks": item["NetworkSettings"]["Networks"],
                "environmentSha256": digest(item["Config"].get("Env", []))}
        volume = read(["docker", "volume", "inspect", "--format", "{{json .}}", "duora_pgdata"])
        profile["volumes"] = [volume_identity(volume)]
        audit["volume"] = {key: volume.get(key) for key in ("Name", "CreatedAt", "Driver", "Scope", "Mountpoint")}
        audit["volume"]["optionsSha256"] = digest(volume.get("Options"))
        audit["volume"]["labelsSha256"] = digest(volume.get("Labels"))
        _profile(profile)
        result = {"profile": profile, "audit": audit}
        return {**result, "inventorySha256": digest(result)}
    except (KeyError, TypeError, ValueError, UnicodeError, ArithmeticError) as error:
        raise GuardError("Cannot establish the reviewed healthy runtime") from error


def require_contract(release: dict, *, schema_head: str, legacy_sql_sha256: str) -> None:
    if schema_head and not re.fullmatch(r"\d{14}_[A-Za-z0-9_]+", schema_head):
        raise GuardError("Unknown database schema identity")
    version = release.get("contractVersion")
    if version == "v2":
        return  # External payload authentication is mandatory in runtime_preflight, never a missing-file fallback.
    if version != "v1":
        raise GuardError("Unknown deployment contract")
    try:
        if (release["images"]["postgres"] != LEGACY_DIGESTS[release["architecture"]]
                or legacy_sql_sha256 != LEGACY_SQL_SHA256
                or schema_head > LEGACY_SCHEMA_CEILING or release["schema"]["max"] > LEGACY_SCHEMA_CEILING):
            raise GuardError("Release exceeds the explicitly qualified legacy-v1 profile")
    except (KeyError, TypeError) as error:
        raise GuardError("Missing qualified legacy-v1 identity") from error


def decode_json(data: bytes) -> dict:
    if len(data) > 256 * 1024:
        raise GuardError("Metadata budget exceeded")
    def pairs(items: list[tuple[str, object]]) -> dict:
        value = {}
        for key, item in items:
            if key in value:
                raise GuardError("Duplicate metadata field")
            value[key] = item
        return value
    try:
        value = json.loads(data.decode("utf-8"), object_pairs_hook=pairs)
        if not isinstance(value, dict):
            raise GuardError("Metadata must be an object")
        return value
    except (ValueError, UnicodeError, RecursionError) as error:
        raise GuardError("Invalid runtime metadata") from error
