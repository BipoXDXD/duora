"""Disposable Actions-only end-to-end qualification, never a production entry point."""

import hashlib
import json
import os
from pathlib import Path
import selectors
import shutil
import subprocess
import sys
import tempfile
import threading
import time
from unittest.mock import patch

import duora_deploy as executor
import duora_secrets
import install_executor as installer
import reconcile_payload as payload
import runtime_guard as guard
import smoke_local


def validate_observations(samples: list[dict], *, start: float, end: float,
                          errors: list[str], events_alive: bool) -> None:
    if errors or not events_alive or len(samples) < 2 or end <= start:
        raise RuntimeError("Continuity observer or event stream failed: " + json.dumps(
            {"errors": errors, "eventsAlive": events_alive, "samples": len(samples), "start": start, "end": end}))
    times = [sample["at"] for sample in samples]
    if (times != sorted(times) or times[0] > start + 3 or times[-1] < end - 3
            or any(right - left > 3 for left, right in zip(times, times[1:]))
            or any(not sample["database_ready"] for sample in samples)):
        raise RuntimeError("Database readiness coverage has a failure or an uncovered interval")


def command(argv: list[str]) -> str:
    result = subprocess.run(argv, text=True, capture_output=True, check=False, timeout=180)
    if result.returncode:
        raise RuntimeError("Synthetic lab command failed: " + " ".join(argv[:5]) + ": " + result.stderr[-1200:])
    return result.stdout.strip()


def _instances(compose: list[str]) -> dict:
    identifiers = command(compose + ["ps", "--all", "--quiet"]).split()
    values = json.loads(command(["docker", "inspect", *identifiers]))
    return {v["Config"]["Labels"]["com.docker.compose.service"]:
        {"id": v["Id"], "startedAt": v["State"]["StartedAt"], "restartCount": v["RestartCount"]} for v in values}


def run_preservation_lab() -> dict:
    if (os.environ.get("GITHUB_ACTIONS") != "true" or os.environ.get("RUNNER_ENVIRONMENT") != "github-hosted"
            or os.environ.get("GITHUB_REPOSITORY") != "BipoXDXD/duora"):
        raise RuntimeError("Continuity qualification is restricted to disposable hosted Actions")
    if os.geteuid() != 0:
        names = "GITHUB_ACTIONS,RUNNER_ENVIRONMENT,GITHUB_REPOSITORY,GITHUB_RUN_ID,GITHUB_RUN_ATTEMPT,GITHUB_SHA,GITHUB_EVENT_PATH,RUNNER_TEMP"
        result = subprocess.run(["sudo", "--non-interactive", "--preserve-env=" + names,
            sys.executable, str(Path(__file__).resolve()), "--actions-lab"], text=True, capture_output=True,
            check=False, timeout=1200)
        if result.returncode:
            raise RuntimeError("Disposable root lab failed: " + (result.stdout + result.stderr)[-6000:])
        line = next(line for line in reversed(result.stdout.splitlines()) if line.startswith("LAB_RESULT="))
        return json.loads(line.removeprefix("LAB_RESULT="))
    return _root_lab()


def _root_lab() -> dict:
    architecture = command(["docker", "version", "--format", "{{.Server.Arch}}"])
    if architecture not in guard.LEGACY_DIGESTS:
        raise RuntimeError("Unsupported disposable architecture")
    if command(["docker", "ps", "--all", "--quiet", "--filter", "label=com.docker.compose.project=duora"]):
        raise RuntimeError("Refusing to adopt an existing stack")
    if "duora_pgdata" in command(["docker", "volume", "ls", "--format", "{{.Name}}","--filter","name=^duora_pgdata$"]).split():
        raise RuntimeError("Refusing to adopt an existing data volume")
    root = Path(tempfile.mkdtemp(prefix="duora-qualified-actions-", dir="/root"))
    repo = Path(__file__).resolve().parents[2]
    compose_file = root / "compose.yml"
    raw_compose = (repo / "infra/compose/compose.prod.yml").read_text()
    raw_compose = raw_compose.replace("Email__PickupDirectory: /tmp/duora-mail",
        "Email__PickupDirectory: /tmp/duora-mail\n      Email__Provider: Resend\n"
        "      Email__Resend__ApiKey: re_synthetic_qualification\n      Email__Resend__From: noreply@app.example.com")
    compose_file.write_text(raw_compose)
    state = root / "state"
    state.mkdir(mode=0o700)
    (state / ".deploy.lock").touch(mode=0o600)
    secrets = root / "secrets"
    duora_secrets.init_secrets(secrets, change_owner=True)
    duora_secrets.init_backup(secrets, change_owner=True)
    for name, text in [("pgbackrest_repo.conf", "[global]\nrepo1-s3-endpoint=s3.invalid\nrepo1-s3-bucket=synthetic\n"
            "repo1-s3-region=us-east-1\nrepo1-s3-key=synthetic\nrepo1-s3-key-secret=synthetic\n"),
            ("pgbackrest_cipher.conf", "[global]\nrepo1-cipher-pass=synthetic-qualification\n")]:
        file = secrets / name
        file.write_text(text)
        file.chmod(0o400)
        os.chown(file, 999, 999)
    media = root / "media"
    for area in ("quarantine", "derivatives"):
        directory = media / area
        directory.mkdir(parents=True, mode=0o700)
        marker = directory / ".duora-media-area"
        marker.write_text("synthetic preserved asset\n")
        marker.chmod(0o400)
        os.chown(marker, 1654, 1654)
        os.chown(directory, 1654, 1654)
    host_env = root / "duora.env"
    host_env.write_text("\n".join(["DUORA_IMAGE_PREFIX=ghcr.io/bipoxdxd/duora-", "DUORA_MIN_FREE_GIB=0",
        "DUORA_DISK_CHECK_PATH=/", f"DUORA_SECRETS_DIR={secrets}", f"DUORA_MEDIA_DIR={media}",
        "DUORA_MEDIA_REVIEW=", "DUORA_DOMAIN=localhost", "DUORA_TLS=internal", "DUORA_HTTP_PORT=80",
        "DUORA_HTTPS_PORT=443", "DUORA_REGION_CUTOVER=2026-10-22T22:34:50Z"]) + "\n")
    invariant_hash = guard.digest({"compose": raw_compose, "host": host_env.read_text(),
        "assets": [(media / a / ".duora-media-area").read_text() for a in ("quarantine", "derivatives")]})
    registry_name = "duora-critical-" + root.name
    registry = "localhost:5050/duora/"
    compose = None
    session = None
    events = None
    stop = threading.Event()
    samples: list[dict] = []
    app_samples: list[dict] = []
    observer_errors: list[str] = []
    monitor = None
    app_monitor = None
    try:
        pin_fixture = root / "pin-policy"
        pin_fixture.mkdir(mode=0o700)
        pinned_json = pin_fixture / "compose.coolify.json"
        pinned_bytes = b'{"name":"synthetic-pin-policy","services":{}}\n'
        pinned_json.write_bytes(pinned_bytes)
        pinned_json.chmod(0o600)
        pin = {"compose": str(pinned_json), "compose_sha256": hashlib.sha256(pinned_bytes).hexdigest()}
        if installer.pinned_compose(pin) != pinned_json: raise RuntimeError("Actual pinned JSON path was not selected")
        for unsafe in ("parent-mode", "file-mode", "digest", "symlink"):
            candidate = dict(pin)
            link = pin_fixture / "linked.json"
            if unsafe == "parent-mode": pin_fixture.chmod(0o770)
            elif unsafe == "file-mode": pinned_json.chmod(0o620)
            elif unsafe == "digest": pinned_json.write_text("changed synthetic config")
            else:
                link.symlink_to(pinned_json)
                candidate["compose"] = str(link)
            try:
                try: installer.pinned_compose(candidate)
                except installer.InstallError: pass
                else: raise RuntimeError("Pinned JSON accepted unsafe " + unsafe)
            finally:
                pin_fixture.chmod(0o700)
                pinned_json.write_bytes(pinned_bytes)
                pinned_json.chmod(0o600)
                link.unlink(missing_ok=True)
        receiver_link = root / "receiver-policy/receiver.py"
        receiver_target = receiver_link.parent / "versions" / ("2.0.2-" + installer.RECEIVER_SHA256) / "receiver.py"
        receiver_target.parent.mkdir(parents=True)
        receiver_source = (repo / "scripts/deploy/fixtures/receiver-2.0.2-1ccd.py.txt").read_bytes()
        if hashlib.sha256(receiver_source).hexdigest() != installer.RECEIVER_SHA256:
            raise RuntimeError("Historical qualified receiver fixture differs from installed 2.0.2")
        receiver_target.write_bytes(receiver_source)
        receiver_target.chmod(0o755)
        receiver_link.symlink_to(receiver_target)
        receiver_negatives = []
        with patch.object(installer, "RECEIVER_PATH", receiver_link):
            binding = installer.receiver_binding()
            verified_receiver = installer._module(Path(binding["resolvedTarget"]), "duora_lab_versioned_receiver")
            if verified_receiver.receiver_digest() != installer.RECEIVER_SHA256:
                raise RuntimeError("Verified receiver load differs from its bound source")
            for unsafe in ("link-owner", "target-owner", "parent-owner", "parent-mode", "retarget", "relative-link", "content", "target-symlink", "hardlink"):
                other = receiver_link.parent / "unqualified.py"
                saved = receiver_target.parent / "saved-receiver.py"
                extra = receiver_target.parent / "extra-link.py"
                if unsafe == "link-owner": os.lchown(receiver_link, 65534, 65534)
                elif unsafe == "target-owner": os.chown(receiver_target, 65534, 65534)
                elif unsafe == "parent-owner": os.chown(receiver_target.parent, 65534, 65534)
                elif unsafe == "parent-mode": receiver_target.parent.chmod(0o770)
                elif unsafe in {"retarget", "relative-link"}:
                    other.write_bytes(receiver_source)
                    receiver_link.unlink()
                    target = other if unsafe == "retarget" else Path("versions") / receiver_target.parent.name / "receiver.py"
                    receiver_link.symlink_to(target)
                elif unsafe == "content": receiver_target.write_text("unqualified receiver")
                elif unsafe == "target-symlink":
                    receiver_target.rename(saved)
                    receiver_target.symlink_to(saved)
                else: os.link(receiver_target, extra)
                try:
                    try: installer.receiver_binding()
                    except installer.InstallError: receiver_negatives.append(unsafe)
                    else: raise RuntimeError("Receiver accepted unsafe " + unsafe)
                finally:
                    os.chown(receiver_target.parent, 0, 0)
                    receiver_target.parent.chmod(0o755)
                    extra.unlink(missing_ok=True)
                    if saved.exists():
                        receiver_target.unlink()
                        saved.rename(receiver_target)
                    os.chown(receiver_target, 0, 0)
                    receiver_target.write_bytes(receiver_source)
                    receiver_target.chmod(0o755)
                    receiver_link.unlink()
                    receiver_link.symlink_to(receiver_target)
                    other.unlink(missing_ok=True)
            if installer.receiver_binding() != binding: raise RuntimeError("Receiver fixture restoration changed its binding")
        command(["docker", "run", "--detach", "--rm", "--name", registry_name,
                 "--publish", "127.0.0.1:5050:5000", smoke_local.REGISTRY_IMAGE])
        images = {"postgres": guard.LEGACY_DIGESTS[architecture]}
        for role in ("web", "worker", "edge", "migrator"):
            tagged = registry + role + ":qualified-a"
            command(["docker", "tag", "duora-" + role + ":ci", tagged])
            command(["docker", "push", "--quiet", tagged])
            refs = json.loads(command(["docker", "image", "inspect", "--format", "{{json .RepoDigests}}", tagged]))
            images[role] = next(ref for ref in refs if ref.startswith(registry + role + "@sha256:"))
        manifest = smoke_local.build_manifest(os.environ["GITHUB_SHA"], architecture, images)
        manifest.update(contractVersion="v1", releaseId="qualified-bootstrap")
        bootstrap = executor.parse_manifest(json.dumps(manifest), image_prefix="")
        paths = executor.Paths(compose_file, host_env, state)
        executor._stage_release_files(bootstrap, state)
        executor._record_release(bootstrap, state)
        executor._mark_current(bootstrap, state)
        compose = executor._compose_command(paths, state / "releases/qualified-bootstrap.env")
        # Explicit creation is limited to a confirmed empty disposable fixture, outside the product executor.
        command(compose + ["up", "--detach", "--wait", "postgres", "backup"])
        inventory = executor.runtime_inventory(bootstrap, paths)
        installer._write(state / guard.BASELINE_NAME, installer._json(inventory["profile"]))
        def identity_metadata() -> dict:
            return {role: {key: value for key, value in payload._inspect("container", item["id"], payload.capture_binary).items()
                          if key in {"Config", "HostConfig", "Mounts", "NetworkSettings", "RestartCount", "Created", "Image", "Id"}}
                    for role, item in inventory["profile"]["services"].items()}
        initial_metadata = identity_metadata()
        def difference_paths(left, right, prefix="") -> list[str]:
            if isinstance(left, dict) and isinstance(right, dict):
                return [path for key in sorted(set(left) | set(right))
                        for path in difference_paths(left.get(key), right.get(key), prefix + "." + key)]
            return [prefix] if left != right else []

        def native_build_payload(reference: str, *, revision: str, architecture: str) -> str:
            if not reference.startswith(registry + "migrator@sha256:") or revision != os.environ["GITHUB_SHA"]:
                raise RuntimeError("Foreign native lab payload")
            item = payload._inspect("image", reference, payload.capture_binary)
            labels = item["Config"]["Labels"]
            if (item["Architecture"] != architecture or item["Os"] != "linux"
                    or labels["org.opencontainers.image.revision"] != revision
                    or labels["org.opencontainers.image.title"] != "duora-migrator"
                    or labels["org.opencontainers.image.source"] != "https://github.com/BipoXDXD/duora"):
                raise RuntimeError("Native lab source identity differs")
            sql = payload._copy_stopped_sql(item["Id"], item["Id"])
            if sql.encode() != (repo / "infra/images/dotnet/reconcile-privileges.sql").read_bytes():
                raise RuntimeError("Native lab canonical SQL differs")
            return sql

        with patch.object(payload, "extract_sql", side_effect=native_build_payload):
            initial_raw = dict(manifest, releaseId="qualified-initial", previousReleaseId="qualified-bootstrap")
            initial = executor.parse_manifest(json.dumps(initial_raw), image_prefix="")
            try:
                executor.deploy(initial, paths, executor.run_command, verify_backup=False)
            except executor.DeployError:
                # Metadata values may contain secrets; disclose only changed field paths.
                print("[baseline-diagnostic] " + json.dumps({"metadataPaths": difference_paths(initial_metadata, identity_metadata()),
                    "profilePaths": difference_paths(inventory["profile"], executor.runtime_inventory(initial, paths)["profile"])}), flush=True)
                raise
            current_compose = executor._compose_command(paths, state / "releases/qualified-initial.env")
            pg = inventory["profile"]["services"]["postgres"]["id"]
            backup = inventory["profile"]["services"]["backup"]["id"]
            command(["docker", "exec", pg, "psql", "-U", "postgres", "-d", "duora", "-v", "ON_ERROR_STOP=1", "-c",
                     "create table ops.deploy_continuity_sentinel(id int primary key, value text); "
                     "insert into ops.deploy_continuity_sentinel values(1,'preserved-synthetic-sentinel')"])
            session = subprocess.Popen(["docker", "exec", "--interactive", pg, "psql", "--no-psqlrc", "-At",
                "-v", "ON_ERROR_STOP=1", "-U", "postgres", "-d", "duora"], stdin=subprocess.PIPE,
                stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, bufsize=1)
            def query(sql: str) -> str:
                assert session is not None and session.stdin is not None and session.stdout is not None
                session.stdin.write(sql + ";\n")
                session.stdin.flush()
                with selectors.DefaultSelector() as selector:
                    selector.register(session.stdout, selectors.EVENT_READ)
                    if not selector.select(15): raise RuntimeError("Persistent SQL session deadline exceeded")
                result = session.stdout.readline().strip()
                if not result or session.poll() is not None: raise RuntimeError("Persistent SQL session died")
                return result
            backend_pid = query("select pg_backend_pid()")
            sentinel_before = query("select count(*)||':'||min(value) from ops.deploy_continuity_sentinel")
            history_before = query('select string_agg("MigrationId",\',\' order by "MigrationId") from ops.__ef_migrations_history')
            events_file = (root / "events.jsonl").open("w")
            window_start = time.time()
            events = subprocess.Popen(["docker", "events", "--since", str(window_start), "--format", "{{json .}}", "--filter", "type=container",
                "--filter", "container=" + pg, "--filter", "container=" + backup], stdout=events_file, stderr=subprocess.DEVNULL)
            def sample() -> None:
                try:
                    while not stop.is_set():
                        at = time.time()
                        db = subprocess.run(["docker", "exec", pg, "pg_isready", "-h", "127.0.0.1", "-U", "duora_app"],
                                            capture_output=True, check=False, timeout=2).returncode
                        samples.append({"at": at, "database_ready": db == 0})
                        stop.wait(0.25)
                except Exception as error:
                    observer_errors.append("database:" + type(error).__name__)  # No output or credentials in proof.
            def sample_application() -> None:
                try:
                    while not stop.is_set():
                        at = time.time()
                        timed_out = False
                        try:
                            result = subprocess.run(current_compose + ["exec", "--no-TTY", "edge", "wget", "--quiet", "--spider",
                                "--timeout=1", "http://duora-web:8080/health/ready"], capture_output=True, check=False, timeout=8)
                            ready = result.returncode == 0
                        except subprocess.TimeoutExpired:
                            ready, timed_out = False, True  # Expected app outage is measured independently of database continuity.
                        app_samples.append({"at": at, "application_ready": ready, "timedOut": timed_out})
                        stop.wait(0.25)
                except Exception as error:
                    observer_errors.append("application-observer:" + type(error).__name__)
            monitor = threading.Thread(target=sample, daemon=True)
            app_monitor = threading.Thread(target=sample_application, daemon=True)
            monitor.start()
            app_monitor.start()
            deadline = time.monotonic() + 5
            while not samples and not observer_errors and time.monotonic() < deadline: time.sleep(0.05)
            if not samples or observer_errors: raise RuntimeError("Readiness sampler did not start")

            # Root-owned real filesystem activation and rollback-on-postcheck failure, without touching services.
            install_paths = installer.InstallPaths(root / "duora-deploy", root / "versions", root / "preimages", state, root / "ingress")
            install_paths.entry.write_text("# synthetic previous executor preimage; never invoked\n")
            install_paths.entry.chmod(0o755)
            for directory in [install_paths.ingress, install_paths.ingress / "duora"]: directory.mkdir(mode=0o700)
            for lock in [install_paths.ingress / "host.lock", install_paths.ingress / "duora/release.lock"]: lock.touch(mode=0o600)
            event = json.loads(Path(os.environ["GITHUB_EVENT_PATH"]).read_text())
            source_commit = event.get("pull_request", {}).get("head", {}).get("sha", os.environ["GITHUB_SHA"])
            run_url = "https://github.com/BipoXDXD/duora/actions/runs/" + os.environ["GITHUB_RUN_ID"]
            bundle = root / "bundle"
            manifest_sha = installer.build_bundle(repo / "scripts/deploy", bundle, commit=source_commit, run_url=run_url)
            live_inventory = executor.runtime_inventory(initial, paths)
            before_install = _instances(current_compose)
            receiver = installer._module(repo / "scripts/release/coolify/receiver.py", "duora_lab_receiver")
            setattr(receiver, "STATE", install_paths.ingress)
            def idle() -> None:
                installer.require_ingress_idle(receiver, install_paths, expected_commit=source_commit, manifest_sha256=manifest_sha)
            def inspect() -> dict: return executor.runtime_inventory(initial, paths)
            options = dict(expected_commit=source_commit, manifest_sha256=manifest_sha,
                reviewed_inventory_sha256=live_inventory["inventorySha256"],
                expected_executor_sha256=installer._sha(install_paths.entry), paths=install_paths,
                inspect_runtime=inspect, require_idle=idle)
            preflight = installer.install_bundle(bundle, **options)
            if preflight["status"] != "preflight" or (state / "executor-install-receipt.json").exists():
                raise RuntimeError("Static preflight wrote activation state")
            # The actual receiver's durable intent and lock code fence this actual installer.
            pending = install_paths.ingress / "duora/pending.json"
            pending.write_text('{"release":"synthetic-in-flight"}')
            try:
                try: installer.install_bundle(bundle, **options)
                except installer.InstallError: pass
                else: raise RuntimeError("Installer accepted the receiver's pending intent")
            finally: pending.unlink()
            def competing_receiver() -> dict:
                child = os.fork()
                if child == 0:
                    try:
                        with receiver.release_locks(install_paths.ingress / "host.lock"):
                            os._exit(92)
                    except BlockingIOError: os._exit(0)
                    except BaseException: os._exit(93)
                _pid, status = os.waitpid(child, 0)
                if not os.WIFEXITED(status) or os.WEXITSTATUS(status) != 0:
                    raise RuntimeError("Actual receiver acquired a lock held by installer")
                return inspect()
            installer.install_bundle(bundle, **{**options, "inspect_runtime": competing_receiver})
            with receiver.release_locks(install_paths.ingress / "host.lock"):
                try: installer.install_bundle(bundle, **options)
                except installer.InstallError: pass
                else: raise RuntimeError("Installer acquired an active receiver lock")
            def fail_after_activation() -> dict:
                if install_paths.entry.is_symlink(): raise installer.InstallError("Intentional disposable postcheck failure")
                return inspect()
            try:
                installer.install_bundle(bundle, **{**options, "inspect_runtime": fail_after_activation}, apply=True)
            except installer.InstallError:
                if installer._sha(install_paths.entry) != options["expected_executor_sha256"]:
                    raise RuntimeError("Own-file recovery failed")
            else: raise RuntimeError("Postcheck failure did not refuse activation")
            receipt = installer.install_bundle(bundle, **options, apply=True)
            static_preserved = _instances(current_compose) == before_install
            if not static_preserved: raise RuntimeError("Static installation changed services")
            active = installer._module(install_paths.entry, "duora_lab_installed_executor")
            active.runtime_preflight(initial, paths, active.run_command, authenticate_payload=False)

            bad_tag = registry + "migrator:qualified-failure"
            bad_dockerfile = root / "failed.Dockerfile"
            bad_dockerfile.write_text("FROM " + images["migrator"] + '\nENTRYPOINT ["/bin/sh","-c","exit 47"]\n')
            command(["docker", "build", "--file", str(bad_dockerfile), "--tag", bad_tag, str(root)])
            command(["docker", "push", "--quiet", bad_tag])
            refs = json.loads(command(["docker", "image", "inspect", "--format", "{{json .RepoDigests}}", bad_tag]))
            bad_migrator = next(ref for ref in refs if ref.startswith(registry + "migrator@sha256:"))
            first_failed_raw = dict(initial_raw, contractVersion="v2", releaseId="qualified-first-v2-failed",
                previousReleaseId=initial.release_id, images=dict(images, migrator=bad_migrator))
            try: active.deploy(active.parse_manifest(json.dumps(first_failed_raw), image_prefix=""), paths, active.run_command, verify_backup=False)
            except active.DeployError: pass
            else: raise RuntimeError("The first v2 failure was accepted")
            floor_bytes = (state / "v2-contract-floor.json").read_bytes()
            if active._current_release_id(state) != initial.release_id: raise RuntimeError("Failed first v2 changed current")
            # Recover the exact qualified current v1 application without reapplying its weaker SQL.
            active.deploy(initial, paths, active.run_command, verify_backup=False)
            if ((state / "v2-contract-floor.json").read_bytes() != floor_bytes
                    or query("select pg_backend_pid()") != backend_pid):
                raise RuntimeError("Current application recovery lowered the v2 floor or lost the SQL session")
            before_legacy = _instances(current_compose)["worker"]
            next_legacy = active.parse_manifest(json.dumps(dict(initial_raw, releaseId="qualified-new-v1-refused")), image_prefix="")
            try: active.deploy(next_legacy, paths, active.run_command, verify_backup=False)
            except active.DeployError: pass
            else: raise RuntimeError("New legacy SQL became eligible after current app-only recovery")
            if _instances(current_compose)["worker"] != before_legacy: raise RuntimeError("Legacy guard stopped the recovered worker")

            def next_web(label: str) -> str:
                tagged = registry + "web:qualified-" + label
                dockerfile = root / (label + ".Dockerfile")
                dockerfile.write_text("FROM " + images["web"] + '\nLABEL duora.synthetic.release="' + label + '"\n')
                command(["docker", "build", "--file", str(dockerfile), "--tag", tagged, str(root)])
                command(["docker", "push", "--quiet", tagged])
                refs = json.loads(command(["docker", "image", "inspect", "--format", "{{json .RepoDigests}}", tagged]))
                return next(ref for ref in refs if ref.startswith(registry + "web@sha256:"))
            first_raw = dict(initial_raw, contractVersion="v2", releaseId="qualified-v2-a", previousReleaseId=initial.release_id,
                             images=dict(images, web=next_web("a")))
            first = active.parse_manifest(json.dumps(first_raw), image_prefix="")
            # Remove the pushed digest alias. The real executor must cold-pull it before native SQL extraction.
            command(["docker", "image", "rm", images["migrator"]])
            active.deploy(first, paths, active.run_command, verify_backup=False)
            if query("select pg_backend_pid()") != backend_pid: raise RuntimeError("First forward lost SQL session")

            second_images = dict(images, web=next_web("b"))
            second_raw = dict(first_raw, releaseId="qualified-v2-b", previousReleaseId=first.release_id, images=second_images)
            second = active.parse_manifest(json.dumps(second_raw), image_prefix="")
            active.deploy(second, paths, active.run_command, verify_backup=False)
            guard.require_preserved(live_inventory["profile"], active.runtime_inventory(second, paths)["profile"])
            if query("select pg_backend_pid()") != backend_pid: raise RuntimeError("Application deployment lost SQL session")
            acl_query = ("select md5(string_agg(n.nspname||'.'||c.relname||':'||p.priv||':'||"
                "has_table_privilege('duora_app',c.oid,p.priv)::text,',' order by n.nspname,c.relname,p.priv)) "
                "from pg_class c join pg_namespace n on n.oid=c.relnamespace cross join "
                "(values('SELECT'),('INSERT'),('UPDATE'),('DELETE'),('TRUNCATE'))p(priv) "
                "where c.relkind in ('r','p') and n.nspname not in ('pg_catalog','information_schema') "
                "and n.nspname not like 'pg_%'")
            acl_before = query(acl_query)
            # A genuine post-worker-stop failure must keep current B and permit its concrete recorded A rollback.
            bad_raw = dict(second_raw, releaseId="qualified-failed", previousReleaseId=second.release_id,
                images=dict(second_images, migrator=bad_migrator))
            try: active.deploy(active.parse_manifest(json.dumps(bad_raw), image_prefix=""), paths, active.run_command, verify_backup=False)
            except active.DeployError: pass
            else: raise RuntimeError("The deliberately failed migrator was accepted")
            if (active._current_release_id(state) != second.release_id or (state / "releases/qualified-failed.json").exists()
                    or "worker" in command(current_compose + ["ps", "--services", "--status", "running"]).split()):
                raise RuntimeError("Failed product deployment did not preserve current/staging boundary")
            # A concrete immutable v2 target, actually served before and after the newer application image.
            active.deploy(first, paths, active.run_command, verify_backup=False)
            acl_after = query(acl_query)
            if (query("select pg_backend_pid()") != backend_pid
                    or query("select count(*)||':'||min(value) from ops.deploy_continuity_sentinel") != sentinel_before
                    or acl_after != acl_before
                    or query('select string_agg("MigrationId",\',\' order by "MigrationId") from ops.__ef_migrations_history') != history_before):
                raise RuntimeError("Compatible rollback changed SQL session, sentinel or schema")
            guard.require_preserved(live_inventory["profile"], active.runtime_inventory(first, paths)["profile"])

            negatives = ["real_new_legacy_after_first_failed_v2_current_app_recovery_refused"]
            def refuses(label: str, target=first) -> None:
                worker_before = _instances(current_compose)["worker"]
                try: active.deploy(target, paths, active.run_command, verify_backup=False)
                except active.DeployError: pass
                else: raise RuntimeError("Negative guard accepted " + label)
                if _instances(current_compose)["worker"] != worker_before: raise RuntimeError("Guard stopped worker: " + label)
                negatives.append(label)
            memory = json.loads(command(["docker", "inspect", pg]))[0]["HostConfig"]["Memory"]
            command(["docker", "update", "--memory", str(memory + 1048576), pg])
            refuses("real_docker_update_resource")
            command(["docker", "update", "--memory", str(memory), pg])
            unreviewed_network = registry_name + "-unreviewed"
            command(["docker", "network", "create", "--internal", unreviewed_network])
            command(["docker", "network", "connect", unreviewed_network, pg])
            refuses("real_network_connect")
            command(["docker", "network", "disconnect", unreviewed_network, pg])
            command(["docker", "network", "rm", unreviewed_network])
            compose_file.write_text(raw_compose.replace("  pgdata:\n", "  pgdata:\n    name: duora_forbidden_qualification\n"))
            refuses("desired_volume_rebinding_without_touching_existing_data")
            compose_file.write_text(raw_compose)
            legacy = active.parse_manifest(json.dumps(dict(first_raw, releaseId="qualified-legacy-refusal", contractVersion="v1")), image_prefix="")
            refuses("real_same_schema_contract_downgrade_refused", legacy)
            command(["docker", "exec", pg, "psql", "-U", "postgres", "-d", "duora", "-v", "ON_ERROR_STOP=1", "-c",
                'insert into ops.__ef_migrations_history values(\'20990101000000_SyntheticGuard\',\'10.0.13\')'])
            refuses("real_unknown_schema_head_refuses_legacy", legacy)
            command(["docker", "exec", pg, "psql", "-U", "postgres", "-d", "duora", "-v", "ON_ERROR_STOP=1", "-c",
                'delete from ops.__ef_migrations_history where "MigrationId"=\'20990101000000_SyntheticGuard\''])
            after_positive = active.runtime_inventory(first, paths)
            guard.require_preserved(live_inventory["profile"], after_positive["profile"])
            window_end = time.time()
            stop.set()
            monitor.join(timeout=15)
            app_monitor.join(timeout=10)
            if monitor.is_alive() or app_monitor.is_alive() or not app_samples:
                raise RuntimeError("Database readiness continuity failed")
            validate_observations(samples, start=window_start, end=window_end, errors=observer_errors,
                                  events_alive=events.poll() is None)
            if query("select pg_backend_pid()") != backend_pid: raise RuntimeError("Read-only guards lost SQL session")
            assert session.stdin is not None
            session.stdin.write("\\q\n")
            session.stdin.flush()
            session.wait(timeout=10)
            session = None
            events.terminate()
            events.wait(timeout=10)
            events_file.close()
            raw_events = [json.loads(line) for line in (root / "events.jsonl").read_text().splitlines()]
            event_records = [{"action": event.get("Action"), "id": event.get("Actor", {}).get("ID"),
                              "timeNano": event.get("timeNano")} for event in raw_events]
            if any(event["id"] not in {pg, backup} or type(event["timeNano"]) is not int for event in event_records):
                raise RuntimeError("Event observation has a foreign or unbound identity")
            actions = [event["action"] for event in event_records]
            if not actions or not any(a.startswith("exec_") for a in actions) or any(a in {"create", "start", "stop", "restart", "kill", "die", "destroy", "oom"} for a in actions):
                raise RuntimeError("Database/backup lifecycle event during application-only window")
            events = None
            # Every destructive negative is explicit administrative fixture work AFTER the positive window.
            # Each subsequent negative gets a separately audited synthetic baseline, never product auto-enrollment.
            command(["docker", "stop", backup])
            command(["docker", "rm", backup])
            command(current_compose + ["up", "--detach", "--wait", "--no-deps", "backup"])
            refuses("real_backup_container_id_replacement_after_positive_window")
            installer._write(state / guard.BASELINE_NAME, installer._json(active.runtime_inventory(first, paths)["profile"]))
            backup = command(current_compose + ["ps", "--all", "--quiet", "backup"])
            command(["docker", "stop", backup])
            command(["docker", "rm", backup])
            foreign_volume = "duora_foreign_" + root.name
            compose_file.write_text(raw_compose.replace("  pgdata:\n", "  pgdata:\n    name: " + foreign_volume + "\n"))
            try: command(current_compose + ["up", "--detach", "--wait", "--no-deps", "backup"])
            finally: compose_file.write_text(raw_compose)
            refuses("real_backup_foreign_volume_mount_after_positive_window")
            backup = command(current_compose + ["ps", "--all", "--quiet", "backup"])
            command(["docker", "stop", backup])
            command(["docker", "rm", backup])
            command(current_compose + ["up", "--detach", "--wait", "--no-deps", "backup"])
            command(["docker", "volume", "rm", foreign_volume])
            installer._write(state / guard.BASELINE_NAME, installer._json(active.runtime_inventory(first, paths)["profile"]))
            command(["docker", "restart", pg])
            deadline = time.monotonic() + 60
            while time.monotonic() < deadline:
                item = json.loads(command(["docker", "inspect", pg]))[0]
                if item["State"].get("Health", {}).get("Status") == "healthy": break
                time.sleep(1)
            refuses("real_administrative_restart_after_positive_continuity_window")
            if invariant_hash != guard.digest({"compose": compose_file.read_text(), "host": host_env.read_text(),
                    "assets": [(media / a / ".duora-media-area").read_text() for a in ("quarantine", "derivatives")]}):
                raise RuntimeError("Synthetic regional/Resend/asset invariants changed")
            report = {"sourceCommit": source_commit, "testedCheckout": os.environ["GITHUB_SHA"], "architecture": architecture,
                "runUrl": run_url, "sql_session_survived": True, "database_backup_identity_preserved": True,
                "negative_guards_passed": negatives, "static_installation_preserved_services": static_preserved,
                "compatible_v2_rollback_passed": True, "baseline": live_inventory["profile"],
                "two_forward_deploys_under_observation": True, "failed_product_recovered": True,
                "first_failed_v2_current_v1_app_recovery_preserved_floor": True,
                "pinnedComposePolicy": {"realRootFilesystem": True, "customJsonPathSelected": True,
                    "unsafeParentFileModeDigestSymlinkRefused": True, "scope": "Synthetic pin policy fixture; actual host pin is qualified under installer locks"},
                "receiverVersionedLinkPolicy": {"realRootFilesystem": True, "sourceSha256": installer.RECEIVER_SHA256,
                    "layout": "versions/2.0.2-<qualified-sha256>/receiver.py", "verifiedTargetLoaded": True,
                    "negativeCases": receiver_negatives, "bindingRestoredExactly": True},
                "receiver_installer_interlock_proved": True,
                "observationWindow": {"start": window_start, "end": window_end, "errors": observer_errors,
                    "maximumAllowedGapSeconds": 3, "startedBefore": first.release_id, "endedAfter": "compatible rollback and negative read-only guards"},
                "staticInstallation": receipt, "manifestSha256": manifest_sha, "readiness": samples,
                "applicationReadiness": app_samples,
                "afterPositiveRuntime": after_positive, "databaseBackupEvents": event_records,
                "rollbackAclFingerprintBefore": acl_before, "rollbackAclFingerprintAfter": acl_after,
                "databaseBackupEventActions": sorted(set(actions)), "backendPid": backend_pid,
                "rollbackArtifact": first_raw, "newerArtifact": second_raw, "invariantsSha256": invariant_hash,
                "payloadOrigin": "Real candidate build in disposable loopback OCI registry; production GHCR path remains a separate pre-worker gate",
                "backupScope": "No production backup touched; existing mandatory restore/PITR job remains separate",
                "negativeRestartScope": "Intentional backup recreation/foreign mount and PG restart only AFTER positive window; separate synthetic audited enrollments isolate negatives, no product auto-enrollment"}
            output = Path(os.environ["RUNNER_TEMP"]) / "duora-executor-lab" / architecture
            output.mkdir(parents=True, exist_ok=True)
            shutil.copytree(bundle, output / "bundle")
            (output / "qualification.json").write_text(json.dumps(report, indent=2) + "\n")
            for path in [output.parent, output, *(output.rglob("*"))]: path.chmod(0o755 if path.is_dir() else 0o644)
            return report
    finally:
        stop.set()
        if monitor is not None: monitor.join(timeout=15)
        if app_monitor is not None: app_monitor.join(timeout=10)
        if session is not None:
            session.kill()
            session.wait(timeout=10)
        if events is not None:
            events.terminate()
            events.wait(timeout=10)
        if compose is not None:
            subprocess.run(compose + ["down", "--volumes", "--remove-orphans"], capture_output=True, check=False, timeout=120)
        subprocess.run(["docker", "rm", "--force", registry_name], capture_output=True, check=False, timeout=30)
        shutil.rmtree(root)


if __name__ == "__main__":
    if sys.argv[1:] != ["--actions-lab"]: raise SystemExit("Actions-only qualification entry required")
    print("LAB_RESULT=" + json.dumps(run_preservation_lab(), separators=(",", ":")))
