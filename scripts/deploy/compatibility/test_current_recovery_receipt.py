"""Tiny new stdlib parser tests only. These fixtures are NEVER native execution receipts."""
import copy
import json
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parent))
from current_recovery_receipt import BINDINGS, EXECUTOR, FALSE, RECEIVER, SCOPE, SQL, TRUE, decode, validate_receipt


def sample():
    h, commit = "a" * 64, "b" * 40
    images = {role: {"reference": "ghcr.io/bipoxdxd/duora-" + role + "@sha256:" + h,
                     "config_digest": "sha256:" + h} for role in ("web", "worker", "edge", "migrator", "postgres")}
    pins = {"cohort_commit": commit, "cohort_tree": "c" * 40, "old_commit": "d" * 40,
            "old_images": copy.deepcopy(images), "old_migrations": [str(x) for x in range(35)],
            "new_migrations": [str(x) for x in range(48)]}
    # The runtime Docker object ID and OCI config digest are separate even in pure fixtures.
    for entry in images.values():
        entry.update(docker_image_id=entry["reference"].split("@")[1], manifest_sha256=h,
                     config_digest_origin="frozen_manifest_hash_bound_to_reference")
    old = pins["old_commit"]
    result = {"schema": 1, "scope": SCOPE, "success": True, "payload_origin": "local_cohort_build", "control_plane": "CoolifyHTTP_fixture",
              **{key: True for key in TRUE}, **{key: False for key in FALSE},
              "runtime": {"system": "Linux", "architecture": "x86_64", "uid": 0, "github_hosted": True, "python": "3.12"},
              "run": {"id": "1", "attempt": "1", "pull_request": 260, "workflow_sha": commit, "merge_sha": commit, "workflow_head": commit},
              "source": {"executor_sha256": EXECUTOR, "receiver_sha256": RECEIVER, "canonical_sql_sha256": SQL,
                         "cohort_commit": commit, "cohort_tree": pins["cohort_tree"], "old_commit": old,
                         "bundle_sha256": {"duora_deploy.py": EXECUTOR,
                            "runtime_guard.py": "f54f26b817638d2b905f22e30d5c1ffbce87f2e23d4087e1f8e8129cfd7a3f19",
                            "reconcile_payload.py": "b08625009bf74afe3e84750e383a0fc4262de17d6f6f2bb4a264cb9244c9b6b4",
                            "install_executor.py": "0113832a49e021d4c3444115bbb97969f4b06859b7d43fbecabdc0ece8d55f99"},
                         "harness_sha256": h, "parser_sha256": h, "image_identity_helper_sha256": h},
              "functional_receipt_sha256": h, "command_trace_sha256": h, "public_records_sha256": h, "old_images": images,
              "new_migrator": {"docker_image_id": "sha256:" + h, "source_head": commit, "build_count_this_driver": 0},
              "history": {"before": pins["old_migrations"], "after_ddl": pins["new_migrations"], "after_recovery": pins["new_migrations"]},
              "runtime_before": {"schema": 1, "services": {"postgres": {"id": h}, "backup": {"id": "e" * 64}},
                                 "volumes": [{"name": "duora_pgdata"}], "context": {"project": "duora"}},
              "database_before": {"id": h, "image": "sha256:" + h, "startedAt": "2026-10-25T00:00:00Z", "restartCount": 0,
                                  "mountsSha256": h, "postmasterStart": "2026-10-25 00:00:00+00"},
              "sentinel_before_sha256": h, "sentinel_after_sha256": h,
              "session_before": {"pid": 123, "backend_start": "2026-10-25 00:00:00+00"},
              "bindings_before": {key: h for key in BINDINGS}, "bindings_after": {key: h for key in BINDINGS},
              "assets_before": {"quarantine": h, "derivatives": h}, "assets_after": {"quarantine": h, "derivatives": h},
              "schema_after_ddl_sha256": h, "schema_after_recovery_sha256": h,
              "api": {"patch_count": 1, "start_count": 1, "finished_count": 1, "rejected_requests": 0, "failed_jobs": 0,
                      "lock_handoff": {"host_held": True, "project_held": True, "executor_free": True}, "deployment_uuid": "fixture-123"},
              "control_result": {"complete": True, "deploymentUuid": "fixture-123"},
              "repeat_effects": {"sql_writes": 0, "backup": 0, "compose_up": 0, "api_patch": 0, "api_start": 0},
              "archive": {"fences_absent": True, "active_intents_absent": True, "global_preimage_sha256": h,
                          "project_preimage_sha256": h, "application_outcome_sha256": h, "control_outcome_sha256": h, "compatibility_pointer_sha256": h},
              "original_intents": {"global": h, "project": h},
              "applications": {role: {"id": h, "imageId": images[role]["docker_image_id"], "reference": images[role]["reference"],
                                      "commit": old, "restartCount": 0} for role in ("web", "worker", "edge")},
              "observer": {"id": h, "imageId": images["edge"]["docker_image_id"], "reference": images["edge"]["reference"], "restartCount": 0},
              "continuity": {"samples": 3, "all_ready": True, "maximum_gap_seconds": 0.6, "events_alive_through_end": True,
                             "forbidden_events": 0, "covers_entire_recovery": True, "events_sha256": h,
                             "event_barriers": {"nonce": "a" * 32, "event_filter": "label=io.duora.current-recovery-lab=" + "a" * 32,
                                  "since_unix_seconds": 1, "protected_start_ns": 1_000_000_120, "protected_end_ns": 1_000_000_180,
                                  "records_consumed": 3, "stream_alive_at_start": True, "stream_alive_at_end": True,
                                  "end_consumed": True, "reader_complete": True,
                                  "start": {"id": "1" * 64, "type": "container", "action": "create", "record_index": 0,
                                            "time_nano": 1_000_000_100, "consumed_at_ns": 1_000_000_110},
                                  "end": {"id": "2" * 64, "type": "container", "action": "create", "record_index": 2,
                                          "time_nano": 1_000_000_200, "consumed_at_ns": 1_000_000_210}}}, "child_normal_deploy_exit": 0,
              "fault": "after_acknowledged_migrator_exit0_before_next_producer"}
    for before, after in (("runtime_before", "runtime_after"), ("database_before", "database_after"),
                          ("session_before", "session_after"), ("control_result", "repeated_control_result")):
        result[after] = copy.deepcopy(result[before])
    return copy.deepcopy(result), pins


class NewReceiptBoundaryTests(unittest.TestCase):
    def reject(self, mutate):
        value, pins = sample()
        mutate(value)
        with self.assertRaises((ValueError, KeyError, TypeError)):
            validate_receipt(value, pins)

    def test_identity_helper_hash_is_required(self):
        value, pins = sample()
        value["source"]["image_identity_helper_sha256"] = "a" * 64
        self.assertIs(validate_receipt(value, pins), value)
        for mutation in (lambda record: record["source"].pop("image_identity_helper_sha256", None),
                         lambda record: record["source"].__setitem__("image_identity_helper_sha256", "unknown")):
            bad = copy.deepcopy(value)
            mutation(bad)
            with self.assertRaises((ValueError, KeyError, TypeError)):
                validate_receipt(bad, pins)

    def test_scoped_example_roundtrip(self):
        value, pins = sample()
        self.assertEqual(validate_receipt(decode(json.dumps(value).encode()), pins), value)

    def test_all_truth_boundaries_are_required(self):
        for name in TRUE:
            with self.subTest(name=name):
                self.reject(lambda value: value.__setitem__(name, False))
        for name in FALSE:
            with self.subTest(name=name):
                self.reject(lambda value: value.__setitem__(name, True))

    def test_ghcr_and_real_coolify_cannot_be_inferred(self):
        self.reject(lambda value: value.__setitem__("payload_origin", "ghcr"))
        self.reject(lambda value: value.__setitem__("control_plane", "Coolify"))
        self.reject(lambda value: value.__setitem__("scope", "old_web_schema48_function_only_not_executor_recovery"))

    def test_preservation_and_catalog_are_exact(self):
        self.reject(lambda value: value["bindings_after"].__setitem__("current", "f" * 64))
        self.reject(lambda value: value["history"]["after_recovery"].reverse())
        self.reject(lambda value: value["runtime_after"]["services"]["backup"].__setitem__("id", "f" * 64))
        self.reject(lambda value: value["database_after"].__setitem__("postmasterStart", "later"))
        self.reject(lambda value: value["session_after"].__setitem__("pid", 124))

    def test_empty_equal_baselines_are_not_evidence(self):
        self.reject(lambda value: value.update(runtime_before={}, runtime_after={}))
        self.reject(lambda value: value.update(database_before={}, database_after={}))

    def test_only_one_positive_job_and_no_repeat_producers(self):
        self.reject(lambda value: value["api"].__setitem__("start_count", 2))
        self.reject(lambda value: value["api"].__setitem__("start_count", True))
        self.reject(lambda value: value["api"].__setitem__("deployment_uuid", ""))
        self.reject(lambda value: value["repeat_effects"].__setitem__("backup", 1))
        self.reject(lambda value: value["api"]["lock_handoff"].__setitem__("host_held", False))
        self.reject(lambda value: value["archive"].__setitem__("fences_absent", False))

    def test_frozen_sidecar_and_receiver_cannot_drift(self):
        self.reject(lambda value: value["source"].__setitem__("receiver_sha256", "f" * 64))
        self.reject(lambda value: value["source"]["bundle_sha256"].__setitem__("runtime_guard.py", "f" * 64))
        self.reject(lambda value: value["new_migrator"].__setitem__("build_count_this_driver", 1))

    def test_continuity_requires_full_native_observation(self):
        self.reject(lambda value: value["continuity"].__setitem__("forbidden_events", 1))
        self.reject(lambda value: value["continuity"].__setitem__("maximum_gap_seconds", 4))
        self.reject(lambda value: value["continuity"].__setitem__("events_alive_through_end", False))
        self.reject(lambda value: value["continuity"].__setitem__("covers_entire_recovery", False))

    def test_event_boundaries_must_be_consumed_known_and_ordered(self):
        self.reject(lambda value: value["continuity"].pop("event_barriers"))
        for key in ("end_consumed", "reader_complete", "stream_alive_at_start", "stream_alive_at_end"):
            with self.subTest(key=key):
                self.reject(lambda value: value["continuity"]["event_barriers"].__setitem__(key, False))
        for marker, key, bad in (("start", "id", ""), ("end", "id", "1" * 64),
                                 ("end", "record_index", 0), ("end", "time_nano", 1_000_000_150),
                                 ("start", "consumed_at_ns", 1_000_000_130),
                                 ("end", "consumed_at_ns", None), ("end", "record_index", True)):
            with self.subTest(marker=marker, key=key):
                self.reject(lambda value: value["continuity"]["event_barriers"][marker].__setitem__(key, bad))
        self.reject(lambda value: value["continuity"]["event_barriers"].__setitem__("records_consumed", 2))
        self.reject(lambda value: value["continuity"]["event_barriers"].__setitem__("event_filter", "type=container"))

    def test_v3_container_create_marker_delta(self):
        # New v3 delta only: network events cannot satisfy the label-filtered container barrier contract.
        value, pins = sample()
        self.assertIs(validate_receipt(value, pins), value)
        for phase in ("start", "end"):
            with self.subTest(phase=phase):
                def network_marker(receipt):
                    marker = receipt["continuity"]["event_barriers"][phase]
                    marker["type"] = "network"
                    marker["labels"] = {"io.duora.current-recovery-lab": "a" * 32}
                self.reject(network_marker)
                self.reject(lambda receipt: receipt["continuity"]["event_barriers"][phase].__setitem__("action", "start"))
                self.reject(lambda receipt: receipt["continuity"]["event_barriers"][phase].__setitem__("type", "image"))

    def test_decode_refuses_duplicate_nonfinite_and_oversize(self):
        for raw in (b'{"scope":1,"scope":2}', b'{"x":NaN}', b" " * 131073):
            with self.subTest(raw=raw[:24]), self.assertRaises(ValueError):
                decode(raw)


if __name__ == "__main__":
    unittest.main()
