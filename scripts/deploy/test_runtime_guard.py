"""Identity contract only; initial inventory review and real drift remain separate mandatory gates."""
import copy
import json
import unittest

import runtime_guard as guard


def container() -> dict:
    return {"Id": "1" * 64, "Image": "sha256:" + "a" * 64, "Created": "2026-10-23T01:49:00Z",
            "RestartCount": 0, "State": {"Status": "running", "Running": True, "Restarting": False,
            "StartedAt": "2026-10-23T01:49:01Z", "Health": {"Status": "healthy", "Log": []}, "Pid": 111},
            "Config": {"Image": "ghcr.io/bipoxdxd/duora-postgres@sha256:" + "b" * 64,
                       "Env": ["SYNTHETIC_SECRET=do-not-print"], "Labels": {"com.docker.compose.config-hash": "c" * 64}},
            "HostConfig": {"Memory": 1024, "PidsLimit": 128, "RestartPolicy": {"Name": "unless-stopped"},
                           "SecurityOpt": ["no-new-privileges:true"], "CapDrop": ["ALL"]},
            "Mounts": [{"Type": "volume", "Name": "duora_pgdata", "Destination": "/var/lib/postgresql", "RW": True}],
            "NetworkSettings": {"Networks": {"duora_data": {"IPAddress": "172.30.20.2", "EndpointID": "endpoint"}}}}


def volume() -> dict:
    return {"Name": "duora_pgdata", "CreatedAt": "2026-10-22T10:00:00Z", "Driver": "local", "Scope": "local",
            "Mountpoint": "/var/lib/docker/volumes/duora_pgdata/_data", "Options": None,
            "Labels": {"com.docker.compose.project": "duora", "com.docker.compose.volume": "pgdata"}}


def reviewed_profile() -> dict:
    identity = {"id": "1" * 64, "imageId": "sha256:" + "a" * 64, "createdAt": "2026-10-23T01:49:00Z",
                "startedAt": "2026-10-23T01:49:01Z", "restartCount": 0, "runtimeSha256": "d" * 64}
    backup = dict(identity, id="2" * 64)
    return {"schema": 1, "context": {"hostIdSha256": "e" * 64, "project": "duora", "guardVersion": 1,
            "dockerVersion": "29.9.1", "dockerApi": "1.52", "dockerServerSha256": "a" * 64,
            "composeVersion": "5.6.0", "composeSha256": "f" * 64,
            "configHashes": {"postgres": "c" * 64, "backup": "c" * 64}},
            "services": {"postgres": identity, "backup": backup},
            "volumes": [{"name": "duora_pgdata", "createdAt": "2026-10-22T10:00:00Z", "runtimeSha256": "a" * 64}]}


class RuntimeIdentityTests(unittest.TestCase):
    def test_docker_mount_enumeration_order_is_ignored_but_no_mount_record_or_field_is_lost(self) -> None:
        original = container()
        original["Mounts"].append({"Type": "bind", "Source": "/synthetic/config", "Destination": "/config", "RW": False})
        changed = copy.deepcopy(original)
        changed["Mounts"].reverse()
        self.assertEqual(guard.container_identity(original), guard.container_identity(changed))
        changed["Mounts"][0]["RW"] = True
        self.assertNotEqual(guard.container_identity(original), guard.container_identity(changed))

    def test_stable_identity_hides_environment_and_ignores_only_volatile_health_logs_and_pid(self) -> None:
        original = container()
        identity = guard.container_identity(original)
        changed = copy.deepcopy(original)
        changed["State"]["Pid"] = 222
        changed["State"]["Health"]["Log"] = [{"Output": "a new readiness observation"}]
        self.assertEqual(identity, guard.container_identity(changed))
        self.assertEqual(identity["id"], original["Id"])
        self.assertRegex(identity["runtimeSha256"], r"^[a-f0-9]{64}$")
        self.assertNotIn("SYNTHETIC_SECRET", json.dumps(identity))
        self.assertNotIn("do-not-print", json.dumps(identity))

    def test_actual_resource_security_environment_mount_and_network_changes_change_identity(self) -> None:
        original = container()
        variants = []
        for key, value in [("Memory", 2048), ("PidsLimit", 512), ("SecurityOpt", []), ("CapDrop", []),
                           ("RestartPolicy", {"Name": "always"})]:
            changed = copy.deepcopy(original)
            changed["HostConfig"][key] = value
            variants.append(changed)
        changed = copy.deepcopy(original)
        changed["Config"]["Env"] = ["SYNTHETIC_SECRET=changed"]
        variants.append(changed)
        changed = copy.deepcopy(original)
        changed["Mounts"][0]["RW"] = False
        variants.append(changed)
        changed = copy.deepcopy(original)
        changed["NetworkSettings"]["Networks"]["unreviewed_egress"] = {"IPAddress": "172.20.0.4"}
        variants.append(changed)
        for changed in variants:
            with self.subTest(changed=changed["HostConfig"]):
                self.assertNotEqual(guard.container_identity(original), guard.container_identity(changed))

    def test_restart_recreation_and_image_changes_cannot_keep_the_original_identity(self) -> None:
        original = container()
        for key, value in [("Id", "2" * 64), ("Image", "sha256:" + "b" * 64), ("RestartCount", 1),
                           ("Created", "2026-10-23T02:00:00Z")]:
            changed = copy.deepcopy(original)
            changed[key] = value
            with self.subTest(key=key):
                self.assertNotEqual(guard.container_identity(original), guard.container_identity(changed))
        changed = copy.deepcopy(original)
        changed["State"]["StartedAt"] = "2026-10-23T02:00:01Z"
        self.assertNotEqual(guard.container_identity(original), guard.container_identity(changed))

    def test_unknown_missing_stopped_or_unhealthy_state_is_a_closed_failure(self) -> None:
        variants = [{}, container(), container(), container(), container()]
        variants[1]["State"]["Running"] = False
        variants[2]["State"]["Health"]["Status"] = "unhealthy"
        variants[3]["State"]["Restarting"] = True
        variants[4]["RestartCount"] = True
        missing_health = container()
        del missing_health["State"]["Health"]
        variants.append(missing_health)
        for status in ("starting", "unknown"):
            changed = container()
            changed["State"]["Health"]["Status"] = status
            variants.append(changed)
        paused = container()
        paused["State"]["Status"] = "paused"
        variants.append(paused)
        for changed in variants:
            with self.subTest(state=changed.get("State")), self.assertRaises(guard.GuardError):
                guard.container_identity(changed)

    def test_volume_identity_detects_recreation_driver_mountpoint_and_label_changes(self) -> None:
        original = volume()
        identity = guard.volume_identity(original)
        for key, value in [("CreatedAt", "2026-10-23T02:00:00Z"), ("Driver", "foreign"),
                           ("Mountpoint", "/different"), ("Labels", {}), ("Options", {"device": "/different"})]:
            changed = copy.deepcopy(original)
            changed[key] = value
            with self.subTest(key=key):
                self.assertNotEqual(identity, guard.volume_identity(changed))
        with self.assertRaises(guard.GuardError):
            guard.volume_identity({})

    def test_exact_reviewed_profile_is_accepted_without_mutating_or_enrolling_it(self) -> None:
        baseline = reviewed_profile()
        observed = copy.deepcopy(baseline)
        before = copy.deepcopy(baseline)
        guard.require_preserved(baseline, observed)
        self.assertEqual(before, baseline)
        self.assertEqual(before, observed)

    def test_context_service_volume_or_fingerprint_drift_refuses_without_reenrollment(self) -> None:
        baseline = reviewed_profile()
        variants = []
        for key, value in [("hostIdSha256", "0" * 64), ("project", "foreign"), ("dockerVersion", "changed"),
                           ("composeVersion", "changed"), ("composeSha256", "0" * 64), ("guardVersion", 2)]:
            observed = copy.deepcopy(baseline)
            observed["context"][key] = value
            variants.append(observed)
        for service in ["postgres", "backup"]:
            observed = copy.deepcopy(baseline)
            observed["services"][service]["runtimeSha256"] = "0" * 64
            variants.append(observed)
        observed = copy.deepcopy(baseline)
        observed["volumes"][0]["runtimeSha256"] = "0" * 64
        variants.extend([observed, {}, dict(baseline, schema=True), dict(baseline, schema=2)])
        for observed in variants:
            before = copy.deepcopy(baseline)
            with self.subTest(observed=observed), self.assertRaises(guard.GuardError):
                guard.require_preserved(baseline, observed)
            self.assertEqual(baseline, before)
        with self.assertRaises(guard.GuardError):
            guard.require_preserved({}, reviewed_profile())
