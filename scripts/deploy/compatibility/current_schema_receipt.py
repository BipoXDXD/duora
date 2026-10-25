"""Specific fail-closed receipt checks; synthetic unit tests do not prove native DB compatibility."""

import re
import hashlib
from old_image_identity import validate_old_image_receipt

HEX40 = re.compile(r"[a-f0-9]{40}")
HEX64 = re.compile(r"[a-f0-9]{64}")


def need(condition, message):
    if not condition:
        raise ValueError(message)


def check_history(actual, expected):
    need(
        isinstance(actual, list) and isinstance(expected, list) and expected,
        "missing migration history",
    )
    need(
        all(isinstance(item, str) and item for item in actual),
        "invalid migration identity",
    )
    need(
        actual == expected and len(set(actual)) == len(actual),
        "migration set/count differs",
    )


def check_preservation(before, after):
    fields = (
        "id",
        "image",
        "started_at",
        "restart_count",
        "mounts",
        "postmaster_start",
    )
    need(
        isinstance(before, dict) and isinstance(after, dict),
        "missing database identity",
    )
    need(
        all(key in before and key in after for key in fields),
        "incomplete database identity",
    )
    need(
        all(before[key] == after[key] for key in fields), "PostgreSQL identity changed"
    )
    need(
        all(
            before[key]
            for key in ("id", "image", "started_at", "mounts", "postmaster_start")
        ),
        "empty database identity",
    )
    need(
        type(before["restart_count"]) is int and before["restart_count"] == 0,
        "PostgreSQL restarted",
    )


def validate_receipt(report, pins):
    try:
        need(
            report["scope"] == "old_web_schema48_function_only_not_executor_recovery",
            "invalid qualification scope",
        )
        need(
            report["qualified_schema_states"] == [35, 48],
            "intermediate schema states are unqualified/refused",
        )
        need(
            report["success"] is True and report["cleanup_successful"] is True,
            "laboratory failed or cleanup incomplete",
        )
        source = report["source"]
        for key in (
            "cohort_commit",
            "cohort_tree",
            "old_commit",
            "canonical_sql_sha256",
        ):
            need(source[key] == pins[key], "source binding differs: " + key)
        need(
            isinstance(source["workflow_head"], str)
            and HEX40.fullmatch(source["workflow_head"]),
            "missing actual PR260 head",
        )
        for key in ("executor_source_sha256", "harness_sha256"):
            need(
                isinstance(source[key], str) and HEX64.fullmatch(source[key]),
                "missing source hash",
            )
        runtime = report["runtime"]
        need(
            runtime["system"] == "Linux"
            and runtime["architecture"] == "x86_64"
            and type(runtime["uid"]) is int
            and runtime["uid"] == 0
            and runtime["github_hosted"] is True
            and runtime["python"].startswith("3.12."),
            "requires actual disposable Linux/root/Python3.12 runtime",
        )
        need(
            report["run"]["pull_request"] == 260
            and report["run"]["id"].isdigit()
            and int(report["run"]["id"]) > 0
            and report["run"]["attempt"].isdigit()
            and int(report["run"]["attempt"]) > 0,
            "missing Actions run identity",
        )
        for role in ("web", "worker", "edge", "migrator", "postgres"):
            observed, expected = (
                report["observed_images"][role],
                pins["old_images"][role],
            )
            validate_old_image_receipt(observed, expected)
        need(
            len(pins["old_migrations"]) == 35 and len(pins["new_migrations"]) == 48,
            "qualified schema counts differ",
        )
        check_history(report["migrations_before"], pins["old_migrations"])
        check_history(report["migrations_after"], pins["new_migrations"])
        need(
            report["new_migrator"]["source_head"] == pins["cohort_commit"]
            and re.fullmatch(
                r"sha256:[a-f0-9]{64}", report["new_migrator"]["docker_image_id"]
            ),
            "missing actual newly built migrator identity",
        )
        repair = report["repair_forward"]
        need(
            repair["sql_sha256"] == pins["canonical_sql_sha256"]
            and repair["source_head"] == pins["cohort_commit"],
            "unbound repair-forward SQL",
        )
        for key in (
            "transactional",
            "idempotent",
            "no_old_sql_repair",
            "failed_transaction_rolled_back",
        ):
            need(repair[key] is True, "missing repair-forward evidence: " + key)
        need(
            repair["product_seal_written"] is False,
            "lab must not forge product SQL seal",
        )
        need(
            HEX64.fullmatch(repair["acl_before_replay"])
            and repair["acl_before_replay"] == repair["acl_after_replay"],
            "repair is not idempotent",
        )
        expected_cases = (
            ("head48_pre_sql", False, False),
            ("transaction_failed", False, False),
            ("commit_before_confirmation", False, True),
            ("post_sql_confirmed", True, True),
        )
        need(
            isinstance(repair["cases"], list) and len(repair["cases"]) == 4,
            "missing SQL boundary cases",
        )
        for case, (name, ready, committed) in zip(repair["cases"], expected_cases):
            need(
                case["name"] == name
                and case["schema_count"] == 48
                and case["app_start_permitted"] is ready
                and case["confirmed"] is ready
                and case["transaction_committed"] is committed,
                "head48 alone does not authorize recovery",
            )
        helpers = report["product_sql_helpers"]
        need(
            helpers["source_sha256"] == source["executor_source_sha256"]
            and helpers["transport"]
            == "strict_compose_exec_to_owned_docker_exec_real_results",
            "native SQL helper source/transport differs",
        )
        for key in ("quiescent_calls", "execute_sql_calls", "acl_witness_calls"):
            need(
                type(helpers[key]) is int and helpers[key] >= 3,
                "actual SQL helper evidence missing",
            )
        need(
            helpers["adversarial_acl_refused"] is True
            and helpers["repaired_acl_witness_passed"] is True,
            "real ACL witness not qualified",
        )
        check_preservation(report["database_before"], report["database_after"])
        session = report["persistent_session"]
        need(
            type(session["before"]) is int
            and session["before"] > 0
            and session["before"] == session["after"],
            "persistent SQL session lost",
        )
        need(
            session["driver"] == "pgbench"
            and session["role"] == "duora_app"
            and session["select_only_enforced"] is True
            and session["backend_start_before"]
            and session["backend_start_before"] == session["backend_start_after"],
            "persistent session must be the same SELECT-only app backend; never privileged/psql",
        )
        for key, left, right in (
            ("sentinel", "before", "after"),
            ("schema48", "before_recovery", "after_recovery"),
        ):
            value = report[key]
            need(
                isinstance(value[left], str)
                and HEX64.fullmatch(value[left])
                and value[left] == value[right],
                key + " changed",
            )
        phases = report["phases"]
        need(
            isinstance(phases, list)
            and len(phases) == 2
            and [item["name"] for item in phases]
            == ["old_schema35", "old_schema48_after_failure"],
            "missing real old-binary functional phases",
        )
        for phase in phases:
            for key, expected in (
                ("register_status", 202),
                ("confirm_status", 204),
                ("login_status", 204),
                ("profile_read_status", 200),
                ("profile_patch_status", 200),
                ("consent_count", 2),
            ):
                need(
                    type(phase[key]) is int and phase[key] == expected,
                    "old application operation failed: " + key,
                )
            need(phase["profile_value_verified"] is True, "profile write not read back")
            for key in ("outbox_count", "session_count"):
                need(
                    type(phase[key]) is int and phase[key] > 0,
                    "missing DB write: " + key,
                )
        for key in (
            "retained_old_consent_unchanged",
            "legacy_insert_as_app_has_null_fingerprint_and_locale",
            "new_fingerprint_nullable",
        ):
            need(report[key] is True, "missing compatibility evidence: " + key)
        need(
            report["retained_old_profile_read_status"] == 200,
            "old authenticated profile not retained",
        )
        boundary = report["boundary"]
        need(
            boundary["stage"] == "after_schema48_before_current"
            and boundary["injected"] is True
            and boundary["product_deploy_flow_invoked"] is False
            and boundary["product_current_record_written"] is False,
            "failure boundary or scope differs",
        )
    except (KeyError, TypeError, AttributeError) as error:
        raise ValueError("malformed or incomplete laboratory receipt") from error


def classify_startup_log(raw: bytes) -> dict:
    need(type(raw) is bytes, "startup classification requires bytes")
    sample = raw[:131072]
    known = ("OptionsValidationException", "UnauthorizedAccessException", "DirectoryNotFoundException",
             "FileNotFoundException", "CryptographicException", "SocketException", "NpgsqlException",
             "AuthenticationException", "InvalidOperationException")
    lower = sample.lower()
    return {"exception_types": [name for name in known if name.encode() in sample],
            "permission_denied": b"permission denied" in lower or b"access to the path" in lower,
            "tls_error": b"tls" in lower and (b"error" in lower or b"failed" in lower),
            "log_sha256": hashlib.sha256(sample).hexdigest(), "bytes": len(raw), "truncated": len(raw) > 131072}
