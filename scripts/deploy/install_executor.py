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
import platform
import re
import shutil
import stat
import sys
import tempfile
import time
import urllib.request
from contextlib import ExitStack, contextmanager

sys.dont_write_bytecode = True
import runtime_guard as guard

FILES = ("duora_deploy.py", "reconcile_payload.py", "runtime_guard.py", "install_executor.py")
FENCE_DIRECTORY = "duora-executor-install"


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


def _protected_bytes(path: Path) -> bytes:
    info = path.lstat()
    if (not stat.S_ISREG(info.st_mode) or info.st_uid != os.geteuid() or info.st_mode & 0o022
            or info.st_nlink != 1 or info.st_size > 256 * 1024):
        raise InstallError("Preserved state must be an owned protected bounded regular file")
    return path.read_bytes()


def installation_context(paths: InstallPaths, preserved_files: tuple[Path, ...]) -> dict:
    try:
        current_raw = _protected_bytes(paths.state / "current.json")
        current = guard.decode_json(current_raw)["releaseId"]
        if not isinstance(current, str) or not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._-]{0,79}", current):
            raise InstallError("Invalid current release identity")
        files = (paths.state / "current.json", paths.state / "releases" / (current + ".json"),
                 paths.state / "releases" / (current + ".env"), *preserved_files)
        result: dict = {str(path): hashlib.sha256(_protected_bytes(path)).hexdigest() for path in files}
        floor = paths.state / "v2-contract-floor.json"
        raw_floor = _optional(floor)
        result[str(floor)] = hashlib.sha256(raw_floor).hexdigest() if raw_floor is not None else None
        return result
    except (OSError, ValueError, KeyError, TypeError) as error:
        raise InstallError("Cannot bind reviewed current/manifest/environment/preservation state") from error


def require_installation_context(expected: dict, paths: InstallPaths, preserved_files: tuple[Path, ...]) -> None:
    if installation_context(paths, preserved_files) != expected:
        raise InstallError("Current application or preserved configuration changed since review")


def pinned_compose(pin: dict) -> Path:
    raise NotImplementedError("Select and verify the actual protected observer-pinned Compose")


@contextmanager
def _locks(paths: InstallPaths):
    with ExitStack() as locks:
        for path in (paths.ingress / "host.lock", paths.ingress / "duora/release.lock", paths.state / ".deploy.lock"):
            info = path.lstat()
            if not stat.S_ISREG(info.st_mode) or info.st_uid != os.geteuid() or info.st_mode & 0o022:
                raise InstallError("Shared lock is not an owned protected regular file")
            handle = locks.enter_context(path.open("r"))
            fcntl.flock(handle, fcntl.LOCK_EX | fcntl.LOCK_NB)
        yield


def _optional(path: Path) -> bytes | None:
    return _protected_bytes(path) if path.exists() or path.is_symlink() else None


def require_ingress_idle(receiver, paths: InstallPaths, *, expected_commit: str, manifest_sha256: str) -> None:
    """Receiver-compatible durable fence; exempt only a fully verified own static-install intent."""
    fence_dir = paths.ingress / FENCE_DIRECTORY
    fence = fence_dir / "pending.json"
    if not fence.exists() and not fence.is_symlink():
        receiver.require_host_idle()
        return
    try:
        info = fence_dir.lstat()
        if not stat.S_ISDIR(info.st_mode) or info.st_uid != os.geteuid() or info.st_mode & 0o077:
            raise InstallError("Unowned ingress fence directory")
        pending = guard.decode_json(_protected_bytes(fence))
        if (set(pending) != {"attempt", "attemptSha256"} or not isinstance(pending["attempt"], str)
                or not re.fullmatch(r"installation-[a-z0-9_]+", pending["attempt"])
                or not isinstance(pending["attemptSha256"], str)
                or not re.fullmatch(r"[a-f0-9]{64}", pending["attemptSha256"])):
            raise InstallError("Unknown ingress fence")
        preimage = paths.preimages / pending["attempt"]
        info = preimage.lstat()
        if not stat.S_ISDIR(info.st_mode) or info.st_uid != os.geteuid() or info.st_mode & 0o077:
            raise InstallError("Unowned fenced attempt")
        raw = _protected_bytes(preimage / "attempt.json")
        if hashlib.sha256(raw).hexdigest() != pending["attemptSha256"]: raise InstallError("Fenced journal changed")
        receipt = guard.decode_json(raw)["installedReceipt"]
        if receipt["sourceCommit"] != expected_commit or receipt["manifestSha256"] != manifest_sha256:
            raise InstallError("Fence belongs to another source or bundle")
        validate_bundle(paths.versions / expected_commit, expected_commit=expected_commit, manifest_sha256=manifest_sha256)
        private_pending = _optional(paths.state / "executor-install-pending.json")
        if private_pending is not None and guard.decode_json(private_pending) != pending:
            raise InstallError("Private pending disagrees with the own fence")
        # Never remove or ignore a global or another project's intent, including unknown project directories.
        receiver.require_no_pending(paths.ingress)
        for directory in paths.ingress.iterdir():
            if directory.is_dir() and directory != fence_dir: receiver.require_no_pending(directory)
    except (OSError, ValueError, KeyError, TypeError) as error:
        raise InstallError("Ingress is active or the own fence cannot be authenticated") from error


def _recover_locked(paths: InstallPaths, pending: dict, require_idle: Callable[[], None]) -> dict:
    require_idle()
    marker = paths.state / "executor-install-pending.json"
    fence = paths.ingress / FENCE_DIRECTORY / "pending.json"
    if (set(pending) != {"attempt", "attemptSha256"}
            or not isinstance(pending["attempt"], str)
            or not re.fullmatch(r"installation-[a-z0-9_]+", pending["attempt"])
            or not re.fullmatch(r"[a-f0-9]{64}", pending["attemptSha256"])
            or guard.decode_json(_protected_bytes(fence)) != pending
            or ((marker.exists() or marker.is_symlink()) and guard.decode_json(_protected_bytes(marker)) != pending)):
        raise InstallError("Recovery requires the exact outstanding own-attempt identity")
    preimage = paths.preimages / pending["attempt"]
    info = preimage.lstat()
    if not stat.S_ISDIR(info.st_mode) or info.st_uid != os.geteuid() or info.st_mode & 0o077:
        raise InstallError("Untrusted recovery preimage directory")
    raw = _protected_bytes(preimage / "attempt.json")
    if hashlib.sha256(raw).hexdigest() != pending["attemptSha256"]:
        raise InstallError("Recovery journal changed")
    journal = guard.decode_json(raw)
    receipt = journal["installedReceipt"]
    commit = receipt["sourceCommit"]
    if not re.fullmatch(r"[a-f0-9]{40}", commit): raise InstallError("Invalid recovery source")
    version = paths.versions / commit
    validate_bundle(version, expected_commit=commit, manifest_sha256=receipt["manifestSha256"])
    target = version / "duora_deploy.py"
    previous = preimage / "duora-deploy"
    if _sha(previous) != journal["oldExecutorSha256"]:
        raise InstallError("Previous executor preimage changed")
    old_baseline = _optional(preimage / guard.BASELINE_NAME)
    old_receipt = _optional(preimage / "executor-install-receipt.json")
    for value, key in [(old_baseline, "oldBaselineSha256"), (old_receipt, "oldReceiptSha256")]:
        if (hashlib.sha256(value).hexdigest() if value is not None else None) != journal[key]:
            raise InstallError("Recovery state preimage changed")
    baseline = paths.state / guard.BASELINE_NAME
    current_baseline = _optional(baseline)
    baseline_hash = hashlib.sha256(current_baseline).hexdigest() if current_baseline is not None else None
    if baseline_hash not in {journal["oldBaselineSha256"], receipt["baselineSha256"]}:
        raise InstallError("Intervening baseline change; refusing recovery overwrite")
    current_receipt_path = paths.state / "executor-install-receipt.json"
    current_receipt = _optional(current_receipt_path)
    if current_receipt not in (old_receipt, _json(receipt)):
        raise InstallError("Intervening installation receipt; refusing recovery overwrite")
    is_old = (_sha(paths.entry) == journal["oldExecutorSha256"]
              and paths.entry.is_symlink() == previous.is_symlink()
              and (not previous.is_symlink() or os.readlink(paths.entry) == os.readlink(previous)))
    is_new = (paths.entry.is_symlink() and os.readlink(paths.entry) == str(target)
              and _sha(paths.entry) == receipt["executorSha256"])
    temporary_link = paths.entry.with_name(".duora-deploy-" + preimage.name)
    owned_targets = {str(target)} | ({os.readlink(previous)} if previous.is_symlink() else set())
    if (not (is_old or is_new) or ((temporary_link.exists() or temporary_link.is_symlink())
            and (not temporary_link.is_symlink() or os.readlink(temporary_link) not in owned_targets))):
        raise InstallError("Intervening executor/link change; refusing recovery overwrite")
    # All ownership/preimage checks precede either replacement. Never touch runtime, data or release state.
    temporary_link.unlink(missing_ok=True)
    if old_baseline is None: baseline.unlink(missing_ok=True)
    else: _write(baseline, old_baseline)
    if is_new:
        if previous.is_symlink():
            temporary_link.symlink_to(os.readlink(previous))
            temporary_link.replace(paths.entry)
            _sync(paths.entry.parent)
        else: _write(paths.entry, previous.read_bytes(), mode=stat.S_IMODE(previous.stat().st_mode))
    if old_receipt is None: current_receipt_path.unlink(missing_ok=True)
    else: _write(current_receipt_path, old_receipt)
    recovery = {"status": "recovered-own-attempt", **pending, "recoveredAt": int(time.time()),
                "executorSha256": journal["oldExecutorSha256"]}
    _write(paths.state / "executor-recovery-receipt.json", _json(recovery))
    marker.unlink(missing_ok=True)
    _sync(paths.state)
    fence.unlink()
    _sync(fence.parent)
    return recovery


def recover_installation(*, paths: InstallPaths, pending: dict, require_idle: Callable[[], None]) -> dict:
    try:
        with _locks(paths): return _recover_locked(paths, pending, require_idle)
    except (OSError, ValueError, KeyError, TypeError) as error:
        raise InstallError("Own-attempt recovery refused without overwriting foreign state") from error


def install_bundle(source: Path, *, expected_commit: str, manifest_sha256: str,
                   reviewed_inventory_sha256: str, expected_executor_sha256: str,
                   paths: InstallPaths, inspect_runtime: Callable[[], dict],
                   require_idle: Callable[[], None], apply: bool = False,
                   qualification_artifact: int | None = None) -> dict:
    manifest = validate_bundle(source, expected_commit=expected_commit, manifest_sha256=manifest_sha256)
    try:
        with _locks(paths):
            fence_dir = paths.ingress / FENCE_DIRECTORY
            fence = fence_dir / "pending.json"
            if ((paths.state / "executor-install-pending.json").exists() or fence.exists() or fence.is_symlink()):
                raise InstallError("Resolve the outstanding interrupted installation explicitly first")
            require_idle()
            if paths.entry.is_symlink() and not Path(os.readlink(paths.entry)).is_absolute():
                raise InstallError("Relative previous executor target requires explicit administrative review")
            if _sha(paths.entry) != expected_executor_sha256: raise InstallError("Executor preimage changed")
            inventory = inspect_runtime()
            guard.require_preserved(inventory["profile"], inventory["profile"])
            actual_inventory = guard.digest({"profile": inventory["profile"], "audit": inventory["audit"]})
            if actual_inventory != inventory["inventorySha256"] or actual_inventory != reviewed_inventory_sha256:
                raise InstallError("Inventory differs from the explicitly reviewed reference")
            receipt = {"schema": 1, "status": "preflight", "sourceCommit": expected_commit,
                "manifestSha256": manifest_sha256, "inventorySha256": actual_inventory,
                "baselineSha256": hashlib.sha256(_json(inventory["profile"])).hexdigest(),
                "executorSha256": manifest["files"]["duora_deploy.py"]["sha256"], "files": manifest["files"],
                "qualificationRun": manifest["qualificationRun"], "qualificationArtifactId": qualification_artifact}
            if not apply:
                return {**receipt, "inventory": inventory}
            for directory in (paths.versions, paths.preimages, fence_dir):
                if directory.exists() and (directory.is_symlink() or directory.stat().st_uid != os.geteuid()
                                           or directory.stat().st_mode & 0o077):
                    raise InstallError("Version/preimage root is not private")
                directory.mkdir(parents=True, exist_ok=True, mode=0o700)
                _sync(directory.parent)
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
            if paths.entry.is_symlink():
                previous_entry.symlink_to(os.readlink(paths.entry))
                _sync(preimage)
            else: _write(previous_entry, paths.entry.read_bytes(), mode=stat.S_IMODE(paths.entry.stat().st_mode))
            baseline_path = paths.state / guard.BASELINE_NAME
            old_baseline = _optional(baseline_path)
            old_receipt = _optional(paths.state / "executor-install-receipt.json")
            new_baseline = _json(inventory["profile"])
            if old_baseline is not None: _write(preimage / guard.BASELINE_NAME, old_baseline)
            if old_receipt is not None: _write(preimage / "executor-install-receipt.json", old_receipt)
            receipt.update(status="installed", appliedAt=int(time.time()), preimage=str(preimage))
            journal = {"installedReceipt": receipt, "oldExecutorSha256": expected_executor_sha256,
                "oldBaselineSha256": hashlib.sha256(old_baseline).hexdigest() if old_baseline is not None else None,
                "oldReceiptSha256": hashlib.sha256(old_receipt).hexdigest() if old_receipt is not None else None}
            journal_raw = _json(journal)
            _write(preimage / "attempt.json", journal_raw)
            _sync(paths.preimages)
            pending = {"attempt": preimage.name, "attemptSha256": hashlib.sha256(journal_raw).hexdigest()}
            # The durable marker/journal precede every activation write. SIGKILL needs explicit guarded recovery.
            if _sha(paths.entry) != expected_executor_sha256: raise InstallError("Executor changed before activation")
            after_staging = inspect_runtime()
            if after_staging["inventorySha256"] != actual_inventory: raise InstallError("Context changed during staging")
            require_idle()
            # The existing receiver scans unknown project directories before imports or writing any intent.
            # Fence it first, so SIGKILL cannot turn executor-private pending into a circular foreign intent.
            _write(fence, _json(pending))
            temporary_link = paths.entry.with_name(".duora-deploy-" + preimage.name)
            try:
                _write(paths.state / "executor-install-pending.json", _json(pending))
                _write(baseline_path, new_baseline)
                temporary_link.symlink_to(version / "duora_deploy.py")
                temporary_link.replace(paths.entry)
                _sync(paths.entry.parent)
                require_idle()
                after = inspect_runtime()
                guard.require_preserved(inventory["profile"], after["profile"])
                validate_bundle(version, expected_commit=expected_commit, manifest_sha256=manifest_sha256)
                if (after["inventorySha256"] != actual_inventory or _sha(paths.entry) != receipt["executorSha256"]
                        or hashlib.sha256(_protected_bytes(baseline_path)).hexdigest() != receipt["baselineSha256"]):
                    raise InstallError("Post-activation identity changed")
                _write(paths.state / "executor-install-receipt.json", _json(receipt))
                (paths.state / "executor-install-pending.json").unlink()
                _sync(paths.state)
                fence.unlink()
                _sync(fence_dir)
                return receipt
            except Exception:
                _recover_locked(paths, pending, require_idle)
                raise
            finally:
                if temporary_link.is_symlink() and os.readlink(temporary_link) == str(version / "duora_deploy.py"):
                    temporary_link.unlink()
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


def _production_idle(paths: InstallPaths, *, expected_commit: str, manifest_sha256: str) -> None:
    path = Path("/opt/coolify-release/receiver.py")
    if path.stat().st_uid != 0 or path.stat().st_mode & 0o022:
        raise InstallError("Untrusted installed receiver")
    if _sha(path) != "1ccd1ada4777c80081c4f9bb2a64a500b1e221f794406319504d9e66cbb975cb":
        raise InstallError("Shared receiver is outside the qualified version")
    receiver = _module(path, "duora_installer_reviewed_receiver")
    require_ingress_idle(receiver, paths, expected_commit=expected_commit, manifest_sha256=manifest_sha256)
    if receiver.api("deployments") != []:
        raise InstallError("Coolify global queue is active or unknown")


def _verify_provenance(manifest: dict, artifact_id: int) -> None:
    def get(path: str) -> dict:
        request = urllib.request.Request("https://api.github.com/repos/BipoXDXD/duora/" + path,
            headers={"Accept": "application/vnd.github+json", "User-Agent": "duora-static-installer"})
        with urllib.request.urlopen(request, timeout=30) as response:
            return guard.decode_json(response.read(256 * 1024 + 1))
    run = manifest["qualificationRun"].rsplit("/", 1)[1]
    record = get("actions/runs/" + run)
    if (record.get("status") != "completed" or record.get("conclusion") != "success"
            or record.get("head_sha") != manifest["sourceCommit"] or record.get("name") != "CI"
            or record.get("path") != ".github/workflows/ci.yml"
            or record.get("repository", {}).get("full_name") != "BipoXDXD/duora"
            or record.get("head_repository", {}).get("full_name") != "BipoXDXD/duora"
            or record.get("event") not in {"pull_request", "push"}):
        raise InstallError("Bundle qualification is not a successful exact-source CI run")
    jobs = get("actions/runs/" + run + "/jobs?per_page=100")
    required = {"backend", "frontend", "e2e", "ops", "images (amd64)", "images (arm64)", "contract", "secret-scan", "supply-chain"}
    conclusions = {job["name"]: job["conclusion"] for job in jobs["jobs"]}
    if jobs["total_count"] > 100 or any(conclusions.get(name) != "success" for name in required):
        raise InstallError("Every mandatory final-source check must succeed without skips")
    artifact = get("actions/artifacts/" + str(artifact_id))
    architecture = {"x86_64": "amd64", "aarch64": "arm64"}.get(platform.machine())
    if (artifact.get("expired") is not False or artifact.get("name") != "duora-executor-" + str(architecture)
            or artifact.get("workflow_run", {}).get("id") != int(run)
            or artifact.get("workflow_run", {}).get("head_sha") != manifest["sourceCommit"]):
        raise InstallError("Qualification artifact is expired, foreign or not native")
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
    parser.add_argument("--artifact-id", type=int, required=True)
    parser.add_argument("--reviewed-inventory-sha256")
    parser.add_argument("--expected-executor-sha256")
    parser.add_argument("--apply", action="store_true")
    parser.add_argument("--recover-attempt")
    parser.add_argument("--recover-attempt-sha256")
    args = parser.parse_args()
    try:
        if os.geteuid() != 0: raise InstallError("Administrative root entry required")
        os.umask(0o077)
        for item in (args.bundle, *args.bundle.parents):
            info = item.lstat()
            if not item.is_absolute() or not stat.S_ISDIR(info.st_mode) or info.st_uid != 0 or info.st_mode & 0o022:
                raise InstallError("Candidate bundle must have protected root-owned directory ancestry")
        manifest = validate_bundle(args.bundle, expected_commit=args.source_commit, manifest_sha256=args.manifest_sha256)
        if _sha(Path(__file__)) != manifest["files"]["install_executor.py"]["sha256"]:
            raise InstallError("Invoke the installer from the exact verified bundle")
        if args.artifact_id <= 0: raise InstallError("Exact qualification artifact ID required")
        _verify_provenance(manifest, args.artifact_id)
        paths = InstallPaths(Path("/usr/local/sbin/duora-deploy"), Path("/opt/duora-executor/releases"),
            Path("/opt/duora-executor/preimages"), Path("/var/lib/duora"), Path("/var/lib/coolify-release"))
        def idle() -> None:
            _production_idle(paths, expected_commit=args.source_commit, manifest_sha256=args.manifest_sha256)
        if args.recover_attempt or args.recover_attempt_sha256:
            if args.apply or not args.recover_attempt or not args.recover_attempt_sha256:
                raise InstallError("Recovery is separate from apply and requires exact attempt and journal SHA")
            result = recover_installation(paths=paths, pending={"attempt": args.recover_attempt,
                "attemptSha256": args.recover_attempt_sha256}, require_idle=idle)
            print(json.dumps(result, sort_keys=True))
            return 0
        executor = _module(args.bundle / "duora_deploy.py", "duora_candidate_executor")
        pinned = Path("/etc/duora/coolify-deploy.json")
        pin_raw = _protected_bytes(pinned)
        pin = guard.decode_json(pin_raw)
        deploy_paths = executor.Paths(pinned_compose(pin), Path("/etc/duora/duora.env"), paths.state)
        preserved = (deploy_paths.compose_file, deploy_paths.host_env, pinned, Path(pin["caddy"]),
                     Path(pin["manifest"]), Path("/opt/coolify-release/receiver.py"), paths.ingress / "duora/current.json")
        context = installation_context(paths, preserved)
        if hashlib.sha256(pin_raw).hexdigest() != context[str(pinned)]:
            raise InstallError("Pinned configuration changed during initial inspection")
        current = guard.decode_json(_protected_bytes(paths.state / "current.json"))["releaseId"]
        raw = _protected_bytes(paths.state / "releases" / (current + ".json")).decode("utf-8")
        release = executor.parse_manifest(raw, image_prefix="ghcr.io/bipoxdxd/duora-")
        require_installation_context(context, paths, preserved)
        def inspect() -> dict:
            require_installation_context(context, paths, preserved)
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
            require_installation_context(context, paths, preserved)
            inventory["audit"]["preservationStateSha256"] = guard.digest(context)
            inventory["audit"]["preservationFiles"] = context
            inventory["inventorySha256"] = guard.digest({"profile": inventory["profile"], "audit": inventory["audit"]})
            return inventory
        if args.apply and (not args.reviewed_inventory_sha256 or not args.expected_executor_sha256):
            raise InstallError("Apply requires both explicitly reviewed inventory and executor preimage hashes")
        # Inspection still holds the real locks and idle gate. It never writes enrollment or code.
        result = install_bundle(args.bundle, expected_commit=args.source_commit, manifest_sha256=args.manifest_sha256,
            reviewed_inventory_sha256=args.reviewed_inventory_sha256 or inspect()["inventorySha256"],
            expected_executor_sha256=args.expected_executor_sha256 or _sha(paths.entry), paths=paths,
            inspect_runtime=inspect, require_idle=idle, apply=args.apply, qualification_artifact=args.artifact_id)
        print(json.dumps(result, sort_keys=True))
        return 0
    except (InstallError, OSError, ValueError) as error:
        print("Static executor operation refused: " + str(error), file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
