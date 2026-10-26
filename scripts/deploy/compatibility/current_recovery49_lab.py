#!/usr/bin/env python3
"""Only the new C934/48 -> cohort/49 disposable Actions recovery qualification.

Reuse the qualified recovery protocol and actual1ccd client. No production access,
no rebuild of the old application, no replay of the closed35 ->48 qualification.
The sole incoming provenance seam remains the common stopped local-migrator extractor.
"""
import argparse
import base64
import hashlib
import json
import os
from pathlib import Path
import platform
import re
import secrets
import ssl
import struct
import sys
import uuid

sys.dont_write_bytecode = True
sys.path.insert(0, str(Path(__file__).resolve().parent))
import current_recovery_lab as shared
from current_recovery49_receipt import SCOPE, SQL, validate49_receipt
from current_schema_lab import Browser
from old_image_identity import bind_old_image
from owned_edge_transport import LABEL as HTTPS_LABEL, select_owned_edge

need, sha, canonical, put, command = shared.need, shared.sha, shared.canonical, shared.put, shared.command
CORE = "487aa4b05fef2661b22c36be3f90345f6dc747e5628fef09e7822cc0f3ea47d8"


class VerifiedBrowser(Browser):
    def __init__(self, address, certificate):
        # Reuse request/cookie/CSRF handling; never instantiate the unverified default context.
        self.address, self.cookies = address, {}
        self.origin, self.last_status, self.phase = "https://localhost", None, "idle"
        self.context = ssl.create_default_context(cadata=certificate)
        need(self.context.check_hostname and self.context.verify_mode == ssl.CERT_REQUIRED, "verified fixture TLS")


class Native49Lab(shared.NativeLab):
    def __init__(self, args, head):
        super().__init__(args, head)
        folder = args.workflow_source / "scripts/deploy/compatibility"
        self.pins = json.loads((folder / "current49_schema_pins.json").read_bytes())
        self.native_scope, self.native_sql_sha256 = SCOPE, SQL
        self.old_oci_directory = folder / "current49-oci"
        self.fixture_entry_script = "current_recovery49_lab.py"
        self.fixture_modules = {name: folder / name for name in (
            self.fixture_entry_script, "current_recovery49_receipt.py", "current_schema_lab.py",
            "current_schema_receipt.py", "owned_edge_transport.py")}
        self.report["scope"] = SCOPE
        self.http = {"scope": "old_c934_http_locale_on_schema48_and49_fixture_only",
                     "old_commit": self.pins["old_commit"], "success": False}
        self.rootcase = json.loads(args.rootcase.read_bytes())
        self.report["new49_root_case_sha256"] = sha(args.rootcase.read_bytes())
        need(self.bundle["duora_deploy.py"] == CORE, "independently reviewed exact49 core")

    def verify_sources(self):
        super().verify_sources()
        folder = self.args.workflow_source / "scripts/deploy/compatibility"
        self.report["source"]["next_pair_modules_sha256"] = {
            name: sha(path.read_bytes()) for name, path in self.fixture_modules.items()}
        self.report["source"]["profile_sha256"] = sha((folder / "current49_schema_pins.json").read_bytes())
        self.report["source"]["http_recipe_sha256"] = sha((folder / "current49_http_recipe.json").read_bytes())
        need(self.report["source"]["http_recipe_sha256"] == "44afbc61ac67b019f948c3ccef2d71e6f48732fa223a84f36040108cfa99ed68", "exact author HTTP recipe")

    def preflight_handoff(self):
        # No previous functional lab/handoff is executed again: these are the five actual C934 leaf references.
        self.report["old_images"] = {}
        for role, pin in self.pins["old_images"].items():
            self.docker("pull", "--quiet", "--platform", "linux/amd64", pin["reference"], timeout=300)
            self.report["old_images"][role] = bind_old_image(
                (self.old_oci_directory / (role + ".json")).read_bytes(), self.inspect("image", pin["reference"]),
                pin, role=role, revision=self.pins["old_commit"])
        self.keeper = "duora49-recovery-keeper-" + self.nonce
        self.docker("build", "--quiet", "--target", "migrator", "--build-arg", "GIT_COMMIT=" + self.pins["cohort_commit"],
                    "--label", shared.LABEL + "=" + self.nonce, "--tag", self.keeper,
                    "--file", self.args.cohort / "infra/images/dotnet/Dockerfile", self.args.cohort, timeout=900)
        image = self.inspect("image", self.keeper)
        need(re.fullmatch(r"sha256:[a-f0-9]{64}", image["Id"])
             and image["Config"]["Labels"].get(shared.LABEL) == self.nonce, "one owned new migrator build")
        self.migrator = image["Id"]
        self.report["new_migrator"] = {"docker_image_id": self.migrator, "source_head": self.pins["cohort_commit"],
                                        "build_count_this_driver": 1}

    def manifest(self, *args, **kwargs):
        value = super().manifest(*args, **kwargs)
        value["schema"]["min"] = value["migrations"][-1]
        return value

    def configure_fixture_compose(self, rendered):
        edge = rendered["services"]["edge"]
        # Use the exact C934 embedded Caddyfile and application; remove only our own inherited HTTP-stub mount.
        mounts = edge["volumes"]
        own = [v for v in mounts if v.get("target") == "/etc/caddy/Caddyfile"]
        need(len(own) == 1 and own[0]["source"] == str(self.root / "Caddyfile"), "own fixture mount only")
        edge["volumes"] = [v for v in mounts if v not in own]
        edge["environment"].update(DUORA_DOMAIN="localhost", DUORA_TLS="internal")
        edge["labels"][HTTPS_LABEL] = self.nonce
        for service in ("web", "worker"):
            rendered["services"][service]["environment"]["Duora__PublicOrigin"] = "https://localhost"
        rendered["networks"]["edge"].update(internal=True, enable_ipv6=False)
        ipam = rendered["networks"]["edge"]["ipam"]["config"]
        need(len(ipam) == 1 and ipam[0] == {"subnet": "172.30.10.0/24"}, "exact own fixture IPv4 subnet")
        # Explicit own gateway required by the strict selector; verify it from real IPAM without a fallback.
        ipam[0]["gateway"] = "172.30.10.1"
        rendered["networks"]["edge"]["labels"][HTTPS_LABEL] = self.nonce

    def browser(self):
        edge = self.inspect("container", "duora-edge-1")
        network_names = list(edge["NetworkSettings"]["Networks"])
        need(len(network_names) == 1, "owned single edge network")
        network = self.inspect("network", network_names[0])
        # Only bounded boolean schema observations of already inspected OWN fixture metadata; no alternate endpoint or fallback.
        endpoint_data = edge["NetworkSettings"]["Networks"][network_names[0]]
        ipam = network.get("IPAM", {}).get("Config", [])
        member = network.get("Containers", {}).get(edge["Id"], {})
        self.report["edge_transport_schema"] = {
            "network_required_keys": {key: key in network for key in ("Id", "Name", "Driver", "Internal", "EnableIPv6", "Labels", "IPAM", "Containers")},
            "endpoint_required_keys": {key: key in endpoint_data for key in ("NetworkID", "IPAddress")},
            "one_ipam_config": type(ipam) is list and len(ipam) == 1,
            "ipam_required_keys": {key: type(ipam) is list and len(ipam) == 1 and key in ipam[0] for key in ("Subnet", "Gateway")},
            "member_required_keys": {key: key in member for key in ("Name", "IPv4Address")}}
        endpoint = select_owned_edge(edge, network, nonce=self.nonce, name="duora-edge-1", network_name=network_names[0],
                                    reference=self.current.images["edge"], image_id=self.report["old_images"]["edge"]["docker_image_id"])
        cert = self.docker("exec", edge["Id"], "cat", "/data/caddy/pki/authorities/local/root.crt")
        need(0 < len(cert) <= 16384 and cert.startswith("-----BEGIN CERTIFICATE-----"), "public own Caddy CA only")
        self.http["transport"] = endpoint
        return VerifiedBrowser(endpoint["address"], cert)

    def identity_fields(self):
        # Transient fixture credential data stays in memory; only equality is ever emitted.
        return self.sql("select json_build_array(password_hash,security_stamp,concurrency_stamp,"
                        "(select array_agg(r.name order by r.name) from identity.user_roles ur join identity.roles r on r.id=ur.role_id "
                        "where ur.user_id=u.id)) from identity.users u where id='" + self.user + "';")

    def before_native_recovery(self):
        self.report["stage"] = "native49_owned_account_and_verified_https48"
        self.user, self.email = str(uuid.uuid4()), "native-" + self.nonce + "@example.test"
        self.password = "Native!" + secrets.token_hex(20)
        salt = secrets.token_bytes(16)
        key = hashlib.pbkdf2_hmac("sha512", self.password.encode(), salt, 100000, 32)
        encoded = base64.b64encode(b"\x01" + struct.pack(">III", 2, 100000, 16) + salt + key).decode("ascii")
        stamp, concurrency = secrets.token_hex(16), secrets.token_hex(16)
        need(all(re.fullmatch(r"[a-zA-Z0-9@.!+/_=-]+", s) for s in (self.email, encoded, stamp, concurrency)), "bounded generated SQL values")
        self.sql_file("insert into identity.users (id,user_name,normalized_user_name,email,normalized_email,email_confirmed,password_hash,"
                      "security_stamp,concurrency_stamp,phone_number_confirmed,two_factor_enabled,lockout_enabled,access_failed_count,"
                      "lockout_end,status,created_at,temporary_password_expires_at,preferred_locale,locale_version) values ('" + self.user + "','" + self.email + "',upper('" + self.email + "'),'" + self.email + "',upper('" + self.email + "'),true,'" + encoded + "','" + stamp + "','" + concurrency + "',false,false,true,0,null,'PendingEligibility',now(),null,'en-US',0);\n"
                      "insert into identity.user_roles(user_id,role_id) select '" + self.user + "',id from identity.roles where name='participant';\n")
        need(self.sql("select count(*) from identity.user_roles where user_id='" + self.user + "';") == "1", "one synthetic participant role")
        self.initial_identity = self.identity_fields()
        self.registration_negative()
        self.http["before"] = self.exercise_locale("en-US", "pt-BR", 0)

    def registration_negative(self):
        browser = self.browser()
        status, _, raw = browser.request("GET", "/api/v1/legal-documents")
        need(status == 200, "real native legal catalog")
        catalog = json.loads(raw)
        docs = catalog["documents"]
        need(catalog["registrationAvailable"] is False and len(docs) == 4
             and all(d["published"] is False and d["controllerName"] is None for d in docs), "native unapproved embedded catalog retained")
        pt = {d["document"]: d for d in docs if d["locale"] == "pt-BR"}
        email = "registration-negative-" + self.nonce + "@example.test"
        browser.csrf()
        status, _, raw = browser.request("POST", "/api/v1/auth/register", {
            "email": email, "password": self.password, "acceptedTermsVersion": catalog["termsVersion"],
            "acceptedPrivacyVersion": catalog["privacyVersion"], "acceptedTermsLocale": "pt-BR", "acceptedPrivacyLocale": "pt-BR",
            "acceptedTermsSha256": pt["terms"]["sha256"], "acceptedPrivacySha256": pt["privacy"]["sha256"]})
        need(status == 503 and json.loads(raw).get("code") == "TEMPORARILY_UNAVAILABLE", "native unavailable registration must refuse")
        need(self.sql("select count(*) from identity.users where email='" + email + "';") == "0", "registration did not create user")
        self.http.update(native_catalog_unmodified=True, registration_unavailable_503=True, registration_user_not_created=True)

    def exercise_locale(self, before, after, version):
        browser = self.browser()
        browser.csrf()
        status, _, _ = browser.request("POST", "/api/v1/auth/login", {"email": self.email, "password": self.password})
        need(status == 204, "native seeded participant login")
        status, _, raw = browser.request("GET", "/api/v1/me")
        need(status == 200, "session private-proof bootstrap")
        account = json.loads(raw)
        need(account["id"] == self.user and account["emailConfirmed"] is True and account["roles"] == ["participant"], "same ordinary participant")
        token = account["sessionContext"]["requestToken"]
        need(type(token) is str and 0 < len(token) <= 4096, "actual private operation proof")
        status, headers, raw = browser.request("GET", "/api/v1/me/locale", extra={"X-XSRF-TOKEN": token})
        body = json.loads(raw)
        need(status == 200 and body["locale"] == before and str(body["version"]) == str(version)
             and headers.get("etag") == '"' + str(version) + '"', "persisted old-binary locale and exact version")
        status, headers, raw = browser.request("PUT", "/api/v1/me/locale", {"locale": after},
                                              {"X-XSRF-TOKEN": token, "If-Match": headers["etag"]})
        body = json.loads(raw)
        need(status == 200 and body["locale"] == after and str(body["version"]) == str(version + 1)
             and headers.get("etag") == '"' + str(version + 1) + '"', "native conditional locale mutation")
        need(self.sql("select preferred_locale||':'||locale_version from identity.users where id='" + self.user + "';")
             == after + ":" + str(version + 1), "actual database readback")
        return {"login": 204, "locale_before": before, "version_before": version, "locale_after": after, "version_after": version + 1}

    def new_acl(self):
        return self.sql("select json_build_array(has_table_privilege('duora_app','safety.photo_review_appeal_decision_commands','UPDATE'),"
                        "has_table_privilege('duora_app','safety.photo_review_appeal_decision_commands','DELETE'),"
                        "has_table_privilege('duora_app','safety.photo_review_appeal_decision_commands','TRUNCATE'),"
                        "has_column_privilege('duora_app','safety.photo_review_appeal_decision_commands','appeal_id','UPDATE'));")

    def after_acknowledged_migration(self, incoming_sql):
        # Adversarial ACL only on the new own disposable append-only relation; no PUBLIC/inherited-role workaround.
        self.sql_file("grant update,delete,truncate on safety.photo_review_appeal_decision_commands to duora_app;\n"
                      "grant update(appeal_id) on safety.photo_review_appeal_decision_commands to duora_app;\n")
        need(json.loads(self.new_acl()) == [True, True, True, True], "positive unsafe effective relation and column ACL")
        # Unlike information_schema.column_privileges, attacl distinguishes the actual explicit column grant.
        need(self.sql("select count(*) from pg_attribute a join pg_class c on c.oid=a.attrelid "
                      "join pg_namespace n on n.oid=c.relnamespace cross join lateral aclexplode(a.attacl) x "
                      "where n.nspname='safety' and c.relname='photo_review_appeal_decision_commands' "
                      "and a.attname='appeal_id' and x.grantee=(select oid from pg_roles where rolname='duora_app') "
                      "and x.privilege_type='UPDATE';") == "1", "explicit adversarial column privilege exists")
        need(sha(incoming_sql.encode()) == SQL, "same actually extracted native SQL49")
        # The exact payload deliberately refuses column ACL drift atomically; do not weaken it to repair this fault.
        result = command(self.compose + ["exec", "--no-TTY", "postgres", "psql", "--no-psqlrc", "-v", "ON_ERROR_STOP=1",
                         "--single-transaction", "--username", "postgres", "--dbname", "duora", "--file", "/dev/stdin"],
                         text=incoming_sql, checked=False)
        need(result.returncode != 0 and "duora_app may not rewrite photo review history" in result.stderr
             and json.loads(self.new_acl()) == [True] * 4, "positive exact payload column-drift refusal and transaction rollback")
        # Undo only the explicit column fault introduced above by this fixture; keep table faults for real recovery.
        self.sql_file("revoke update(appeal_id) on safety.photo_review_appeal_decision_commands from duora_app;\n")
        need(json.loads(self.new_acl()) == [True] * 4, "relation faults remain for native recovery")
        self.report["new49_acl"] = {"relation_and_explicit_column_unsafe_before": True,
            "explicit_column_payload_refused_atomically": True, "only_owned_column_fault_removed_by_fixture": True,
            "relation_and_column_safe_after": False}

    def after_native_control(self):
        self.report["stage"] = "native49_verified_https_and_effective_acl_after_recovery"
        need(json.loads(self.new_acl()) == [False, False, False, False], "effective new49 ACL safe after exact payload")
        self.report["new49_acl"]["relation_and_column_safe_after"] = True
        self.http["after"] = self.exercise_locale("pt-BR", "en-US", 1)
        need(self.identity_fields() == self.initial_identity, "hash stamps and role unchanged between phases")
        self.http.update(same_seeded_account_hash_stamps_roles=True, TLSVerified=True, success=True)
        raw = (json.dumps(self.http, sort_keys=True) + "\n").encode()
        put(self.args.output.with_name("functional49-receipt.json"), raw, 0o644)
        self.report["functional_receipt_sha256"] = sha(raw)

    def execute(self):
        super().execute()
        self.report["stage"] = "native49_retained_origin_next_forward_and_previous_return"
        self.qualify_retained_origin_and_return()

    def qualify_retained_origin_and_return(self):
        # New state only. The earlier completed recovery/archive/control proof stays immutable.
        trace_start = len(self.trace.read_bytes())
        before_db, before_runtime, before_assets = self.database(), self.executor.runtime_recheck(self.current, self.paths)["profile"], self.assets()
        continuity = shared.Continuity(self)
        self.continuity = continuity
        continuity.start()
        with self.executor.recovery_administrative_locks(self.paths):
            # Fresh, exact fixture ingress for the NEXT attempt. Never overwrite the first manifest/receipt/archive.
            old_pointer = (self.state / "current-v2-compatibility.json").read_bytes()
            original_pin_raw = self.executor._recovery_pin_bytes(self.original_pin)
            need(self.pinpath.read_bytes() == original_pin_raw, "first control really restored the original PIN")
            next_raw = self.manifest("native-next-" + self.nonce[:12], self.pins["cohort_commit"], self.pins["new_migrations"],
                                     previous=self.current.release_id, incoming=True)
            self.incoming = self.executor.parse_manifest(json.dumps(next_raw), image_prefix="ghcr.io/bipoxdxd/duora-")
            next_manifest = Path("/opt/duora/releases") / (self.incoming.release_id + ".json")
            put(next_manifest, canonical(next_raw))
            pin = dict(self.original_pin, manifest=str(next_manifest), manifest_sha256=sha(next_manifest.read_bytes()))
            next_pin_raw = self.executor._recovery_pin_bytes(pin)
            self.pinpath.write_bytes(next_pin_raw)
            ingress = {Path("/var/lib/coolify-release/pending.json"): canonical({"project": "duora", "commit": self.incoming.commit, "status": "importing"}),
                       Path("/var/lib/coolify-release/duora/pending.json"): canonical({"release": {"project": "duora", "commit": self.incoming.commit, "duora": next_raw},
                           "deployment_uuid": "fixture-next-forward-job-" + self.nonce[:12]})}
            for path, raw in ingress.items():
                put(path, raw)
            context = self.executor.observe_current_v2_recovery(self.incoming, self.paths)
            origin = self.executor._recovery_expanded_origin(self.current, context, self.paths, run=self.executor.run_command)
            need(origin["catalogSha256"] == self.executor.RECOVERY_CATALOG_49_SHA256, "actual retained49 archive origin")
            incoming_sql = self.executor._payload.extract_sql(self.incoming.images["migrator"], revision=self.incoming.commit, architecture="amd64")
            store = self.executor.arm_current_v2_deploy(self.incoming, self.paths, incoming_sql, run=self.executor.run_command)
            need(store is not None and store.load()["expandedOrigin"] == origin, "next attempt records actual immutable origin49")
            assert store is not None
            # Execute the real bounded forward producers with the same already-built incoming migrator.
            # This is a fixture containing old application images, NOT product publication of the cohort application.
            env = self.root / "incoming-local-migrator.env"
            compose = self.executor._compose_command(self.paths, self.state / "releases" / (self.current.release_id + ".env")) + ["--env-file", str(env)]
            producers = (("worker-stop", compose + ["stop", "worker"], None),
                         ("migration", compose + ["--profile", "migrate", "run", "--rm", "--no-deps", "migrator"], None),
                         ("privileges", compose + ["exec", "--no-TTY", "postgres", "psql", "--no-psqlrc", "-v", "ON_ERROR_STOP=1", "--single-transaction", "--username", "postgres", "--dbname", "duora", "--file", "/dev/stdin"], incoming_sql),
                         ("application-up", compose + ["up", "--detach", "--wait", "--no-deps", "web", "worker", "edge"], None))
            for name, argv, text in producers:
                result = self.executor._run_current_v2_operation(store, name, argv, self.executor.run_command, input_text=text)
                need(result.returncode == 0, "positive next-forward producer acknowledgement")
            self.executor._wait_ready(compose, self.executor.run_command, 60, shared.time.sleep)
            self.executor._record_release(self.incoming, self.state)
            self.executor._mark_current(self.incoming, self.state)
            store.require_terminal_operations()
            # Fixture ingress controller acknowledges its OWN exact fully recorded frame. This is not a receiver publication receipt.
            for path, expected in ingress.items():
                info = path.lstat()
                need(shared.stat.S_ISREG(info.st_mode) and info.st_uid == info.st_gid == 0 and info.st_nlink == 1
                     and shared.stat.S_IMODE(info.st_mode) == 0o600 and path.read_bytes() == expected, "only exact own next ingress may retire")
                path.unlink()
                descriptor = os.open(path.parent, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
                try:
                    os.fsync(descriptor)
                finally:
                    os.close(descriptor)
            need((self.state / "current-v2-compatibility.json").read_bytes() == old_pointer, "first immutable recovery pointer preserved")
        incoming_manifest = self.root / "incoming-for-completion.json"
        put(incoming_manifest, canonical(self.incoming.raw))
        args = [str(shared.ENTRIES[0]), "--manifest", str(incoming_manifest), "--compose-file", str(self.compose_path), "--finish-current-v2-forward"]
        preview = command(args + ["--preflight-only"], timeout=300)
        projection = json.loads(preview.stdout.splitlines()[-1])
        need(projection["status"] == "preflight" and projection["complete"] is False
             and re.fullmatch(r"[a-f0-9]{64}", projection["forwardOutcomeSha256"]), "actual readonly finish-forward projection")
        finished = command(args, timeout=300)
        outcome = json.loads(finished.stdout.splitlines()[-1])
        need(outcome["status"] == "forward-recorded" and sha(canonical(outcome)) == projection["forwardOutcomeSha256"],
             "actual finish-forward outcome binds the readonly projection")
        need(self.pinpath.read_bytes() == next_pin_raw, "only the own next ingress PIN can be restored")
        self.pinpath.write_bytes(original_pin_raw)
        before_trace = self.trace_counts()
        # Real normal executor entry for the recorded previous application. No source/SQL/daemon result stubs.
        command([str(shared.ENTRIES[0]), "--manifest", str(self.current_manifest), "--compose-file", str(self.compose_path)], timeout=600)
        after_trace = self.trace_counts()
        need(after_trace["sql_writes"] == before_trace["sql_writes"], "normal previous return must execute no SQL")
        need(self.history() == self.pins["new_migrations"] and json.loads(self.new_acl()) == [False] * 4, "retained49 and ACL unchanged on return")
        need(self.executor._recovery_recorded_current(self.paths).raw == self.current.raw, "actual previous application selected")
        need(self.database() == before_db and self.executor.runtime_recheck(self.current, self.paths)["profile"] == before_runtime and self.assets() == before_assets, "DB backup mounts assets preserved across new forward/return")
        continuity.stop(complete=True)
        evidence = continuity.receipt({before_db["id"], before_runtime["services"]["backup"]["id"]})
        self.continuity = None
        next_trace = self.trace.read_bytes()[trace_start:]
        need(0 < len(next_trace) <= shared.BOUND, "bounded actual next-phase command trace")
        put(self.args.output.with_name("recovery49-next-command-trace.ndjson"), next_trace, 0o644)
        self.report["retained49_next_forward_return"] = {"origin_catalog_sha256": origin["catalogSha256"],
            "origin_compatibility_sha256": origin["compatibilitySha256"], "new_owned_ingress_retired": True,
            "prior_pointer_preserved": (self.state / "current-v2-compatibility.json").read_bytes() == old_pointer, "finish_forward_CLI": True,
            "normal_previous_executor_return": True, "return_sql_writes": 0, "catalog49_retained": True,
            "database_backup_assets_preserved": True, "continuity": evidence,
            "command_trace_sha256": sha(next_trace),
            "scope": "native_fixture_protocol_forward_old_app_images_not_product_cohort_publication"}


def fixture_entry():
    # Validate copied wrapper modules before using the common immutable root entry.
    need(sys.platform == "linux" and os.getuid() == os.geteuid() == 0, "native root entry only")
    data = json.loads(Path("/opt/duora/native-entry.json").read_bytes())
    expected = {"current_recovery49_lab.py", "current_recovery49_receipt.py", "current_schema_lab.py", "current_schema_receipt.py", "owned_edge_transport.py"}
    need(set(data["fixture_modules_sha256"]) == expected, "exact wrapper module inventory")
    for name, digest in data["fixture_modules_sha256"].items():
        need(sha(Path("/opt/duora", name).read_bytes()) == digest, "copied wrapper drift")
    return shared.fixture_entry()


def main():
    if len(sys.argv) > 1 and sys.argv[1] == "--fixture-entry":
        return fixture_entry()
    parser = argparse.ArgumentParser()
    for name in ("cohort", "old-source", "workflow-source", "rootcase", "output"):
        parser.add_argument("--" + name, type=Path, required=True)
    args = parser.parse_args()
    head = shared.require_actions()
    os.umask(0o077)
    temporary = Path(os.environ["RUNNER_TEMP"]).resolve()
    need(args.output.resolve().is_relative_to(temporary) and not args.output.exists()
         and args.rootcase.resolve().is_relative_to(temporary), "new owned Actions output only")
    os.environ["GIT_NO_LAZY_FETCH"] = "1"
    lab = Native49Lab(args, head)
    r = lab.report
    r["runtime"] = {"system": platform.system(), "architecture": platform.machine(), "uid": os.geteuid(), "github_hosted": True, "python": "3.12"}
    r["run"] = {"id": os.environ["GITHUB_RUN_ID"], "attempt": os.environ["GITHUB_RUN_ATTEMPT"], "pull_request": 260,
                "workflow_sha": os.environ["GITHUB_WORKFLOW_SHA"], "merge_sha": os.environ["GITHUB_SHA"], "workflow_head": head}
    try:
        lab.execute()
        r["success"] = True
    except BaseException as error:
        r.update(success=False, failure_type=type(error).__name__, failure_stage=r.get("stage", "source_preflight"),
                 failure_locations=shared.failure_locations(error))
        if isinstance(error, KeyError) and error.args and error.args[0] in {"Gateway", "Subnet", "Containers", "Name", "IPv4Address", "IPAddress", "NetworkID", "Id", "Labels", "Config", "Image", "State", "Running", "RestartCount", "OOMKilled", "Status", "PortBindings", "EnableIPv6", "Driver", "Internal", "IPAM"}:
            r["missing_fixture_schema_key"] = error.args[0]
    finally:
        r["cleanup_successful"] = lab.cleanup()
    try:
        validate49_receipt(r, lab.pins, lab.http, lab.rootcase)
        code = 0
    except (ValueError, KeyError, TypeError):
        r["success"] = False
        code = 1
    raw = canonical(r)
    need(len(raw) <= 131072, "public49 receipt bound")
    args.output.parent.mkdir(parents=True, exist_ok=True)
    put(args.output, raw, 0o644)
    print(json.dumps({"scope": SCOPE, "success": r["success"], "production_acceptance": False}))
    return code


if __name__ == "__main__":
    sys.exit(main())
