"""Single static executor installer; apply never invokes product deployment."""
from collections.abc import Callable
from dataclasses import dataclass
from pathlib import Path
import argparse
import fcntl
import hashlib
import importlib.machinery
import importlib.util
import json
import os
import re
import shutil
import stat
import sys
import tempfile
import time
import urllib.request
from contextlib import ExitStack

sys.dont_write_bytecode = True
import runtime_guard as guard

FILES = ("duora_deploy.py", "reconcile_payload.py", "runtime_guard.py", "install_executor.py")


class InstallError(ValueError):
    pass


@dataclass(frozen=True)
class InstallPaths:
    entry: Path
    versions: Path
    preimages: Path
    state: Path
    ingress: Path


def validate_bundle(source: Path, *, expected_commit: str, manifest_sha256: str) -> dict:
    try:
        if (not re.fullmatch(r"[a-f0-9]{40}", expected_commit)
                or not re.fullmatch(r"[a-f0-9]{64}", manifest_sha256)
                or source.is_symlink() or not source.is_dir() or source.stat().st_uid != os.geteuid()
                or source.stat().st_mode & 0o022
                or {p.name for p in source.iterdir()} != set(FILES) | {"bundle.json"}):
            raise InstallError("Unexpected bundle inventory or identity")
        for item in source.iterdir():
            info = item.lstat()
            if (not stat.S_ISREG(info.st_mode) or info.st_uid != os.geteuid() or info.st_nlink != 1 or info.st_size > 256 * 1024
                    or info.st_mode & 0o022):
                raise InstallError("Bundle members must be bounded, regular and protected")
        raw = (source / "bundle.json").read_bytes()
        if hashlib.sha256(raw).hexdigest() != manifest_sha256:
            raise InstallError("Bundle manifest hash differs")
        manifest = guard.decode_json(raw)
        if (set(manifest) != {"schema", "sourceCommit", "guardVersion", "files", "qualificationRun"}
                or type(manifest["schema"]) is not int or manifest["schema"] != 1
                or manifest["sourceCommit"] != expected_commit or type(manifest["guardVersion"]) is not int
                or manifest["guardVersion"] != guard.GUARD_VERSION or set(manifest["files"]) != set(FILES)
                or not re.fullmatch(r"https://github.com/BipoXDXD/duora/actions/runs/[0-9]+", manifest["qualificationRun"])):
            raise InstallError("Unsupported bundle contract or qualification identity")
        for name in FILES:
            record = manifest["files"][name]
            if (set(record) != {"sha256", "mode"} or type(record["mode"]) is not int
                    or record["mode"] != (0o755 if name == "duora_deploy.py" else 0o600)
                    or hashlib.sha256((source / name).read_bytes()).hexdigest() != record["sha256"]):
                raise InstallError("Bundle source hash or mode differs")
        return manifest
    except (OSError, ValueError, TypeError, KeyError) as error:
        raise InstallError("Closed bundle validation failed") from error


def _sha(path: Path) -> str:
    info = path.stat()
    if not stat.S_ISREG(info.st_mode) or info.st_size > 256 * 1024:
        raise InstallError("Invalid executor/preimage")
    return hashlib.sha256(path.read_bytes()).hexdigest()


def _write(path: Path, raw: bytes, *, mode: int = 0o600) -> None:
    fd, name = tempfile.mkstemp(prefix=".executor-", dir=path.parent)
    temporary = Path(name)
    try:
        with os.fdopen(fd, "wb") as stream:
            os.fchmod(stream.fileno(), mode)
            stream.write(raw)
            stream.flush()
            os.fsync(stream.fileno())
        temporary.replace(path)
        _sync(path.parent)
    finally:
        temporary.unlink(missing_ok=True)


def _sync(path: Path) -> None:
    fd = os.open(path, os.O_RDONLY | os.O_DIRECTORY)
    try:
        os.fsync(fd)
    finally:
        os.close(fd)


def _json(value: dict) -> bytes:
    return (json.dumps(value, sort_keys=True, separators=(",", ":"), allow_nan=False) + "\n").encode()


def install_bundle(source: Path, *, expected_commit: str, manifest_sha256: str,
                   reviewed_inventory_sha256: str, expected_executor_sha256: str,
                   paths: InstallPaths, inspect_runtime: Callable[[], dict],
                   require_idle: Callable[[], None], apply: bool = False) -> dict:
    manifest = validate_bundle(source, expected_commit=expected_commit, manifest_sha256=manifest_sha256)
    lock_paths = (paths.ingress / "host.lock", paths.ingress / "duora/release.lock", paths.state / ".deploy.lock")
    with ExitStack() as locks:
        try:
            for path in lock_paths:
                info = path.lstat()
                if not stat.S_ISREG(info.st_mode) or info.st_uid != os.geteuid() or info.st_mode & 0o022:
                    raise InstallError("Shared lock is not an owned protected regular file")
                handle = locks.enter_context(path.open("r"))
                fcntl.flock(handle, fcntl.LOCK_EX | fcntl.LOCK_NB)
            require_idle()
            if _sha(paths.entry) != expected_executor_sha256:
                raise InstallError("Executor preimage changed")
            inventory = inspect_runtime()
            guard.require_preserved(inventory["profile"], inventory["profile"])
            actual_inventory = guard.digest({"profile": inventory["profile"], "audit": inventory["audit"]})
            if actual_inventory != inventory["inventorySha256"] or actual_inventory != reviewed_inventory_sha256:
                raise InstallError("Inventory differs from the explicitly reviewed reference")
            receipt = {"schema": 1, "status": "preflight", "sourceCommit": expected_commit,
                "manifestSha256": manifest_sha256, "inventorySha256": actual_inventory,
                "baselineSha256": hashlib.sha256(_json(inventory["profile"])).hexdigest(),
                "executorSha256": manifest["files"]["duora_deploy.py"]["sha256"], "files": manifest["files"],
                "qualificationRun": manifest["qualificationRun"]}
            if not apply:
                return receipt
            for directory in (paths.versions, paths.preimages):
                if directory.exists() and (directory.is_symlink() or directory.stat().st_uid != os.geteuid()
                                           or directory.stat().st_mode & 0o077):
                    raise InstallError("Version/preimage root is not private")
                directory.mkdir(parents=True, exist_ok=True, mode=0o700)
            version = paths.versions / expected_commit
            if version.exists():
                validate_bundle(version, expected_commit=expected_commit, manifest_sha256=manifest_sha256)
            else:
                stage = Path(tempfile.mkdtemp(prefix=".staging-", dir=paths.versions))
                try:
                    for name in (*FILES, "bundle.json"):
                        _write(stage / name, (source / name).read_bytes(),
                               mode=manifest["files"][name]["mode"] if name in FILES else 0o600)
                    validate_bundle(stage, expected_commit=expected_commit, manifest_sha256=manifest_sha256)
                    stage.rename(version)
                    _sync(paths.versions)
                finally:
                    if stage.exists(): shutil.rmtree(stage)
            preimage = Path(tempfile.mkdtemp(prefix="installation-", dir=paths.preimages))
            previous_entry = preimage / "duora-deploy"
            shutil.copy2(paths.entry, previous_entry, follow_symlinks=False)
            baseline_path = paths.state / guard.BASELINE_NAME
            old_baseline = baseline_path.read_bytes() if baseline_path.exists() else None
            new_baseline = _json(inventory["profile"])
            if old_baseline is not None: _write(preimage / guard.BASELINE_NAME, old_baseline)
            _write(preimage / "attempt.json", _json(receipt))
            if _sha(paths.entry) != expected_executor_sha256:
                raise InstallError("Executor changed before activation")
            new_entry = version / "duora_deploy.py"
            temporary_link = paths.entry.with_name(".duora-deploy-" + preimage.name)
            activated = False
            try:
                _write(baseline_path, new_baseline)
                temporary_link.symlink_to(new_entry)
                temporary_link.replace(paths.entry)
                activated = True
                _sync(paths.entry.parent)
                # The callback re-reads the live runtime and the new installed path in the production CLI.
                require_idle()
                after = inspect_runtime()
                guard.require_preserved(inventory["profile"], after["profile"])
                if after["inventorySha256"] != actual_inventory or _sha(paths.entry) != receipt["executorSha256"]:
                    raise InstallError("Post-activation identity changed")
                receipt.update(status="installed", appliedAt=int(time.time()), preimage=str(preimage))
                _write(paths.state / "executor-install-receipt.json", _json(receipt))
                return receipt
            except Exception:
                owns_entry = activated and paths.entry.is_symlink() and paths.entry.resolve() == new_entry
                if owns_entry and _sha(paths.entry) == receipt["executorSha256"]:
                    if previous_entry.is_symlink():
                        temporary_link.symlink_to(os.readlink(previous_entry))
                        temporary_link.replace(paths.entry)
                    else:
                        _write(paths.entry, previous_entry.read_bytes(), mode=stat.S_IMODE(previous_entry.stat().st_mode))
                elif activated:
                    raise InstallError("Intervening executor change; refusing recovery overwrite")
                if baseline_path.exists() and baseline_path.read_bytes() == new_baseline:
                    if old_baseline is None: baseline_path.unlink()
                    else: _write(baseline_path, old_baseline)
                raise
            finally:
                temporary_link.unlink(missing_ok=True)
        except Exception as error:
            raise InstallError("Static installation/preflight refused; no product deployment invoked") from error


def build_bundle(source: Path, destination: Path, *, commit: str, run_url: str) -> str:
    destination.mkdir(mode=0o700)
    files = {}
    for name in FILES:
        content = (source / name).read_bytes()
        mode = 0o755 if name == "duora_deploy.py" else 0o600
        _write(destination / name, content, mode=mode)
        files[name] = {"sha256": hashlib.sha256(content).hexdigest(), "mode": mode}
    manifest = {"schema": 1, "sourceCommit": commit, "guardVersion": guard.GUARD_VERSION,
                "files": files, "qualificationRun": run_url}
    _write(destination / "bundle.json", _json(manifest))
    checksum = _sha(destination / "bundle.json")
    validate_bundle(destination, expected_commit=commit, manifest_sha256=checksum)
    return checksum


def _module(path: Path, name: str):
    loader = importlib.machinery.SourceFileLoader(name, str(path))
    spec = importlib.util.spec_from_loader(name, loader)
    if spec is None: raise InstallError("Cannot load the verified executor")
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    loader.exec_module(module)
    return module


def _production_idle() -> None:
    path = Path("/opt/coolify-release/receiver.py")
    if path.stat().st_uid != 0 or path.stat().st_mode & 0o022:
        raise InstallError("Untrusted installed receiver")
    if _sha(path) != "1ccd1ada4777c80081c4f9bb2a64a500b1e221f794406319504d9e66cbb975cb":
        raise InstallError("Shared receiver is outside the qualified version")
    receiver = _module(path, "duora_installer_reviewed_receiver")
    receiver.require_host_idle()
    if receiver.api("deployments") != []:
        raise InstallError("Coolify global queue is active or unknown")


def _verify_provenance(manifest: dict) -> None:
    run = manifest["qualificationRun"].rsplit("/", 1)[1]
    request = urllib.request.Request("https://api.github.com/repos/BipoXDXD/duora/actions/runs/" + run,
                                     headers={"Accept": "application/vnd.github+json", "User-Agent": "duora-static-installer"})
    with urllib.request.urlopen(request, timeout=30) as response:
        record = guard.decode_json(response.read(256 * 1024 + 1))
    if (record.get("status") != "completed" or record.get("conclusion") != "success"
            or record.get("head_sha") != manifest["sourceCommit"] or record.get("name") != "CI"
            or record.get("path") != ".github/workflows/ci.yml"
            or record.get("repository", {}).get("full_name") != "BipoXDXD/duora"):
        raise InstallError("Bundle qualification is not a successful exact-source CI run")
    for name, info in manifest["files"].items():
        url = "https://raw.githubusercontent.com/BipoXDXD/duora/" + manifest["sourceCommit"] + "/scripts/deploy/" + name
        with urllib.request.urlopen(url, timeout=30) as response:
            raw = response.read(256 * 1024 + 1)
        if hashlib.sha256(raw).hexdigest() != info["sha256"]:
            raise InstallError("Bundle code differs from its immutable reviewed source")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--bundle", type=Path, required=True)
    parser.add_argument("--source-commit", required=True)
    parser.add_argument("--manifest-sha256", required=True)
    parser.add_argument("--reviewed-inventory-sha256")
    parser.add_argument("--expected-executor-sha256")
    parser.add_argument("--apply", action="store_true")
    args = parser.parse_args()
    try:
        if os.geteuid() != 0: raise InstallError("Administrative root entry required")
        os.umask(0o077)
        manifest = validate_bundle(args.bundle, expected_commit=args.source_commit, manifest_sha256=args.manifest_sha256)
        if _sha(Path(__file__)) != manifest["files"]["install_executor.py"]["sha256"]:
            raise InstallError("Invoke the installer from the exact verified bundle")
        _verify_provenance(manifest)
        paths = InstallPaths(Path("/usr/local/sbin/duora-deploy"), Path("/opt/duora-executor/releases"),
            Path("/opt/duora-executor/preimages"), Path("/var/lib/duora"), Path("/var/lib/coolify-release"))
        executor = _module(args.bundle / "duora_deploy.py", "duora_candidate_executor")
        current = guard.decode_json((paths.state / "current.json").read_bytes())["releaseId"]
        raw = (paths.state / "releases" / (current + ".json")).read_text()
        release = executor.parse_manifest(raw, image_prefix="ghcr.io/bipoxdxd/duora-")
        deploy_paths = executor.Paths(Path("/opt/duora/compose.prod.yml"), Path("/etc/duora/duora.env"), paths.state)
        def inspect() -> dict:
            module = _module(paths.entry, "duora_activated_executor") if paths.entry.is_symlink() and paths.entry.resolve().parent.name == args.source_commit else executor
            inventory = module.runtime_inventory(release, deploy_paths)
            # Schema and legacy identity remain read-only; no external SQL container is materialized here.
            runtime_env = paths.state / "releases" / (current + ".env")
            compose = module._compose_command(deploy_paths, runtime_env)
            head = module._read_migration_head(compose, module.run_command)
            sql_hash = ""
            if release.contract_version == "v1":
                pg = inventory["profile"]["services"]["postgres"]["id"]
                sql_hash = module._payload.capture_binary(["docker", "exec", pg, "sha256sum", "/usr/local/share/duora/reconcile-privileges.sql"]).decode().split()[0]
            guard.require_contract(release.raw, schema_head=head, legacy_sql_sha256=sql_hash)
            return inventory
        if args.apply and (not args.reviewed_inventory_sha256 or not args.expected_executor_sha256):
            raise InstallError("Apply requires both explicitly reviewed inventory and executor preimage hashes")
        # Inspection still holds the real locks and idle gate. It never writes enrollment or code.
        result = install_bundle(args.bundle, expected_commit=args.source_commit, manifest_sha256=args.manifest_sha256,
            reviewed_inventory_sha256=args.reviewed_inventory_sha256 or inspect()["inventorySha256"],
            expected_executor_sha256=args.expected_executor_sha256 or _sha(paths.entry), paths=paths,
            inspect_runtime=inspect, require_idle=_production_idle, apply=args.apply)
        if not args.apply: result["inventory"] = inspect()
        print(json.dumps(result, sort_keys=True))
        return 0
    except (InstallError, OSError, ValueError) as error:
        print("Static executor operation refused: " + str(error), file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
