"""Disposable Actions-only end-to-end qualification, never a production entry point."""

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
    raise NotImplementedError("Continuous readiness and event-stream proof required")


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
    root = Path(tempfile.mkdtemp(prefix="duora-qualified-actions-"))
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
    monitor = None
    try:
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
        manifest.update(contractVersion="v2", releaseId="qualified-bootstrap")
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
            first_raw = dict(manifest, releaseId="qualified-v2-a", previousReleaseId="qualified-bootstrap")
            first = executor.parse_manifest(json.dumps(first_raw), image_prefix="")
            try:
                executor.deploy(first, paths, executor.run_command, verify_backup=False)
            except executor.DeployError:
                # Metadata values may contain secrets; disclose only changed field paths.
                print("[baseline-diagnostic] " + json.dumps({"metadataPaths": difference_paths(initial_metadata, identity_metadata()),
                    "profilePaths": difference_paths(inventory["profile"], executor.runtime_inventory(first, paths)["profile"])}), flush=True)
                raise
            current_compose = executor._compose_command(paths, state / "releases/qualified-v2-a.env")
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
            events = subprocess.Popen(["docker", "events", "--format", "{{json .}}", "--filter", "type=container",
                "--filter", "container=" + pg, "--filter", "container=" + backup], stdout=events_file, stderr=subprocess.DEVNULL)
            def sample() -> None:
                while not stop.is_set():
                    db = subprocess.run(["docker", "exec", pg, "pg_isready", "-h", "127.0.0.1", "-U", "duora_app"],
                                        capture_output=True, check=False, timeout=10).returncode
                    app = subprocess.run(current_compose + ["exec", "--no-TTY", "edge", "wget", "--quiet", "--spider",
                        "--timeout=2", "http://duora-web:8080/health/ready"], capture_output=True, check=False, timeout=8).returncode
                    samples.append({"at": time.time(), "database_ready": db == 0, "application_ready": app == 0})
                    stop.wait(0.25)
            monitor = threading.Thread(target=sample, daemon=True)
            monitor.start()

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
            live_inventory = executor.runtime_inventory(first, paths)
            before_install = _instances(current_compose)
            def idle() -> None:
                if (install_paths.ingress / "pending.json").exists(): raise installer.InstallError("Synthetic pending ingress")
            def inspect() -> dict: return executor.runtime_inventory(first, paths)
            options = dict(expected_commit=source_commit, manifest_sha256=manifest_sha,
                reviewed_inventory_sha256=live_inventory["inventorySha256"],
                expected_executor_sha256=installer._sha(install_paths.entry), paths=install_paths,
                inspect_runtime=inspect, require_idle=idle)
            preflight = installer.install_bundle(bundle, **options)
            if preflight["status"] != "preflight" or (state / "executor-install-receipt.json").exists():
                raise RuntimeError("Static preflight wrote activation state")
            count = 0
            def fail_after_activation() -> dict:
                nonlocal count
                count += 1
                if count > 1: raise installer.InstallError("Intentional disposable postcheck failure")
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
            active.runtime_preflight(first, paths, active.run_command, authenticate_payload=False)

            second_tag = registry + "web:qualified-b"
            dockerfile = root / "second.Dockerfile"
            dockerfile.write_text("FROM " + images["web"] + '\nLABEL duora.synthetic.release="b"\n')
            command(["docker", "build", "--file", str(dockerfile), "--tag", second_tag, str(root)])
            command(["docker", "push", "--quiet", second_tag])
            refs = json.loads(command(["docker", "image", "inspect", "--format", "{{json .RepoDigests}}", second_tag]))
            second_images = dict(images, web=next(r for r in refs if r.startswith(registry + "web@sha256:")))
            second_raw = dict(first_raw, releaseId="qualified-v2-b", previousReleaseId=first.release_id, images=second_images)
            second = active.parse_manifest(json.dumps(second_raw), image_prefix="")
            active.deploy(second, paths, active.run_command, verify_backup=False)
            guard.require_preserved(live_inventory["profile"], active.runtime_inventory(second, paths)["profile"])
            if query("select pg_backend_pid()") != backend_pid: raise RuntimeError("Application deployment lost SQL session")
            # A concrete immutable v2 target, actually served before and after the newer application image.
            active.deploy(first, paths, active.run_command, verify_backup=False)
            if (query("select pg_backend_pid()") != backend_pid
                    or query("select count(*)||':'||min(value) from ops.deploy_continuity_sentinel") != sentinel_before
                    or query('select string_agg("MigrationId",\',\' order by "MigrationId") from ops.__ef_migrations_history') != history_before):
                raise RuntimeError("Compatible rollback changed SQL session, sentinel or schema")
            guard.require_preserved(live_inventory["profile"], active.runtime_inventory(first, paths)["profile"])

            negatives = []
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
            command(["docker", "exec", pg, "psql", "-U", "postgres", "-d", "duora", "-v", "ON_ERROR_STOP=1", "-c",
                'insert into ops.__ef_migrations_history values(\'20990101000000_SyntheticGuard\',\'10.0.13\')'])
            legacy = active.parse_manifest(json.dumps(dict(first_raw, releaseId="qualified-legacy-refusal", contractVersion="v1")), image_prefix="")
            refuses("real_unknown_schema_head_refuses_legacy", legacy)
            command(["docker", "exec", pg, "psql", "-U", "postgres", "-d", "duora", "-v", "ON_ERROR_STOP=1", "-c",
                'delete from ops.__ef_migrations_history where "MigrationId"=\'20990101000000_SyntheticGuard\''])
            stop.set()
            monitor.join(timeout=15)
            if monitor.is_alive() or not samples or any(not s["database_ready"] for s in samples):
                raise RuntimeError("Database readiness continuity failed")
            if query("select pg_backend_pid()") != backend_pid: raise RuntimeError("Read-only guards lost SQL session")
            assert session.stdin is not None
            session.stdin.write("\\q\n")
            session.stdin.flush()
            session.wait(timeout=10)
            session = None
            events.terminate()
            events.wait(timeout=10)
            events_file.close()
            actions = [json.loads(line).get("Action", "") for line in (root / "events.jsonl").read_text().splitlines()]
            if any(a in {"create", "start", "stop", "restart", "kill", "die", "destroy", "oom"} for a in actions):
                raise RuntimeError("Database/backup lifecycle event during application-only window")
            events = None
            # Intentional interruption is confined to this disposable negative fixture AFTER the continuity window.
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
                "staticInstallation": receipt, "manifestSha256": manifest_sha, "readiness": samples,
                "databaseBackupEventActions": sorted(set(actions)), "backendPid": backend_pid,
                "rollbackArtifact": first_raw, "newerArtifact": second_raw, "invariantsSha256": invariant_hash,
                "payloadOrigin": "Real candidate build in disposable loopback OCI registry; production GHCR path remains a separate pre-worker gate",
                "backupScope": "No production backup touched; existing mandatory restore/PITR job remains separate",
                "negativeRestartScope": "Intentional administrative fixture restart occurred only AFTER successful continuity/rollback window"}
            output = Path(os.environ["RUNNER_TEMP"]) / "duora-executor-lab" / architecture
            output.mkdir(parents=True, exist_ok=True)
            shutil.copytree(bundle, output / "bundle")
            (output / "qualification.json").write_text(json.dumps(report, indent=2) + "\n")
            for path in [output.parent, output, *(output.rglob("*"))]: path.chmod(0o755 if path.is_dir() else 0o644)
            return report
    finally:
        stop.set()
        if monitor is not None: monitor.join(timeout=15)
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
