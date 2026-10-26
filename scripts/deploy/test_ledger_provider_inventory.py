"""Read-only metadata contract; no B2 account, network, real credential or object is used."""

import contextlib
import copy
import io
import json
import os
import unittest
from unittest.mock import patch

import ledger_provider_inventory as inventory


class LedgerProviderInventoryTests(unittest.TestCase):
    def inputs(self):
        return {"keyId": "synthetic-key-id", "applicationKey": "SYNTHETIC-SECRET-CANARY",
            "bucketId": "0123456789abcdef01234567", "prefix": "journal/synthetic/", "minimumRetentionDays": 365}

    def auth(self):
        settings = self.inputs()
        return {"accountId": "synthetic-account", "authorizationToken": "SYNTHETIC-TOKEN-CANARY", "apiInfo": {
            "storageApi": {"apiUrl": "https://api001.backblazeb2.com", "allowed": {
                "buckets": [{"id": settings["bucketId"], "name": "SYNTHETIC-BUCKET-CANARY"}],
                "namePrefix": settings["prefix"],
                "capabilities": ["listBuckets", "listFiles", "readBucketRetentions", "readFileRetentions"]}}}}

    def bucket(self):
        return {"buckets": [{"bucketId": self.inputs()["bucketId"], "bucketName": "SYNTHETIC-BUCKET-CANARY",
            "bucketType": "allPrivate", "fileLockConfiguration": {"isClientAuthorizedToRead": True,
                "value": {"isFileLockEnabled": True, "defaultRetention": {"mode": "compliance",
                    "period": {"unit": "days", "duration": 366}}}}, "lifecycleRules": []}]}

    def page(self):
        scope = self.inputs()
        return {"files": [{"bucketId": scope["bucketId"], "fileName": scope["prefix"] + "PRIVATE-OBJECT-CANARY",
            "fileId": "PRIVATE-VERSION-CANARY", "action": "upload", "uploadTimestamp": 1000,
            "fileInfo": {"sensitive": "PRIVATE-BODY-CANARY"}, "fileRetention": {"isClientAuthorizedToRead": True,
                "value": {"mode": "compliance", "retainUntilTimestamp": 1000 + 366 * 86_400_000}}}],
            "nextFileName": None, "nextFileId": None}

    def requester(self, *, auth=None, bucket=None, pages=None):
        responses = [self.auth() if auth is None else auth, self.bucket() if bucket is None else bucket,
            *(pages if pages is not None else [self.page()])]
        calls = []
        def request(method, url, token, body):
            calls.append((method, url, body))
            return copy.deepcopy(responses[len(calls) - 1])
        return request, calls

    def test_sanitized_complete_metadata_never_qualifies_provider_or_restore(self):
        request, calls = self.requester()
        result = inventory.collect_inventory(self.inputs(), request, now_ms=2000)
        rendered = json.dumps(result)
        for marker in ["SYNTHETIC-SECRET-CANARY", "SYNTHETIC-TOKEN-CANARY", "SYNTHETIC-BUCKET-CANARY",
                "PRIVATE-OBJECT-CANARY", "PRIVATE-VERSION-CANARY", "PRIVATE-BODY-CANARY", "https://"]:
            self.assertNotIn(marker, rendered)
        self.assertEqual(len(calls), 3)
        self.assertEqual([c[0] for c in calls], ["GET", "POST", "POST"])
        self.assertEqual(calls[1][2]["bucketId"], self.inputs()["bucketId"])
        self.assertEqual(calls[2][2]["prefix"], self.inputs()["prefix"])
        self.assertTrue(result["listingProtocolExhausted"])
        self.assertTrue(result["keyExactlyScoped"])
        self.assertTrue(result["deleteFilesAbsent"])
        self.assertEqual(result["versionsCurrentlyCoveringUploadPlusRequestedDays"], 1)
        self.assertFalse(result["coverageProvesRetentionSinceUpload"])
        self.assertEqual(result["objectsReadOrWritten"], 0)
        self.assertFalse(result["providerQualified"])
        self.assertFalse(result["restoreAdmission"])

    def test_hidden_retention_metadata_is_unknown_instead_of_disabled(self):
        bucket = self.bucket()
        bucket["buckets"][0]["fileLockConfiguration"] = {"isClientAuthorizedToRead": False, "value": None}
        page = self.page()
        page["files"][0]["fileRetention"] = {"isClientAuthorizedToRead": False, "value": None}
        request, _ = self.requester(bucket=bucket, pages=[page])
        result = inventory.collect_inventory(self.inputs(), request, now_ms=2000)
        self.assertFalse(result["fileLockMetadataVisible"])
        self.assertIsNone(result["fileLockEnabled"])
        self.assertIsNone(result["defaultComplianceRetention"])
        self.assertEqual(result["retentionUnknownVersions"], 1)

    def test_remote_canary_cannot_cross_a_boolean_field_and_optional_unknowns_are_not_false(self):
        bucket = self.bucket()
        bucket["buckets"][0]["fileLockConfiguration"]["value"]["isFileLockEnabled"] = "PRIVATE-FIELD-CANARY"
        request, _ = self.requester(bucket=bucket)
        with self.assertRaises(inventory.InventoryError):
            inventory.collect_inventory(self.inputs(), request)
        bucket = self.bucket()
        bucket["buckets"][0]["bucketType"] = "PRIVATE-FIELD-CANARY"
        bucket["buckets"][0]["fileLockConfiguration"]["value"]["defaultRetention"]["mode"] = "PRIVATE-FIELD-CANARY"
        bucket["buckets"][0]["lifecycleRules"] = [{"fileNamePrefix": self.inputs()["prefix"], "daysFromHidingToDeleting": "PRIVATE-FIELD-CANARY"}]
        request, _ = self.requester(bucket=bucket)
        result = inventory.collect_inventory(self.inputs(), request)
        self.assertIsNone(result["privateBucket"])
        self.assertIsNone(result["defaultComplianceRetention"])
        self.assertIsNone(result["potentiallyShortLifecycle"])
        self.assertFalse(result["lifecycleRulesInterpretable"])
        self.assertNotIn("PRIVATE-FIELD-CANARY", json.dumps(result))

    def test_arbitrary_api_destination_or_insufficient_scope_stops_before_further_reads(self):
        for mode in ["foreign-host", "foreign-prefix", "foreign-bucket", "missing-read-capability"]:
            with self.subTest(mode=mode):
                auth = self.auth()
                storage = auth["apiInfo"]["storageApi"]
                if mode == "foreign-host":
                    storage["apiUrl"] = "https://untrusted.example.test"
                elif mode == "foreign-prefix":
                    storage["allowed"]["namePrefix"] = "another/"
                elif mode == "foreign-bucket":
                    storage["allowed"]["buckets"] = [{"id": "anotherbucket"}]
                else:
                    storage["allowed"]["capabilities"] = ["listBuckets"]
                request, calls = self.requester(auth=auth)
                with self.assertRaises(inventory.InventoryError):
                    inventory.collect_inventory(self.inputs(), request)
                self.assertEqual(len(calls), 1)

    def test_listing_budget_and_hide_markers_are_explicit_without_partial_restore_admission(self):
        pages = []
        for number in range(inventory.MAX_PAGES):
            pages.append({"files": [{"bucketId": self.inputs()["bucketId"],
                "fileName": self.inputs()["prefix"] + str(number), "fileId": str(number), "action": "hide"}],
                "nextFileName": self.inputs()["prefix"] + str(number + 1), "nextFileId": str(number + 1)})
        request, calls = self.requester(pages=pages)
        result = inventory.collect_inventory(self.inputs(), request)
        self.assertEqual(result["hideMarkers"], 3)
        self.assertEqual(len(calls), 2 + inventory.MAX_PAGES)
        self.assertFalse(result["listingProtocolExhausted"])
        self.assertFalse(result["providerQualified"])

    def test_repeated_or_incomplete_cursor_and_foreign_entries_refuse_the_entire_result(self):
        for mode in ["repeated", "incomplete", "missing-fields", "foreign-entry", "foreign-cursor"]:
            with self.subTest(mode=mode):
                page = self.page()
                if mode in {"repeated", "foreign-cursor"}:
                    page["files"] = []
                    page["nextFileName"] = (self.inputs()["prefix"] if mode == "repeated" else "foreign/") + "next"
                    page["nextFileId"] = "next-id"
                elif mode == "incomplete":
                    page["nextFileName"] = self.inputs()["prefix"] + "next"
                elif mode == "missing-fields":
                    del page["nextFileName"]
                    del page["nextFileId"]
                else:
                    page["files"][0]["fileName"] = "foreign/object"
                request, _ = self.requester(pages=[page, page, page])
                with self.assertRaises(inventory.InventoryError):
                    inventory.collect_inventory(self.inputs(), request)

    def test_transport_refuses_non_read_endpoints_redirect_hosts_and_credentials_in_urls(self):
        for url in ["https://api001.backblazeb2.com/b2api/v4/b2_delete_file_version",
                "https://api001.backblazeb2.com/b2api/v4/b2_create_bucket",
                "https://api001.backblazeb2.com.evil.test/b2api/v4/b2_list_buckets",
                "https://userinfo@api001.backblazeb2.com/b2api/v4/b2_list_buckets",
                "http://api001.backblazeb2.com/b2api/v4/b2_list_buckets",
                "https://api001.backblazeb2.com/b2api/v4/b2_list_buckets?unexpected=true"]:
            with self.subTest(url=url), patch.object(inventory.http.client, "HTTPSConnection") as network:
                with self.assertRaises(inventory.InventoryError):
                    inventory.request_metadata("POST", url, "SYNTHETIC-TOKEN-CANARY", {})
                network.assert_not_called()

    def test_cli_errors_do_not_render_remote_exception_credentials_or_arguments(self):
        descriptor, writer = os.pipe()
        try:
            os.write(writer, json.dumps(self.inputs()).encode())
            os.close(writer)
            output = io.StringIO()
            with patch.object(inventory.resource, "setrlimit"), patch.object(inventory.signal, "signal"), \
                    patch.object(inventory.signal, "setitimer"), \
                    patch.object(inventory, "collect_inventory", side_effect=RuntimeError("SYNTHETIC-SECRET-CANARY https://private.example.test")), \
                    contextlib.redirect_stdout(output):
                self.assertEqual(inventory.main(["--once-fd", str(descriptor)]), 2)
            self.assertEqual(json.loads(output.getvalue()), {"status": "UNKNOWN", "code": "READ_ONLY_COLLECTION_UNAVAILABLE"})
        finally:
            os.close(descriptor)


if __name__ == "__main__":
    unittest.main()
