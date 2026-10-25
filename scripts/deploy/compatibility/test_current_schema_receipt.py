"""NEW lightweight parser tests; synthetic outcomes are never native DB evidence."""

import copy
import unittest
import ssl
from typing import Any, cast

from current_schema_receipt import check_history, check_preservation, validate_receipt, classify_startup_log, classify_probe_failure, classify_tls_conditions


def database():
    return {
        "id": "a" * 64,
        "image": "sha256:" + "b" * 64,
        "started_at": "2026-10-25T00:00:00Z",
        "restart_count": 0,
        "mounts": [
            {
                "type": "volume",
                "name": "private-nonce",
                "destination": "/var/lib/postgresql",
            }
        ],
        "postmaster_start": "2026-10-25T00:00:00Z",
    }


def pins():
    return {
        "cohort_commit": "1" * 40,
        "cohort_tree": "2" * 40,
        "old_commit": "3" * 40,
        "old_migrations": ["old-" + str(n) for n in range(35)],
        "new_migrations": ["migration-" + str(n) for n in range(48)],
        "canonical_sql_sha256": "4" * 64,
        "old_images": {
            role: {
                "reference": "ghcr.io/bipoxdxd/duora-" + role + "@sha256:" + "5" * 64,
                "config_digest": "sha256:" + "6" * 64,
            }
            for role in ("web", "worker", "edge", "migrator", "postgres")
        },
    }


def receipt():
    expected = pins()
    phase = {
        "register_status": 202,
        "confirm_status": 204,
        "login_status": 204,
        "profile_read_status": 200,
        "profile_patch_status": 200,
        "consent_count": 2,
        "outbox_count": 1,
        "session_count": 1,
        "profile_value_verified": True,
    }
    repair = {
        "sql_sha256": expected["canonical_sql_sha256"],
        "source_head": expected["cohort_commit"],
        "transactional": True,
        "idempotent": True,
        "no_old_sql_repair": True,
        "product_seal_written": False,
        "acl_before_replay": "d" * 64,
        "acl_after_replay": "d" * 64,
        "cases": [
            {
                "name": name,
                "schema_count": 48,
                "app_start_permitted": ready,
                "confirmed": ready,
                "transaction_committed": committed,
            }
            for name, ready, committed in (
                ("head48_pre_sql", False, False),
                ("transaction_failed", False, False),
                ("commit_before_confirmation", False, True),
                ("post_sql_confirmed", True, True),
            )
        ],
        "failed_transaction_rolled_back": True,
    }
    return {
        "repair_forward": repair,
        "product_sql_helpers": {
            "source_sha256": "8" * 64,
            "transport": "strict_compose_exec_to_owned_docker_exec_real_results",
            "quiescent_calls": 3,
            "execute_sql_calls": 3,
            "acl_witness_calls": 3,
            "adversarial_acl_refused": True,
            "repaired_acl_witness_passed": True,
        },
        "scope": "old_web_schema48_function_only_not_executor_recovery",
        "qualified_schema_states": [35, 48],
        "source": {
            "cohort_commit": expected["cohort_commit"],
            "cohort_tree": expected["cohort_tree"],
            "old_commit": expected["old_commit"],
            "workflow_head": "7" * 40,
            "executor_source_sha256": "8" * 64,
            "harness_sha256": "9" * 64,
            "canonical_sql_sha256": expected["canonical_sql_sha256"],
        },
        "runtime": {
            "system": "Linux",
            "architecture": "x86_64",
            "uid": 0,
            "github_hosted": True,
            "python": "3.12.12",
        },
        "run": {"id": "40200000000", "attempt": "1", "pull_request": 260},
        "observed_images": {
            role: {
                "reference": entry["reference"],
                "config_digest": entry["config_digest"],
                "docker_image_id": entry["reference"].split("@")[1],
                "manifest_sha256": entry["reference"].split("@sha256:")[1],
                "config_digest_origin": "frozen_manifest_hash_bound_to_reference",
            }
            for role, entry in expected["old_images"].items()
        },
        "database_before": database(),
        "database_after": database(),
        "migrations_before": expected["old_migrations"],
        "migrations_after": expected["new_migrations"],
        "new_migrator": {
            "docker_image_id": "sha256:" + "a" * 64,
            "source_head": expected["cohort_commit"],
        },
        "persistent_session": {
            "before": 1234,
            "after": 1234,
            "driver": "pgbench",
            "role": "duora_app",
            "select_only_enforced": True,
            "backend_start_before": "2026-10-25T00:00:00Z",
            "backend_start_after": "2026-10-25T00:00:00Z",
        },
        "sentinel": {"before": "a" * 64, "after": "a" * 64},
        "schema48": {"before_recovery": "b" * 64, "after_recovery": "b" * 64},
        "phases": [
            {"name": "old_schema35", **phase},
            {"name": "old_schema48_after_failure", **phase},
        ],
        "retained_old_profile_read_status": 200,
        "retained_old_consent_unchanged": True,
        "legacy_insert_as_app_has_null_fingerprint_and_locale": True,
        "new_fingerprint_nullable": True,
        "boundary": {
            "stage": "after_schema48_before_current",
            "injected": True,
            "product_deploy_flow_invoked": False,
            "product_current_record_written": False,
        },
        "cleanup_successful": True,
        "success": True,
    }


class ReceiptTests(unittest.TestCase):
    def test_synthetic_complete_receipt_is_accepted(self):
        validate_receipt(receipt(), pins())

    def test_incomplete_or_false_native_receipts_are_refused(self):
        mutations = [
            ("success", False),
            ("success", 1),
            ("scope", "full_production_rollback"),
            ("new_fingerprint_nullable", False),
            ("cleanup_successful", False),
            ("legacy_insert_as_app_has_null_fingerprint_and_locale", False),
            ("retained_old_profile_read_status", 503),
            ("retained_old_consent_unchanged", False),
            ("migrations_after", pins()["new_migrations"][:-1]),
            ("migrations_after", []),
            ("phases", []),
        ]
        for key, value in mutations:
            with self.subTest(key=key, value=value):
                data = receipt()
                data[key] = value
                with self.assertRaises(ValueError):
                    validate_receipt(data, pins())

    def test_wrong_source_runtime_image_and_failure_boundary_are_refused(self):
        changes = [
            ("source", "cohort_commit", "0" * 40),
            ("source", "canonical_sql_sha256", "0" * 64),
            ("source", "workflow_head", ""),
            ("runtime", "system", "Darwin"),
            ("runtime", "uid", 501),
            ("runtime", "github_hosted", False),
            ("boundary", "injected", False),
            ("boundary", "product_deploy_flow_invoked", True),
            ("boundary", "product_current_record_written", True),
            ("persistent_session", "after", 5678),
            ("schema48", "after_recovery", "c" * 64),
        ]
        for group, key, value in changes:
            with self.subTest(group=group, key=key):
                data = receipt()
                data[group][key] = value
                with self.assertRaises(ValueError):
                    validate_receipt(data, pins())
        data = receipt()
        data["observed_images"]["web"]["config_digest"] = "sha256:" + "f" * 64
        with self.assertRaises(ValueError):
            validate_receipt(data, pins())

    def test_health_only_or_one_failed_function_is_not_green(self):
        data = receipt()
        data["phases"][1]["profile_patch_status"] = 503
        with self.assertRaises(ValueError):
            validate_receipt(data, pins())
        data = receipt()
        data["phases"][1]["outbox_count"] = 0
        with self.assertRaises(ValueError):
            validate_receipt(data, pins())

    def test_intermediate_schemas_cannot_be_claimed(self):
        data = receipt()
        data["qualified_schema_states"] = list(range(35, 49))
        with self.assertRaises(ValueError):
            validate_receipt(data, pins())

    def test_head48_without_confirmed_incoming_transaction_cannot_qualify(self):
        missing = receipt()
        missing.pop("repair_forward")
        with self.assertRaises(ValueError):
            validate_receipt(missing, pins())
        for field, value in (
            ("sql_sha256", "f" * 64),
            ("source_head", "f" * 40),
            ("transactional", False),
            ("idempotent", False),
            ("no_old_sql_repair", False),
            ("product_seal_written", True),
            ("failed_transaction_rolled_back", False),
        ):
            with self.subTest(field=field):
                data = receipt()
                data["repair_forward"][field] = value
                with self.assertRaises(ValueError):
                    validate_receipt(data, pins())
        data = receipt()
        data["repair_forward"]["cases"][2]["app_start_permitted"] = True
        with self.assertRaises(ValueError):
            validate_receipt(data, pins())

    def test_real_helpers_and_readonly_app_session_are_required(self):
        data = receipt()
        data.pop("product_sql_helpers")
        with self.assertRaises(ValueError):
            validate_receipt(data, pins())
        for key, value in (
            ("role", "postgres"),
            ("driver", "psql"),
            ("select_only_enforced", False),
            ("backend_start_after", "reconnected"),
        ):
            with self.subTest(key=key):
                data = receipt()
                data["persistent_session"][key] = value
                with self.assertRaises(ValueError):
                    validate_receipt(data, pins())

    def test_missing_or_changed_pg_identity_is_refused(self):
        for key in database():
            with self.subTest(field=key):
                changed = copy.deepcopy(database())
                changed[key] = [] if key == "mounts" else "changed"
                with self.assertRaises(ValueError):
                    check_preservation(database(), changed)
        with self.assertRaises(ValueError):
            check_preservation({}, {})

    def test_schema_prefix_wrong_count_duplicates_or_down_are_refused(self):
        expected = ["one", "two", "three"]
        check_history(expected, expected)
        for actual in ([], ["one"], ["one", "two", "two"], ["one", "two", "foreign"]):
            with self.subTest(actual=actual):
                with self.assertRaises(ValueError):
                    check_history(actual, expected)




class StartupLogClassificationTests(unittest.TestCase):
    def test_known_exception_classification_never_returns_message_or_secret(self):
        marker = b"sensitive-unreported-value"
        value = classify_startup_log(b"System.UnauthorizedAccessException: Permission denied " + marker)
        self.assertEqual(value["exception_types"], ["UnauthorizedAccessException"])
        self.assertTrue(value["permission_denied"])
        self.assertNotIn(marker.decode(), str(value))
        self.assertEqual(set(value), {"exception_types", "permission_denied", "mentions_tls_and_error", "log_sha256", "bytes", "truncated"})

    def test_unknown_text_is_only_hashed_without_arbitrary_class_or_message(self):
        value = classify_startup_log(b"UnknownSecretClass: never-emit-this")
        self.assertEqual(value["exception_types"], [])
        self.assertFalse(value["permission_denied"])
        self.assertNotIn("never-emit-this", str(value))

    def test_log_bound_is_explicit_and_untyped_input_is_refused(self):
        value = classify_startup_log(b"x" * 131073)
        self.assertTrue(value["truncated"])
        self.assertEqual(value["bytes"], 131073)
        with self.assertRaises(ValueError): classify_startup_log(cast(Any, "not-bytes"))



class ProbeDiagnosticClassificationTests(unittest.TestCase):
    def test_standard_ssl_reason_is_preserved_without_error_text(self):
        error = ssl.SSLError(1, "never-emit-credential")
        error.reason = "TLSV1_ALERT_INTERNAL_ERROR"
        value = classify_probe_failure(error)
        self.assertEqual(value, {"kind": "ssl_error", "errno": 1, "ssl_reason": "TLSV1_ALERT_INTERNAL_ERROR"})
        self.assertNotIn("never-emit-credential", str(value))

    def test_unknown_ssl_reason_is_not_echoed(self):
        error = ssl.SSLError(1, "private-message")
        error.reason = "private-reason"
        self.assertEqual(classify_probe_failure(error)["ssl_reason"], "unknown")
        self.assertNotIn("private", str(classify_probe_failure(error)))

    def test_non_ssl_error_is_a_closed_kind_and_bounded_errno(self):
        self.assertEqual(classify_probe_failure(ConnectionRefusedError(111, "private")),
                         {"kind": "connection_refused", "errno": 111, "ssl_reason": None})
        self.assertEqual(classify_probe_failure(ValueError("private")),
                         {"kind": "response_rejected", "errno": None, "ssl_reason": None})

    def test_tls_words_on_separate_lines_do_not_become_a_handshake_condition(self):
        value = classify_tls_conditions(b"tls initialized\nother error unrelated\n")
        self.assertFalse(any(value.values()))
        self.assertTrue(classify_tls_conditions(b"http: TLS handshake error from synthetic peer: no certificate available")["handshake_error"])
        self.assertTrue(classify_tls_conditions(b"http: TLS handshake error from synthetic peer: no certificate available")["no_certificate_available"])

    def test_unknown_tls_log_text_is_not_returned_and_input_type_is_strict(self):
        value = classify_tls_conditions(b"private-reason and unrelated words")
        self.assertFalse(any(value.values()))
        self.assertNotIn("private-reason", str(value))
        with self.assertRaises(ValueError): classify_tls_conditions(cast(Any, "not-bytes"))

if __name__ == "__main__":
    unittest.main()
