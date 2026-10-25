"""Closed read-only collection, contract and pre-mutation refusal contracts."""
import copy
import json
import unittest

import runtime_guard as guard
from test_runtime_guard import container, volume

COMPOSE = ["docker", "compose", "--file", "/synthetic/compose.yml", "--env-file", "/synthetic/current.env"]
REFERENCE = guard.LEGACY_DIGESTS["amd64"]


class RuntimeDaemon:
    def __init__(self) -> None:
        self.commands: list[list[str]] = []
        self.services = {}
        self.config = {"name": "duora", "volumes": {"pgdata": {"name": "duora_pgdata"}},
                       "networks": {"data": {"name": "duora_data", "internal": True}}, "services": {}}
        for role, identifier in [("postgres", "1" * 64), ("backup", "2" * 64)]:
            item = container()
            item["Id"] = identifier
            item["Config"]["Image"] = REFERENCE
            item["Config"]["Labels"].update({"com.docker.compose.project": "duora", "com.docker.compose.service": role})
            item["Mounts"][0]["RW"] = role == "postgres"
            item["HostConfig"].update({"NanoCpus": 0, "CpuShares": 0, "Privileged": False, "ReadonlyRootfs": False})
            self.services[role] = item
            self.config["services"][role] = {"image": REFERENCE, "networks": {"data": {}}, "cap_drop": ["ALL"],
                "security_opt": ["no-new-privileges:true"], "restart": "unless-stopped",
                "deploy": {"resources": {"limits": {"memory": 1024, "pids": 128}}}}

    def __call__(self, command: list[str]) -> bytes:
        self.commands.append(command)
        if command == COMPOSE + ["config", "--format", "json"]:
            return json.dumps(self.config).encode()
        if command[:len(COMPOSE) + 2] == COMPOSE + ["config", "--hash"]:
            return (command[-1] + " " + "c" * 64 + "\n").encode()
        if command[:len(COMPOSE) + 3] == COMPOSE + ["ps", "--all", "--quiet"]:
            return (self.services[command[-1]]["Id"] + "\n").encode()
        if command == ["docker", "compose", "version", "--short"]:
            return b"5.6.0\n"
        if command[:2] == ["docker", "version"]:
            return json.dumps({"Version": "29.9.1", "ApiVersion": "1.56"}).encode()
        if command[:3] == ["docker", "image", "inspect"]:
            return json.dumps({"Id": "sha256:" + "a" * 64}).encode()
        if command[:3] == ["docker", "container", "inspect"]:
            return json.dumps(next(i for i in self.services.values() if i["Id"] == command[-1])).encode()
        if command[:3] == ["docker", "volume", "inspect"]:
            return json.dumps(volume()).encode()
        raise AssertionError("Mutation or unbounded/unexpected command: " + repr(command))

    def collect(self) -> dict:
        return guard.collect_runtime(COMPOSE, postgres_reference=REFERENCE, compose_sha256="f" * 64,
                                     host_id_sha256="e" * 64, capture=self)


class CollectionTests(unittest.TestCase):
    def test_collection_binds_desired_and_actual_identity_without_cleartext_environment(self) -> None:
        daemon = RuntimeDaemon()
        result = daemon.collect()
        guard.require_preserved(result["profile"], result["profile"])
        self.assertEqual(result["inventorySha256"], guard.digest({"profile": result["profile"], "audit": result["audit"]}))
        self.assertNotIn("do-not-print", json.dumps(result))
        self.assertNotIn("SYNTHETIC_SECRET=", json.dumps(result))
        self.assertEqual(len(result["profile"]["services"]), 2)

    def test_preexisting_resource_network_mount_image_label_and_config_drift_is_not_enrolled(self) -> None:
        for mode in ["memory", "network", "mount", "image", "role", "config", "missing"]:
            daemon = RuntimeDaemon()
            item = daemon.services["postgres"]
            if mode == "memory": item["HostConfig"]["Memory"] = 2048
            if mode == "network": item["NetworkSettings"]["Networks"]["unreviewed"] = {}
            if mode == "mount": item["Mounts"][0]["Name"] = "foreign_data"
            if mode == "image": item["Image"] = "sha256:" + "b" * 64
            if mode == "role": item["Config"]["Labels"]["com.docker.compose.service"] = "web"
            if mode == "config": item["Config"]["Labels"]["com.docker.compose.config-hash"] = "0" * 64
            if mode == "missing": item["Id"] = ""
            with self.subTest(mode=mode), self.assertRaises(guard.GuardError):
                daemon.collect()

    def test_explicit_legacy_profile_is_bounded_by_digest_sql_hash_and_schema(self) -> None:
        release = {"contractVersion": "v1", "architecture": "amd64", "images": {"postgres": REFERENCE},
                   "schema": {"max": guard.LEGACY_SCHEMA_CEILING}}
        guard.require_contract(release, schema_head=guard.LEGACY_SCHEMA_CEILING, legacy_sql_sha256=guard.LEGACY_SQL_SHA256)
        variants = [copy.deepcopy(release) for _ in range(3)]
        variants[0]["images"]["postgres"] = REFERENCE[:-1] + "0"
        variants[1]["schema"]["max"] = "20990101000000_Future"
        variants[2]["contractVersion"] = "v3"
        for changed in variants:
            with self.subTest(changed=changed), self.assertRaises(guard.GuardError):
                guard.require_contract(changed, schema_head=guard.LEGACY_SCHEMA_CEILING, legacy_sql_sha256=guard.LEGACY_SQL_SHA256)
        for schema, sql in [("20990101000000_Future", guard.LEGACY_SQL_SHA256), (guard.LEGACY_SCHEMA_CEILING, "0" * 64)]:
            with self.subTest(schema=schema, sql=sql), self.assertRaises(guard.GuardError):
                guard.require_contract(release, schema_head=schema, legacy_sql_sha256=sql)

    def test_v2_cannot_be_selected_by_missing_legacy_metadata_or_an_unknown_version(self) -> None:
        release = {"contractVersion": "v2", "architecture": "amd64", "images": {"postgres": REFERENCE},
                   "schema": {"max": guard.LEGACY_SCHEMA_CEILING}}
        guard.require_contract(release, schema_head="20990101000000_Future", legacy_sql_sha256="")
        for version in [None, "", "v0", "v999", True]:
            with self.subTest(version=version), self.assertRaises(guard.GuardError):
                guard.require_contract(dict(release, contractVersion=version), schema_head="", legacy_sql_sha256="")
