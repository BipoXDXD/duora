"""Pure public receipt validator. No Docker, product imports, IO or fixture initialization."""
import hashlib
import json
import re
from typing import Any
from old_image_identity import validate_old_image_receipt

SCOPE = "native_current_v2_recovery_composed_local_cohort_http_fixture"
EXECUTOR = "ac5510f6b47ffac1a07ee1cb79af386cb931249140c32aae9d704265929ec1ae"
RECEIVER = "1ccd1ada4777c80081c4f9bb2a64a500b1e221f794406319504d9e66cbb975cb"
SQL = "5d666adcaefeaa7883d4e2596c6ef66ee03b67d04bca79d590e690f801573747"
BUNDLE = {"duora_deploy.py": EXECUTOR,
          "reconcile_payload.py": "b08625009bf74afe3e84750e383a0fc4262de17d6f6f2bb4a264cb9244c9b6b4",
          "runtime_guard.py": "f54f26b817638d2b905f22e30d5c1ffbce87f2e23d4087e1f8e8129cfd7a3f19",
          "install_executor.py": "0113832a49e021d4c3444115bbb97969f4b06859b7d43fbecabdc0ece8d55f99"}
BINDINGS = {"current", "previous", "manifest", "environment", "floor", "runtime", "compose", "hostEnvironment"}
TRUE = ("recovery_CLI_exercised", "complete_control_CLI_exercised", "root_locks_exercised",
        "executor_handoff_exercised", "runtime_guard_exercised", "real_backup_WAL_verify",
        "real_receiver_HTTP_client", "actual_recovered_applications", "actual_sleeping_observer",
        "observer_deploy_and_start_exercised", "cleanup_successful", "keeper_cleaned_last")
FALSE = ("incoming_ghcr_exercised", "product_forward_main_exercised", "real_Coolify_hosting_exercised",
         "observer_main_proxy_checks_exercised", "production_HTTPS_www_exercised",
         "native_B2_API_exercised", "native_egress_firewall_exercised", "tail_interruptions_exercised",
         "production_acceptance", "publication_authorized_by_receipt")


def need(condition, message):
    if not condition:
        raise ValueError(message)


def failure_locations(error: BaseException) -> list[dict]:
    """Closed source locations only: never exception messages, locals, paths or command output."""
    sources = {"current_recovery_lab.py", "duora_deploy.py", "runtime_guard.py", "reconcile_payload.py", "install_executor.py"}
    kinds = {"DeployError", "GuardError", "PayloadError", "ValueError", "KeyError", "TypeError", "OSError", "RuntimeError",
             "TimeoutExpired", "CalledProcessError"}
    result, seen = [], set()
    current: BaseException | None = error
    while current is not None and id(current) not in seen and len(result) < 4:
        seen.add(id(current))
        frames = []
        frame = current.__traceback__
        while frame is not None and len(frames) < 8:
            source = frame.tb_frame.f_code.co_filename.rsplit("/", 1)[-1]
            if source in sources and 0 < frame.tb_lineno <= 10000:
                frames.append({"source": source, "line": frame.tb_lineno})
            frame = frame.tb_next
        kind = type(current).__name__
        result.append({"kind": kind if kind in kinds else "Other", "frames": frames})
        current = current.__cause__
    return result


def digest(value):
    return hashlib.sha256(json.dumps(value, sort_keys=True, separators=(",", ":"), allow_nan=False).encode()).hexdigest()


def sha(value):
    need(type(value) is str and re.fullmatch(r"[a-f0-9]{64}", value), "invalid public hash")


def decode(raw):
    need(type(raw) is bytes and len(raw) <= 131072, "receipt byte bound")
    def pairs(items):
        result = {}
        for key, value in items:
            need(key not in result, "duplicate receipt key")
            result[key] = value
        return result
    value = json.loads(raw, object_pairs_hook=pairs, parse_constant=lambda _: need(False, "nonfinite JSON"))
    need(type(value) is dict, "receipt object required")
    return value


def validate_event_barriers(continuity: dict[str, Any]):
    barriers: dict[str, Any] = continuity["event_barriers"]
    need(type(barriers) is dict and barriers.get("end_consumed") is True
         and barriers.get("reader_complete") is True and barriers.get("stream_alive_at_start") is True
         and barriers.get("stream_alive_at_end") is True, "consumed live start/end event boundaries required")
    nonce = barriers["nonce"]
    need(type(nonce) is str and re.fullmatch(r"[a-f0-9]{32}", nonce)
         and barriers["event_filter"] == "label=io.duora.current-recovery-lab=" + nonce, "exact owned event nonce filter")
    for key in ("since_unix_seconds", "protected_start_ns", "protected_end_ns", "records_consumed"):
        need(type(barriers[key]) is int and barriers[key] > 0, "positive native boundary integer required")
    start: dict[str, Any] = barriers["start"]
    end: dict[str, Any] = barriers["end"]
    for marker in (start, end):
        need(type(marker) is dict and marker.get("type") == "container" and marker.get("action") == "create",
             "real unstarted container-create marker required")
        sha(marker["id"])
        for key in ("record_index", "time_nano", "consumed_at_ns"):
            need(type(marker[key]) is int and marker[key] >= (0 if key == "record_index" else 1), "known consumed marker coordinates")
    need(start["id"] != end["id"] and 0 <= start["record_index"] < end["record_index"] < barriers["records_consumed"],
         "distinct ordered consumed marker records required")
    need(barriers["since_unix_seconds"] * 1_000_000_000 <= start["time_nano"]
         <= start["consumed_at_ns"] <= barriers["protected_start_ns"] <= barriers["protected_end_ns"]
         <= end["time_nano"] <= end["consumed_at_ns"] and start["time_nano"] < end["time_nano"],
         "event timestamps must bracket the entire protected interval")
    sha(continuity["events_sha256"])


def validate_actions_opt_fixture(value):
    need(type(value) is dict and set(value) == {"path", "scope", "original_mode", "fixture_mode", "restored_mode", "changed", "root_owned", "non_recursive", "identity_unchanged", "identity_sha256", "restored"}, "exact hosted Actions /opt prerequisite receipt required")
    need(value["path"] == "/opt" and value["scope"] == "disposable_hosted_actions_only"
         and type(value["original_mode"]) is int and value["original_mode"] in {0o755, 0o777}
         and type(value["fixture_mode"]) is int and value["fixture_mode"] == 0o755
         and type(value["restored_mode"]) is int and value["restored_mode"] == value["original_mode"]
         and type(value["changed"]) is bool and value["changed"] == (value["original_mode"] == 0o777)
         and all(value[key] is True for key in ("root_owned", "non_recursive", "identity_unchanged", "restored")),
         "unknown fixture preimage, drift or incomplete restoration")
    sha(value["identity_sha256"])


def validate_receipt(r, pins):
    need(r.get("schema") == 1 and type(r["schema"]) is int and r.get("scope") == SCOPE, "receipt scope")
    need(r.get("success") is True and r.get("payload_origin") == "local_cohort_build"
         and r.get("control_plane") == "CoolifyHTTP_fixture", "bounded success required")
    for name in TRUE:
        need(r.get(name) is True, "missing native evidence flag: " + name)
    for name in FALSE:
        need(r.get(name) is False, "overclaimed boundary: " + name)
    validate_actions_opt_fixture(r["actions_opt_fixture"])
    runtime, run, source = r["runtime"], r["run"], r["source"]
    need(runtime == {"system": "Linux", "architecture": "x86_64", "uid": 0,
                     "github_hosted": True, "python": "3.12"}, "native runtime")
    need(run["pull_request"] == 260 and all(type(run[k]) is str and run[k].isdigit()
         and int(run[k]) > 0 for k in ("id", "attempt")), "run identity")
    for key in ("workflow_sha", "merge_sha", "workflow_head"):
        need(type(run[key]) is str and re.fullmatch(r"[a-f0-9]{40}", run[key]), "source run identity")
    need(source["receiver_sha256"] == RECEIVER
         and source["canonical_sql_sha256"] == SQL and source["cohort_commit"] == pins["cohort_commit"]
         and source["cohort_tree"] == pins["cohort_tree"] and source["old_commit"] == pins["old_commit"], "frozen source")
    sha(source["executor_sha256"])
    need(set(source["bundle_sha256"]) == set(BUNDLE)
         and source["bundle_sha256"]["duora_deploy.py"] == source["executor_sha256"]
         and all(source["bundle_sha256"][name] == expected for name, expected in BUNDLE.items() if name != "duora_deploy.py"),
         "exact copied actual-head bundle required")
    for value in (source["harness_sha256"], source["parser_sha256"], source["image_identity_helper_sha256"], r["functional_receipt_sha256"], r["command_trace_sha256"], r["public_records_sha256"]):
        sha(value)
    observed = r["old_images"]
    need(set(observed) == set(pins["old_images"]), "old cached roles")
    for role, pin in pins["old_images"].items():
        validate_old_image_receipt(observed[role], pin)
    need(re.fullmatch(r"sha256:[a-f0-9]{64}", r["new_migrator"]["docker_image_id"])
         and r["new_migrator"]["source_head"] == pins["cohort_commit"] and r["new_migrator"]["build_count_this_driver"] == 0,
         "single inherited cohort build")
    history = r["history"]
    need(history["before"] == pins["old_migrations"] and history["after_ddl"] == pins["new_migrations"]
         and history["after_recovery"] == pins["new_migrations"], "exact ordered35 to48 catalog")
    profile = r["runtime_before"]
    need(profile["schema"] == 1 and profile["context"]["project"] == "duora"
         and set(profile["services"]) == {"postgres", "backup"}
         and len(profile["volumes"]) == 1 and profile["volumes"][0]["name"] == "duora_pgdata", "real native profile shape")
    need(r["runtime_before"] == r["runtime_after"], "PG/backup/volume baseline drift")
    database = r["database_before"]
    need(set(database) == {"id", "image", "startedAt", "restartCount", "mountsSha256", "postmasterStart"}
         and type(database["restartCount"]) is int and database["restartCount"] == 0
         and database["startedAt"] and database["postmasterStart"]
         and database["id"] == profile["services"]["postgres"]["id"], "complete persistent database identity")
    sha(database["id"])
    sha(database["mountsSha256"])
    need(r["database_before"] == r["database_after"], "database identity/postmaster/mount drift")
    need(r["sentinel_before_sha256"] == r["sentinel_after_sha256"], "sentinel drift")
    sha(r["sentinel_before_sha256"])
    need(r["session_before"] == r["session_after"] and type(r["session_before"]["pid"]) is int
         and r["session_before"]["pid"] > 0 and r["session_before"]["backend_start"], "persistent readonly backend")
    for name in ("bindings_before", "bindings_after"):
        need(set(r[name]) == BINDINGS, "preservation bindings")
        for value in r[name].values():
            sha(value)
    need(r["bindings_before"] == r["bindings_after"], "preserved files drift")
    need(r["assets_before"] == r["assets_after"] and set(r["assets_before"]) == {"quarantine", "derivatives"}, "asset markers drift")
    for value in r["assets_before"].values():
        sha(value)
    need(r["schema_after_ddl_sha256"] == r["schema_after_recovery_sha256"], "recovery changed expanded schema")
    sha(r["schema_after_ddl_sha256"])
    api = r["api"]
    need(all(type(api[key]) is int for key in ("patch_count", "start_count", "finished_count", "rejected_requests", "failed_jobs")), "integer HTTP counters")
    need(api["patch_count"] == 1 and api["start_count"] == 1 and api["finished_count"] == 1
         and api["rejected_requests"] == 0 and api["failed_jobs"] == 0
         and api["lock_handoff"] == {"host_held": True, "project_held": True, "executor_free": True}, "positive once-only HTTP/lock proof")
    need(type(api["deployment_uuid"]) is str and re.fullmatch(r"[a-z0-9][a-z0-9-]{0,63}", api["deployment_uuid"]), "positive job UUID")
    need(r["control_result"]["complete"] is True and r["control_result"]["deploymentUuid"] == api["deployment_uuid"]
         and r["repeated_control_result"] == r["control_result"], "same known completed result")
    need(r["repeat_effects"] == {"sql_writes": 0, "backup": 0, "compose_up": 0, "api_patch": 0, "api_start": 0}, "repeat must not produce")
    need(r["archive"]["fences_absent"] is True and r["archive"]["active_intents_absent"] is True
         and r["archive"]["global_preimage_sha256"] == r["original_intents"]["global"]
         and r["archive"]["project_preimage_sha256"] == r["original_intents"]["project"], "durable archive/preimage/fence proof")
    for key in ("application_outcome_sha256", "control_outcome_sha256", "compatibility_pointer_sha256"):
        sha(r["archive"][key])
    need(set(r["applications"]) == {"web", "worker", "edge"}, "actual apps missing")
    for role, app in r["applications"].items():
        need(app["imageId"] == observed[role]["docker_image_id"] and app["reference"] == observed[role]["reference"]
             and app["commit"] == pins["old_commit"] and app["restartCount"] == 0, "old app runtime identity")
        sha(app["id"])
    need(r["observer"]["imageId"] == observed["edge"]["docker_image_id"] and r["observer"]["reference"] == observed["edge"]["reference"]
         and r["observer"]["restartCount"] == 0, "actual observer identity")
    sha(r["observer"]["id"])
    continuity = r["continuity"]
    validate_event_barriers(continuity)
    need(continuity["samples"] >= 2 and continuity["all_ready"] is True and continuity["maximum_gap_seconds"] <= 3
         and continuity["events_alive_through_end"] is True and continuity["forbidden_events"] == 0
         and continuity["covers_entire_recovery"] is True, "continuous readiness/events required")
    need(r["child_normal_deploy_exit"] == 0 and r["fault"] == "after_acknowledged_migrator_exit0_before_next_producer", "composed boundary")
    return r
