"""Bounded B2 metadata inventory for an explicitly coordinated operator69 window.

Not a journal adapter, restore gate, permission change or background monitor.
The existing credential and requested scope enter a pipe FD, remain in RAM and
are never included in argv, environment, output, files or exception messages.
No object bodies are downloaded. Only authorize/list-bucket/list-versions exist.
Primary protocol: Backblaze b2_authorize_account, b2_list_buckets and
b2_list_file_versions v4 documentation (reviewed 2026-10-25).
"""

import base64
import hashlib
import http.client
import json
import os
from pathlib import Path
import re
import resource
import signal
import ssl
import stat
import sys
import time
from urllib.parse import urlsplit


MAX_INPUT = 16 * 1024
MAX_RESPONSE = 1024 * 1024
MAX_OUTPUT = 8192
MAX_PAGES = 3
PAGE_SIZE = 100
MAX_SECONDS = 60
AUTHORIZE = "https://api.backblazeb2.com/b2api/v4/b2_authorize_account"
OPERATIONS = {"b2_authorize_account": "GET", "b2_list_buckets": "POST", "b2_list_file_versions": "POST"}


class InventoryError(ValueError):
    """The fixed code is safe to publish; never attach remote bodies or identifiers."""


def _text(value, maximum=512):
    if not isinstance(value, str) or not value or len(value) > maximum or any(ord(c) < 32 for c in value):
        raise InventoryError("INVALID_METADATA")
    return value


def _number(value):
    if isinstance(value, str) and value.isascii() and value.isdigit() and len(value) <= 20:
        value = int(value)
    if type(value) is not int or not 0 <= value <= 2**63 - 1:
        raise InventoryError("INVALID_METADATA")
    return value


def request_metadata(method, url, authorization, body):
    """HTTPS only, fixed B2 API operations, no proxy environment, redirects or retry."""
    parsed = urlsplit(url)
    host = parsed.hostname or ""
    operation = parsed.path.removeprefix("/b2api/v4/")
    if (parsed.scheme != "https" or parsed.port is not None or parsed.username is not None
            or parsed.password is not None or parsed.query or parsed.fragment
            or parsed.path != "/b2api/v4/" + operation or OPERATIONS.get(operation) != method
            or not re.fullmatch(r"api(?:[0-9]{3})?\.backblazeb2\.com", host)
            or (operation == "b2_authorize_account" and url != AUTHORIZE)
            or (operation != "b2_authorize_account" and not re.fullmatch(r"api[0-9]{3}\.backblazeb2\.com", host))):
        raise InventoryError("DESTINATION_REFUSED")
    connection = http.client.HTTPSConnection(host, timeout=10, context=ssl.create_default_context())
    try:
        encoded = None if body is None else json.dumps(body, separators=(",", ":")).encode("utf-8")
        connection.request(method, parsed.path, body=encoded,
            headers={"Authorization": authorization, "Content-Type": "application/json", "Accept": "application/json"})
        response = connection.getresponse()
        if response.status != 200:
            raise InventoryError("REMOTE_READ_UNAVAILABLE")
        chunks = bytearray()
        while True:
            chunk = response.read1(min(65536, MAX_RESPONSE + 1 - len(chunks)))
            if not chunk:
                break
            chunks.extend(chunk)
            if len(chunks) > MAX_RESPONSE:
                raise InventoryError("RESPONSE_BUDGET_EXCEEDED")
        result = json.loads(chunks)
        if not isinstance(result, dict):
            raise InventoryError("INVALID_METADATA")
        return result
    finally:
        connection.close()


def collect_inventory(inputs, request=request_metadata, now_ms=None):
    """Return sanitized scope/count facts only; even a complete listing is not restore admission."""
    if not isinstance(inputs, dict):
        raise InventoryError("INVALID_INPUT")
    if set(inputs) != {"keyId", "applicationKey", "bucketId", "prefix", "minimumRetentionDays"}:
        raise InventoryError("INVALID_INPUT")
    key_id = _text(inputs["keyId"])
    key = _text(inputs["applicationKey"])
    bucket = _text(inputs["bucketId"], 64)
    prefix = _text(inputs["prefix"], 256)
    required_days = _number(inputs["minimumRetentionDays"])
    if (not re.fullmatch(r"[A-Za-z0-9]{6,64}", bucket)
            or not re.fullmatch(r"[A-Za-z0-9_-]+(?:/[A-Za-z0-9_-]+)*/", prefix)
            or not 365 <= required_days <= 3650):
        raise InventoryError("INVALID_SCOPE")
    now_ms = int(time.time() * 1000) if now_ms is None else _number(now_ms)
    basic = "Basic " + base64.b64encode((key_id + ":" + key).encode()).decode("ascii")
    auth = request("GET", AUTHORIZE, basic, None)
    account = _text(auth["accountId"])
    token = _text(auth["authorizationToken"], 4096)
    storage = auth["apiInfo"]["storageApi"]
    api = _text(storage["apiUrl"])
    if not re.fullmatch(r"https://api[0-9]{3}\.backblazeb2\.com", api):
        raise InventoryError("DESTINATION_REFUSED")
    allowed = storage["allowed"]
    capabilities = allowed["capabilities"]
    if not isinstance(capabilities, list) or any(not isinstance(c, str) for c in capabilities):
        raise InventoryError("INVALID_METADATA")
    buckets = allowed.get("buckets")
    restriction = allowed.get("namePrefix")
    bucket_allowed = buckets is None or (isinstance(buckets, list) and any(b.get("id") == bucket for b in buckets))
    prefix_allowed = restriction is None or (isinstance(restriction, str) and prefix.startswith(restriction))
    if not bucket_allowed or not prefix_allowed or not {"listBuckets", "listFiles"}.issubset(capabilities):
        raise InventoryError("READ_SCOPE_UNAVAILABLE")
    result: dict = {"provider": "BackblazeB2", "status": "METADATA_ONLY", "requests": 1,
        "scopeBindingSha256": hashlib.sha256((bucket + "\0" + prefix).encode()).hexdigest(),
        "keyExactlyScoped": isinstance(buckets, list) and len(buckets) == 1 and buckets[0].get("id") == bucket and restriction == prefix,
        "deleteFilesAbsent": "deleteFiles" not in capabilities,
        "canReadBucketRetention": "readBucketRetentions" in capabilities,
        "canReadFileRetention": "readFileRetentions" in capabilities,
        "minimumRetentionDaysRequested": required_days, "pages": 0, "uploadVersions": 0, "hideMarkers": 0,
        "otherEntries": 0, "versionsCurrentlyCoveringUploadPlusRequestedDays": 0, "currentlyLockedVersions": 0,
        "coverageProvesRetentionSinceUpload": False, "observedNonComplianceVersions": 0,
        "retentionUnknownVersions": 0, "listingProtocolExhausted": False, "providerQualified": False,
        "restoreAdmission": False, "objectsReadOrWritten": 0, "permissionsChanged": False}
    response = request("POST", api + "/b2api/v4/b2_list_buckets", token, {"accountId": account, "bucketId": bucket})
    result["requests"] += 1
    found = response["buckets"]
    if not isinstance(found, list) or len(found) != 1 or found[0].get("bucketId") != bucket:
        raise InventoryError("BUCKET_SCOPE_UNCONFIRMED")
    info = found[0]
    bucket_type = info.get("bucketType")
    result["privateBucket"] = True if bucket_type == "allPrivate" else False if bucket_type == "allPublic" else None
    lock = info.get("fileLockConfiguration", {})
    visible = lock.get("isClientAuthorizedToRead") is True and isinstance(lock.get("value"), dict)
    result["fileLockMetadataVisible"] = visible
    lock_value = lock["value"] if visible else {}
    enabled = lock_value.get("isFileLockEnabled") if visible else None
    if enabled is not None and type(enabled) is not bool:
        raise InventoryError("INVALID_METADATA")
    result["fileLockEnabled"] = enabled
    retention = lock_value.get("defaultRetention") or {}
    mode = retention.get("mode")
    result["defaultComplianceRetention"] = (mode == "compliance") if visible and "mode" in retention and mode in {None, "compliance", "governance"} else None
    period = retention.get("period") or {}
    duration = period.get("duration")
    default_days = None
    if type(duration) is int and duration > 0 and period.get("unit") in {"days", "years"}:
        default_days = duration * (365 if period["unit"] == "years" else 1)
    result["defaultRetentionMeetsRequestedDays"] = (default_days >= required_days) if default_days is not None else None
    rules = info.get("lifecycleRules")
    if not isinstance(rules, list):
        raise InventoryError("LIFECYCLE_UNAVAILABLE")
    relevant = [r for r in rules if isinstance(r, dict) and isinstance(r.get("fileNamePrefix"), str)
        and (prefix.startswith(r["fileNamePrefix"]) or r["fileNamePrefix"].startswith(prefix))]
    interpretable = all(isinstance(r, dict) and isinstance(r.get("fileNamePrefix"), str) for r in rules)
    interpretable = interpretable and all("daysFromHidingToDeleting" in r and (r["daysFromHidingToDeleting"] is None
        or (type(r["daysFromHidingToDeleting"]) is int and r["daysFromHidingToDeleting"] >= 0)) for r in relevant)
    short_lifecycle = any(type(r.get("daysFromHidingToDeleting")) is int
        and r["daysFromHidingToDeleting"] < required_days for r in relevant)
    result["lifecycleRulesInterpretable"] = interpretable
    result["potentiallyShortLifecycle"] = True if short_lifecycle else False if interpretable else None
    cursor = None
    seen_cursors = set()
    seen_versions = set()
    for _ in range(MAX_PAGES):
        payload = {"bucketId": bucket, "prefix": prefix, "maxFileCount": PAGE_SIZE}
        if cursor is not None:
            payload.update(startFileName=cursor[0], startFileId=cursor[1])
        page = request("POST", api + "/b2api/v4/b2_list_file_versions", token, payload)
        result["requests"] += 1
        result["pages"] += 1
        files = page["files"]
        if not isinstance(files, list) or len(files) > PAGE_SIZE:
            raise InventoryError("LISTING_BUDGET_EXCEEDED")
        for item in files:
            if not isinstance(item, dict) or not _text(item["fileName"], 1024).startswith(prefix) or item.get("bucketId") != bucket:
                raise InventoryError("FOREIGN_LISTING_ENTRY")
            identity = (item["fileName"], _text(item["fileId"], 512))
            if identity in seen_versions:
                raise InventoryError("REPEATED_LISTING_ENTRY")
            seen_versions.add(identity)
            action = item.get("action")
            if action == "hide":
                result["hideMarkers"] += 1
            elif action == "upload":
                result["uploadVersions"] += 1
                uploaded = _number(item["uploadTimestamp"])
                protected = item.get("fileRetention", {})
                value = protected.get("value")
                if (protected.get("isClientAuthorizedToRead") is True and isinstance(value, dict)
                        and value.get("mode") == "compliance" and value.get("retainUntilTimestamp") is not None):
                    until = _number(value["retainUntilTimestamp"])
                    result["versionsCurrentlyCoveringUploadPlusRequestedDays"] += int(until >= uploaded + required_days * 86_400_000)
                    result["currentlyLockedVersions"] += int(until > now_ms)
                elif (protected.get("isClientAuthorizedToRead") is True and isinstance(value, dict)
                        and "mode" in value and value["mode"] in {None, "governance"}):
                    result["observedNonComplianceVersions"] += 1
                else:
                    result["retentionUnknownVersions"] += 1
            else:
                result["otherEntries"] += 1
        if "nextFileName" not in page or "nextFileId" not in page:
            raise InventoryError("INVALID_LISTING_CURSOR")
        name, identifier = page["nextFileName"], page["nextFileId"]
        if name is None and identifier is None:
            result["listingProtocolExhausted"] = True
            break
        if name is None or identifier is None or not _text(name, 1024).startswith(prefix):
            raise InventoryError("INVALID_LISTING_CURSOR")
        cursor = (name, _text(identifier, 512))
        if cursor in seen_cursors:
            raise InventoryError("REPEATED_LISTING_CURSOR")
        seen_cursors.add(cursor)
    return result


def main(argv):
    # The operator's separately pinned wrapper owns host/release/lease authorization.
    if len(argv) != 2 or argv[0] != "--once-fd" or not argv[1].isascii() or not argv[1].isdigit():
        print('{"status":"UNKNOWN","code":"INVALID_INVOCATION"}')
        return 2
    raw = bytearray()
    inputs = None
    try:
        resource.setrlimit(resource.RLIMIT_CORE, (0, 0))
        def alarm(_signal, _frame):
            raise InventoryError("TIME_BUDGET_EXCEEDED")
        signal.signal(signal.SIGALRM, alarm)
        signal.setitimer(signal.ITIMER_REAL, MAX_SECONDS)
        descriptor = int(argv[1])
        if not stat.S_ISFIFO(os.fstat(descriptor).st_mode):
            raise InventoryError("INPUT_FD_REFUSED")
        with os.fdopen(os.dup(descriptor), "rb") as stream:
            raw.extend(stream.read(MAX_INPUT + 1))
        if len(raw) > MAX_INPUT:
            raise InventoryError("INPUT_BUDGET_EXCEEDED")
        inputs = json.loads(raw)
        report = collect_inventory(inputs)
        report["collectorSha256"] = hashlib.sha256(Path(__file__).read_bytes()).hexdigest()
        rendered = json.dumps(report, sort_keys=True, separators=(",", ":"))
        if len(rendered.encode("utf-8")) > MAX_OUTPUT:
            raise InventoryError("OUTPUT_BUDGET_EXCEEDED")
        print(rendered)
        return 0
    except (Exception, KeyboardInterrupt):
        # Do not render exception text: urllib, TLS, JSON, identifiers and credentials may be present there.
        print('{"status":"UNKNOWN","code":"READ_ONLY_COLLECTION_UNAVAILABLE"}')
        return 2
    finally:
        signal.setitimer(signal.ITIMER_REAL, 0)
        raw[:] = b"\0" * len(raw)
        if isinstance(inputs, dict):
            inputs.clear()


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
