#!/usr/bin/env python3
"""Root-owned, project-bound release ingress. Receives OCI data, never commands."""

import fcntl
import hashlib
import http.client
import ipaddress
import json
import os
import re
import shutil
import signal
import socket
import sys
import tarfile
import tempfile
import time
import traceback
import urllib.parse
from pathlib import Path
from typing import Any

PROJECTS = {
    "stillon": {"app": "ghcr.io/bipoxdxd/stillon"},
    "goblinshift": {
        "app": "ghcr.io/bipoxdxd/goblinshift",
        "web": "ghcr.io/bipoxdxd/goblinshift-web",
        "executor": "ghcr.io/bipoxdxd/goblinshift-executor",
    },
    "duora": {
        role: "ghcr.io/bipoxdxd/duora-" + role
        for role in ["web", "worker", "migrator", "edge", "postgres"]
    },
}
HEX = r"[0-9a-f]{64}"
DIGEST = re.compile("sha256:" + HEX)
SAFE_ENV = {
    "PATH": "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin",
    "LC_ALL": "C",
    "HOME": "/root",
}
BASE = Path("/etc/coolify-release")
STATE = Path("/var/lib/coolify-release")
LIMIT = 8 * 1024**3


def json_unique(text: Any) -> Any:
    def pairs(items: Any) -> Any:
        result = {}
        for key, value in items:
            if key in result:
                raise ValueError("Duplicate JSON key")
            result[key] = value
        return result

    return json.loads(text, object_pairs_hook=pairs)


def parse_request(value: Any) -> Any:
    if not re.fullmatch(r"release [0-9a-f]{40}", value):
        raise ValueError("Only release <full SHA> is allowed")
    return value.split(" ")[1]


def validate_release(data: Any, project: Any, commit: Any) -> Any:
    fields = {"version", "project", "commit", "images"} | (
        {"duora"} if project == "duora" else set()
    )
    if (
        not isinstance(data, dict)
        or set(data) != fields
        or type(data["version"]) is not int
        or data["version"] != 1
    ):
        raise ValueError("Unexpected release fields")
    if data["project"] != project or data["commit"] != commit:
        raise ValueError("Release identity mismatch")
    if not isinstance(data["images"], dict) or set(data["images"]) != set(PROJECTS[project]):
        raise ValueError("Unexpected image roles")
    for role, reference in data["images"].items():
        if not isinstance(reference, str) or not re.fullmatch(
            re.escape(PROJECTS[project][role]) + r"@sha256:" + HEX, reference
        ):
            raise ValueError("Image must be an immutable allowed repository digest")
    if project == "duora":
        manifest = data["duora"]
        if (
            not isinstance(manifest, dict)
            or manifest.get("commit") != commit
            or manifest.get("images") != data["images"]
            or manifest.get("architecture") != "amd64"
        ):
            raise ValueError("Duora manifest differs from transported release")
    return data


def unpack(stream: Any, destination: Any, roles: Any, limit: Any = LIMIT) -> Any:
    seen = set()
    total = 0
    names = "|".join(re.escape(role) for role in roles)
    file_pattern = re.compile(
        r"(release\.json|(?:" + names + r")/(?:oci-layout|index\.json|blobs/sha256/" + HEX + r"))"
    )
    directory_pattern = re.compile(r"(?:" + names + r")(?:/blobs(?:/sha256)?)?")
    with tarfile.open(fileobj=stream, mode="r|") as archive:
        for member in archive:
            name = member.name.removeprefix("./").rstrip("/")
            if member.isdir() and (name in {"", "."} or directory_pattern.fullmatch(name)):
                continue
            if not member.isfile() or not file_pattern.fullmatch(name) or name in seen:
                raise ValueError("Archive path, type or duplicate refused")
            seen.add(name)
            total += member.size
            if (
                total > limit
                or member.size < 0
                or (name == "release.json" and member.size > 131072)
            ):
                raise ValueError("Release exceeds size limit")
            path = destination / name
            path.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
            source = archive.extractfile(member)
            if source is None:
                raise ValueError("Missing archive data")
            with path.open("xb") as target:
                shutil.copyfileobj(source, target, 1024 * 1024)


def validate_oci(root: Any, digest: Any) -> Any:
    if not DIGEST.fullmatch(digest):
        raise ValueError("Invalid image digest")
    if json_unique((root / "oci-layout").read_bytes()) != {"imageLayoutVersion": "1.0.0"}:
        raise ValueError("Unexpected OCI layout")
    index = json_unique((root / "index.json").read_bytes())
    if (
        not isinstance(index, dict)
        or set(index) - {"schemaVersion", "mediaType", "manifests"}
        or index.get("schemaVersion") != 2
    ):
        raise ValueError("Unexpected OCI index")
    descriptors = index.get("manifests")
    if not isinstance(descriptors, list) or len(descriptors) != 1:
        raise ValueError("Exactly one image required")
    desc = descriptors[0]
    if (
        not isinstance(desc, dict)
        or set(desc) - {"mediaType", "digest", "size", "platform"}
        or desc.get("digest") != digest
    ):
        raise ValueError("Named or mismatched image refused")
    if "platform" in desc and desc["platform"] != {"os": "linux", "architecture": "amd64"}:
        raise ValueError("Unexpected platform")
    for blob in (root / "blobs/sha256").iterdir():
        hasher = hashlib.sha256()
        with blob.open("rb") as source:
            for chunk in iter(lambda: source.read(1024 * 1024), b""):
                hasher.update(chunk)
        if hasher.hexdigest() != blob.name:
            raise ValueError("OCI blob checksum mismatch")

    def read_descriptor(item: Any) -> Any:
        if (
            not isinstance(item, dict)
            or not isinstance(item.get("digest"), str)
            or not DIGEST.fullmatch(item["digest"])
        ):
            raise ValueError("Invalid blob descriptor")
        path = root / "blobs/sha256" / item["digest"][7:]
        if path.stat().st_size != item.get("size"):
            raise ValueError("Blob size mismatch")
        return path

    document = json_unique(read_descriptor(desc).read_bytes())
    config = json_unique(read_descriptor(document["config"]).read_bytes())
    if config.get("os") != "linux" or config.get("architecture") != "amd64":
        raise ValueError("Only linux/amd64 images accepted")
    if not isinstance(document.get("layers"), list):
        raise ValueError("Invalid image layers")
    for layer in document["layers"]:
        read_descriptor(layer)


def deployment_finished(record: Any) -> Any:
    state = record.get("status")
    if state == "finished":
        return True
    if state in {"queued", "in_progress"}:
        return False
    raise ValueError("Coolify deployment failed or was cancelled")


def validate_runtime(record: Any, uuid: Any, digest: Any = None) -> Any:
    if (
        record["State"]["Status"] != "running"
        or record["State"].get("Health", {}).get("Status", "healthy") != "healthy"
    ):
        raise ValueError("Application container is not healthy")
    if uuid is not None and record["Config"]["Labels"].get("coolify.applicationUuid") != uuid:
        raise ValueError("Unexpected Coolify ownership")
    if digest is not None and record["Image"] != digest:
        raise ValueError("Running release image differs")


class DockerConnection(http.client.HTTPConnection):
    def __init__(self) -> None:
        super().__init__("localhost", timeout=900)

    def connect(self) -> None:
        self.sock = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        self.sock.settimeout(self.timeout)
        self.sock.connect("/var/run/docker.sock")


def docker_request(method: Any, path: Any, body: Any = None, headers: Any = None) -> Any:
    connection = DockerConnection()
    try:
        connection.request(method, "/v1.52" + path, body=body, headers=headers or {})
        response = connection.getresponse()
        data = response.read()
        if response.status not in {200, 201, 204}:
            raise ValueError("Docker operation failed")
        return data
    finally:
        connection.close()


def command(argv: Any) -> Any:
    # Finite Docker API operations, no shell, external executable or caller-supplied endpoint.
    if argv[:3] == ["/usr/bin/docker", "load", "-i"] and len(argv) == 4:
        path = Path(argv[3])
        with path.open("rb") as archive:
            data = docker_request(
                "POST",
                "/images/load?quiet=1",
                archive,
                {"Content-Type": "application/x-tar", "Content-Length": str(path.stat().st_size)},
            )
        for line in data.splitlines():
            if json_unique(line).get("error"):
                raise ValueError("Docker rejected OCI image")
        return data
    if argv[:3] == ["/usr/bin/docker", "image", "inspect"] and len(argv) == 4:
        return (
            b"["
            + docker_request("GET", "/images/" + urllib.parse.quote(argv[3], safe="") + "/json")
            + b"]"
        )
    if argv[:2] == ["/usr/bin/docker", "inspect"] and len(argv) == 3:
        return (
            b"["
            + docker_request("GET", "/containers/" + urllib.parse.quote(argv[2], safe="") + "/json")
            + b"]"
        )
    if argv[:2] == ["/usr/bin/docker", "tag"] and len(argv) == 4:
        repository, _, tag = argv[3].rpartition(":")
        query = urllib.parse.urlencode({"repo": repository, "tag": tag})
        return docker_request(
            "POST", "/images/" + urllib.parse.quote(argv[2], safe="") + "/tag?" + query
        )
    raise ValueError("Docker operation outside release contract")


def api(path: Any, method: Any = "GET", payload: Any = None) -> Any:
    token = (BASE / "api-token").read_text().strip()
    connection = http.client.HTTPConnection("127.0.0.1", 8000, timeout=60)
    try:
        connection.request(
            method,
            "/api/v1/" + path,
            body=None if payload is None else json.dumps(payload).encode(),
            headers={
                "Authorization": "Bearer " + token,
                "Accept": "application/json",
                "Content-Type": "application/json",
            },
        )
        response = connection.getresponse()
        data = response.read()
        if response.status not in {200, 201}:
            raise ValueError("Coolify API operation failed")
        return json_unique(data)
    finally:
        connection.close()


def atomic_json(path: Any, data: Any) -> Any:
    temporary = path.with_suffix(".next")
    with temporary.open("w") as handle:
        os.chmod(temporary, 0o600)
        json.dump(data, handle, indent=2)
        handle.write("\n")
        handle.flush()
        os.fsync(handle.fileno())
    temporary.replace(path)


def import_images(release: Any, stage: Any) -> Any:
    imported = {}
    for role, reference in release["images"].items():
        digest = reference.split("@")[1]
        root = stage / role
        validate_oci(root, digest)
    # Every role validates before any Docker mutation. Repack only checked regular files.
    for role, reference in release["images"].items():
        digest = reference.split("@")[1]
        root = stage / role
        bundle = stage / (role + ".tar")
        with tarfile.open(bundle, "w") as archive:
            for path in sorted(root.rglob("*")):
                if path.is_file():
                    archive.add(path, arcname=path.relative_to(root), recursive=False)
        command(["/usr/bin/docker", "load", "-i", str(bundle)])
        actual = json.loads(command(["/usr/bin/docker", "image", "inspect", digest]))[0]
        if actual["Id"] != digest or actual["Architecture"] != "amd64" or actual["Os"] != "linux":
            raise ValueError("Imported image identity differs")
        tag = f"{PROJECTS[release['project']][role]}:release-{release['commit']}"
        command(["/usr/bin/docker", "tag", digest, tag])
        imported[role] = tag + "@" + digest
    return imported


def update_envs(uuid: Any, values: Any) -> Any:
    api(
        "applications/" + uuid + "/envs/bulk",
        "PATCH",
        {
            "data": [
                {
                    "key": key,
                    "value": value,
                    "is_literal": True,
                    "is_preview": False,
                    "is_shown_once": True,
                }
                for key, value in values.items()
            ]
        },
    )


def configure_duora(release: Any, config: Any) -> Any:
    # The existing reviewed executor checks schema, backups, WAL and readiness.
    manifest = release["duora"]
    module_path = Path("/usr/local/sbin/duora-deploy")
    import importlib.util

    loader = importlib.machinery.SourceFileLoader("duora_manifest", str(module_path))
    spec = importlib.util.spec_from_loader(loader.name, loader)
    if spec is None:
        raise ValueError("Cannot load reviewed manifest parser")
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    loader.exec_module(module)
    module.parse_manifest(json.dumps(manifest), image_prefix="ghcr.io/bipoxdxd/duora-")
    pinned = Path("/etc/duora/coolify-deploy.json")
    current = json_unique(pinned.read_bytes())
    path = Path("/opt/duora/releases") / (manifest["releaseId"] + ".json")
    if path.exists() and json_unique(path.read_bytes()) != manifest:
        raise ValueError("Release ID already used")
    atomic_json(path, manifest)
    current["manifest"] = str(path)
    current["manifest_sha256"] = hashlib.sha256(path.read_bytes()).hexdigest()
    current["observer_environment"]["DUORA_OBSERVER_IMAGE"] = manifest["images"]["edge"]
    # Git config remains frozen, so all other observer metadata stays pinned.
    atomic_json(pinned, current)
    update_envs(config["uuid"], {"DUORA_OBSERVER_IMAGE": manifest["images"]["edge"]})


def deploy(release: Any, imported: Any, config: Any, state: Any) -> Any:
    project = release["project"]
    uuid = config["uuid"]
    values = {}
    if project == "stillon":
        values = {"STILLON_IMAGE": imported["app"]}
    if project == "goblinshift":
        values = {
            "GS_APP_IMAGE": imported["app"],
            "GS_WEB_IMAGE": imported["web"],
            "GS_EXECUTOR_IMAGE": release["images"]["executor"],
        }
        whitelist = Path("/srv/goblinshift/executor/images.json")
        previous = json_unique(whitelist.read_bytes())["allowed"]
        atomic_json(
            whitelist, {"allowed": list(dict.fromkeys([release["images"]["executor"], *previous]))}
        )
    if project == "duora":
        configure_duora(release, config)
    else:
        update_envs(uuid, values)
    result = api("applications/" + uuid + "/start", "POST")
    deployment = result["deployment_uuid"]
    atomic_json(state / "pending.json", {"release": release, "deployment_uuid": deployment})
    deadline = time.monotonic() + 1200
    while time.monotonic() < deadline:
        if deployment_finished(api("deployments/" + deployment)):
            break
        time.sleep(5)
    else:
        raise ValueError("Coolify timed out; inspect deployment before retry")
    for name in config["containers"]:
        record = json.loads(command(["/usr/bin/docker", "inspect", name]))[0]
        role = config["image_roles"].get(name)
        digest = release["images"][role].split("@")[1] if role else None
        validate_runtime(record, uuid if project != "duora" else None, digest)
    for probe in config["readiness"]:
        record = json.loads(command(["/usr/bin/docker", "inspect", probe["container"]]))[0]
        address = record["NetworkSettings"]["Networks"][probe["network"]]["IPAddress"]
        address = str(ipaddress.ip_address(address))
        connection = http.client.HTTPConnection(address, probe["port"], timeout=30)
        try:
            connection.request("GET", probe["path"])
            response = connection.getresponse()
            response.read()
            if response.status != 200:
                raise ValueError("Application readiness failed")
        finally:
            connection.close()
    if project == "stillon":
        command(["/usr/bin/docker", "tag", imported["app"], "stillon:" + release["commit"]])
        (Path("/opt/stillon") / "versao").write_text(release["commit"] + "\n")
    atomic_json(
        state / "current.json",
        {"release": release, "deployment_uuid": deployment, "finished_at": time.time()},
    )
    (state / "pending.json").unlink()
    return deployment


def main() -> None:
    if os.geteuid() != 0 or len(sys.argv) != 2 or sys.argv[1] not in PROJECTS:
        raise ValueError("Project-bound root entry required")
    project = sys.argv[1]
    sha = parse_request(os.environ.get("SSH_ORIGINAL_COMMAND", ""))
    os.umask(0o077)
    signal.signal(signal.SIGHUP, signal.SIG_IGN)
    state = STATE / project
    state.mkdir(parents=True, exist_ok=True, mode=0o700)
    with (state / "release.lock").open("a") as lock:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        if shutil.disk_usage(state).free < 20 * 1024**3:
            raise ValueError("Insufficient free disk for safe release")
        with tempfile.TemporaryDirectory(prefix="incoming-", dir=state) as temporary:
            stage = Path(temporary)
            unpack(sys.stdin.buffer, stage, set(PROJECTS[project]))
            release = validate_release(
                json_unique((stage / "release.json").read_bytes()), project, sha
            )
            imported = import_images(release, stage)
            config = json_unique((BASE / (project + ".json")).read_bytes())
            deployment = deploy(release, imported, config, state)
            print(
                json.dumps(
                    {
                        "project": project,
                        "commit": sha,
                        "deployment_uuid": deployment,
                        "status": "healthy",
                    }
                )
            )


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        # Detailed diagnosis stays on the root side; CI receives a fixed message.
        descriptor = os.open(
            "/var/log/coolify-release-errors.log", os.O_WRONLY | os.O_APPEND | os.O_CREAT, 0o600
        )
        with os.fdopen(descriptor, "a") as error_log:
            traceback.print_exc(file=error_log)
        # No API body, private env, command stderr or app logs reach CI.
        print(
            "Release refused or failed: "
            + type(error).__name__
            + "; inspect root-owned deployment state.",
            file=sys.stderr,
        )
        sys.exit(1)
