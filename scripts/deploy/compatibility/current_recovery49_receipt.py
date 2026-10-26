"""Closed next-pair native receipt validation; no Docker, imports of the product, or native initialization."""
import hashlib
import json

from current_recovery_receipt import validate_receipt as validate_shared_receipt, validate_event_barriers, need

SCOPE = "native_current_c934_schema49_recovery_http_fixture"
SQL = "ee88da57b072243a02a1c71f8ee2c2c5a00190dc53f81bde34ca15fc70dc6685"
OLD_COMMIT = "c9341ab39a5e3133067eb579021efdd8c19b0033"
OLD_CATALOG = "4e4b537c7c42dfedc521502008f19c98095ebdd409cc43223304d379f1334f26"
NEW_CATALOG = "56054aa8ca5b2ec32f9c687ddc57ab21c4251aa9fbc31c5fc86f9a2ee45de5d8"


def digest(value):
    return hashlib.sha256(json.dumps(value, separators=(",", ":")).encode()).hexdigest()


def validate49_receipt(receipt, pins, functional, rootcase):
    need(pins["old_commit"] == OLD_COMMIT and len(pins["old_migrations"]) == 48
         and len(pins["new_migrations"]) == 49 and pins["new_migrations"][:-1] == pins["old_migrations"]
         and digest(pins["old_migrations"]) == OLD_CATALOG and digest(pins["new_migrations"]) == NEW_CATALOG
         and pins["canonical_sql_sha256"] == SQL, "exact48 to49 profile")
    validate_shared_receipt(receipt, pins, expected_scope=SCOPE, expected_sql_sha256=SQL, expected_build_count=1)
    need(rootcase["scope"] == "native_root49_attempt_data_only_not_OCI_or_binary_proof"
         and rootcase["native_root"] is True and rootcase["success"] is True and rootcase["tests"] == 1
         and rootcase["failures"] == rootcase["errors"] == rootcase["skipped"] == 0, "one actual root case")
    need(functional["scope"] == "old_c934_http_locale_on_schema48_and49_fixture_only"
         and functional["old_commit"] == OLD_COMMIT and functional["success"] is True,
         "separate exact old-binary HTTP functional evidence")
    need(functional["native_catalog_unmodified"] is True and functional["registration_unavailable_503"] is True
         and functional["registration_user_not_created"] is True and functional["TLSVerified"] is True,
         "native draft legal gate and real owned HTTPS")
    need(functional["same_seeded_account_hash_stamps_roles"] is True
         and functional["before"] == {"login": 204, "locale_before": "en-US", "version_before": 0,
                                      "locale_after": "pt-BR", "version_after": 1}
         and functional["after"] == {"login": 204, "locale_before": "pt-BR", "version_before": 1,
                                     "locale_after": "en-US", "version_after": 2}, "two actual persisted conditional operations")
    need(all(type(receipt["new49_acl"][key]) is int for key in
             ("explicit_column_grants_before", "explicit_column_grants_after", "recovery_sql_terminal_count"))
         and receipt["new49_acl"]["relation_and_explicit_column_unsafe_before"] is True
         and receipt["new49_acl"]["scope"] == "initial_fault_and_first_recovery_only"
         and receipt["new49_acl"]["explicit_column_grants_before"] == 1
         and receipt["new49_acl"]["explicit_column_grants_after"] == 0
         and receipt["new49_acl"]["recovery_sql_terminal_count"] == 1
         and receipt["new49_acl"]["recovery_sql_acknowledged"] is True
         and receipt["new49_acl"]["authenticated_sql_sha256"] == SQL
         and receipt["new49_acl"]["relation_and_column_safe_after"] is True,
         "new append-only relation and column privileges independently witnessed")
    need(receipt["new49_root_case_sha256"] == hashlib.sha256((json.dumps(rootcase, sort_keys=True) + "\n").encode()).hexdigest(),
         "exact separate root-case bytes")
    need(receipt["functional_receipt_sha256"] == hashlib.sha256((json.dumps(functional, sort_keys=True) + "\n").encode()).hexdigest(),
         "exact separate HTTP functional bytes")
    returned = receipt["retained49_next_forward_return"]
    need(returned["origin_catalog_sha256"] == NEW_CATALOG
         and returned["scope"] == "native_fixture_protocol_forward_old_app_images_not_product_cohort_publication"
         and all(returned[key] is True for key in ("finish_forward_CLI", "normal_previous_executor_return",
             "catalog49_retained", "database_backup_assets_preserved", "new_owned_ingress_retired", "prior_pointer_preserved",
             "first_events_preserved", "next_events_file_disjoint", "marker_histories_disjoint"))
         and type(returned["return_sql_writes"]) is int and returned["return_sql_writes"] == 0
         and returned["prior_markers_removed"] == 2,
         "actual new origin forward completion and no-SQL previous return")
    need(type(returned["command_trace_sha256"]) is str and len(returned["command_trace_sha256"]) == 64,
         "separate actual new-phase trace binding")
    validate_event_barriers(returned["continuity"])
    interval = returned["continuity"]
    need(interval["all_ready"] is True and interval["samples"] >= 2
         and interval["maximum_gap_seconds"] <= 3 and interval["forbidden_events"] == 0
         and interval["covers_entire_recovery"] is True, "continuous DB backup preservation for next forward/return")
    return receipt
