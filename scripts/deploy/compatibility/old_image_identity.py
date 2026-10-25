"""Pure lab-only OCI identity binding. Never imports Docker or a product executor."""
import hashlib
import json
import re
from typing import Any


def need(condition: object, message: str) -> None:
    if not condition:
        raise ValueError(message)


def validate_old_image_receipt(observed: dict[str, Any], pin: dict[str, Any]) -> None:
    reference, config = pin["reference"], pin["config_digest"]
    need(isinstance(reference, str) and re.fullmatch(r"ghcr\.io/bipoxdxd/duora-[a-z]+@sha256:[a-f0-9]{64}", reference), "immutable old reference required")
    need(isinstance(config, str) and re.fullmatch(r"sha256:[a-f0-9]{64}", config), "exact OCI config pin required")
    leaf = reference.split("@")[1]
    need(observed["reference"] == reference and observed["config_digest"] == config
         and observed["docker_image_id"] in (leaf, config)
         and observed["manifest_sha256"] == leaf.removeprefix("sha256:")
         and observed["config_digest_origin"] == "frozen_manifest_hash_bound_to_reference",
         "old Docker ID and hash-bound OCI config must remain distinct")


def bind_old_image(raw: bytes, observed: dict[str, Any], pin: dict[str, Any], *, role: str, revision: str) -> dict[str, str]:
    need(role in {"web", "worker", "migrator", "edge", "postgres"}, "unknown old image role")
    need(type(raw) is bytes and 0 < len(raw) <= 65536, "bounded exact manifest bytes required")
    need(type(pin.get("reference")) is str and pin["reference"].startswith("ghcr.io/bipoxdxd/duora-" + role + "@sha256:"), "role-bound immutable reference required")
    reference = pin["reference"]
    leaf = reference.split("@")[1]
    need("sha256:" + hashlib.sha256(raw).hexdigest() == leaf, "frozen manifest differs from exact reference leaf")
    def pairs(items: list[tuple[str, Any]]) -> dict[str, Any]:
        result: dict[str, Any] = {}
        for key, value in items:
            need(key not in result, "duplicate OCI manifest key")
            result[key] = value
        return result
    manifest: dict[str, Any] = json.loads(raw, object_pairs_hook=pairs, parse_constant=lambda _: need(False, "nonfinite OCI manifest"))
    media_type, config_type = (("application/vnd.docker.distribution.manifest.v2+json", "application/vnd.docker.container.image.v1+json")
                               if role == "postgres" else ("application/vnd.oci.image.manifest.v1+json", "application/vnd.oci.image.config.v1+json"))
    need(type(manifest) is dict and type(manifest.get("schemaVersion")) is int and manifest["schemaVersion"] == 2
         and manifest.get("mediaType") == media_type
         and type(manifest.get("config")) is dict
         and manifest["config"].get("mediaType") == config_type
         and manifest["config"].get("digest") == pin["config_digest"], "frozen manifest does not bind the exact OCI config")
    result = {"reference": reference, "config_digest": manifest["config"]["digest"],
              "docker_image_id": observed["Id"], "manifest_sha256": hashlib.sha256(raw).hexdigest(),
              "config_digest_origin": "frozen_manifest_hash_bound_to_reference"}
    validate_old_image_receipt(result, pin)
    need(observed.get("Architecture") == "amd64" and observed.get("Os") == "linux"
         and reference in observed.get("RepoDigests", []), "real image platform or immutable RepoDigest differs")
    descriptor = observed.get("Descriptor")
    if descriptor is not None:
        need(type(descriptor) is dict and descriptor.get("digest") == leaf, "Docker descriptor differs from exact leaf")
    labels = observed["Config"].get("Labels") or {}
    if role == "postgres":
        need(labels.get("io.duora.postgres.version") == "18.6", "old PostgreSQL version differs")
    else:
        need(labels.get("org.opencontainers.image.source") == "https://github.com/BipoXDXD/duora"
             and labels.get("org.opencontainers.image.title") == "duora-" + role
             and labels.get("org.opencontainers.image.revision") == revision, "old source, role or revision differs")
    return result
