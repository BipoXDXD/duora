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
RECEIVER_PATH = Path("/opt/coolify-release/receiver.py")
RECEIVER_SHA256 = "e9af03f08162ecf2bda094d38511b44b810043863d3104f60ea1046ae687f3b3"
LEGACY_RECEIVER_SHA256 = "1ccd1ada4777c80081c4f9bb2a64a500b1e221f794406319504d9e66cbb975cb"


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
        result: dict = {str(path): (receiver_binding() if path == RECEIVER_PATH else hashlib.sha256(_protected_bytes(path)).hexdigest())
                        for path in files}
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
    try:
        path = Path(pin["compose"])
        _require_root_path(path)
        checksum = pin["compose_sha256"]
        if (not path.is_absolute() or not isinstance(checksum, str) or not re.fullmatch(r"[a-f0-9]{64}", checksum)
                or hashlib.sha256(_protected_bytes(path)).hexdigest() != checksum):
            raise InstallError("Actual observer-pinned Compose identity differs")
        return path
    except (OSError, ValueError, TypeError, KeyError) as error:
        raise InstallError("Cannot qualify the actual observer-pinned Compose") from error


def _require_root_path(path: Path, *, directory: bool = False) -> None:
    if not path.is_absolute(): raise InstallError("Absolute protected root path required")
    for item in (path, *path.parents):
        info = item.lstat()
        expected = stat.S_ISDIR if directory or item != path else stat.S_ISREG
        if not expected(info.st_mode) or info.st_uid != 0 or info.st_mode & 0o022:
            raise InstallError("Root-owned path and ancestry without symlinks or unprivileged writes required")


def receiver_binding() -> dict:
    """The only symlink exception: the exact qualified receiver leaf and immutable 2.1.1 target."""
    try:
        _require_root_path(RECEIVER_PATH.parent, directory=True)
        info = RECEIVER_PATH.lstat()
        if info.st_uid != 0 or info.st_nlink != 1: raise InstallError("Unowned receiver entry")
        raw_link = None
        if stat.S_ISLNK(info.st_mode):
            # Linux symlink permission bits are normally 0777 and do not authorize modifying its target.
            raw_link = os.readlink(RECEIVER_PATH)
            target = RECEIVER_PATH.parent / "versions" / ("2.1.1-" + RECEIVER_SHA256) / "receiver.py"
            if raw_link != str(target): raise InstallError("Receiver link is outside the exact qualified version")
        else:
            target = RECEIVER_PATH
        _require_root_path(target)
        resolved = RECEIVER_PATH.resolve(strict=True)
        if resolved != target: raise InstallError("Receiver resolution differs from the qualified target")
        content = _protected_bytes(target)
        checksum = hashlib.sha256(content).hexdigest()
        if checksum != RECEIVER_SHA256: raise InstallError("Receiver source differs from qualified 2.1.1")
        after = RECEIVER_PATH.lstat()
        identity = lambda value: (value.st_dev, value.st_ino, value.st_uid, value.st_gid, value.st_nlink, value.st_mode)
        if identity(info) != identity(after) or (raw_link is not None and os.readlink(RECEIVER_PATH) != raw_link):
            raise InstallError("Receiver changed during protected inspection")
        target_info = target.lstat()
        return {"kind": "qualified-versioned-symlink" if raw_link is not None else "qualified-regular",
                "readlink": raw_link, "resolvedTarget": str(resolved), "sha256": checksum,
                "entry": {"uid": info.st_uid, "gid": info.st_gid, "mode": stat.S_IMODE(info.st_mode), "nlink": info.st_nlink},
                "target": {"uid": target_info.st_uid, "gid": target_info.st_gid,
                           "mode": stat.S_IMODE(target_info.st_mode), "nlink": target_info.st_nlink}}
    except (OSError, ValueError, TypeError) as error:
        raise InstallError("Cannot bind the protected qualified receiver") from error


def _legacy_receiver_binding() -> dict:
    """The single legacy preimage; never used by ordinary ingress or recovery binding."""
    try:
        _require_root_path(RECEIVER_PATH.parent, directory=True)
        before = RECEIVER_PATH.lstat()
        if before.st_uid != 0 or before.st_nlink != 1:
            raise InstallError("Unowned legacy receiver entry")
        link = os.readlink(RECEIVER_PATH) if stat.S_ISLNK(before.st_mode) else None
        target = (RECEIVER_PATH.parent / "versions" / ("2.0.2-" + LEGACY_RECEIVER_SHA256) / "receiver.py"
                  if link is not None else RECEIVER_PATH)
        if link is not None and link != str(target):
            raise InstallError("Legacy receiver target differs from the single qualified version")
        _require_root_path(target)
        if RECEIVER_PATH.resolve(strict=True) != target:
            raise InstallError("Legacy receiver resolution changed")
        if hashlib.sha256(_protected_bytes(target)).hexdigest() != LEGACY_RECEIVER_SHA256:
            raise InstallError("Legacy receiver preimage differs")
        after = RECEIVER_PATH.lstat()
        if ((before.st_dev, before.st_ino, before.st_uid, before.st_gid, before.st_mode, before.st_nlink)
                != (after.st_dev, after.st_ino, after.st_uid, after.st_gid, after.st_mode, after.st_nlink)
                or (link is not None and os.readlink(RECEIVER_PATH) != link)):
            raise InstallError("Legacy receiver changed during inspection")
        target_info = target.lstat()
        return {"kind": "qualified-versioned-symlink" if link is not None else "qualified-regular",
                "readlink": link, "resolvedTarget": str(target), "sha256": LEGACY_RECEIVER_SHA256,
                "entry": {"uid": before.st_uid, "gid": before.st_gid,
                          "mode": stat.S_IMODE(before.st_mode), "nlink": before.st_nlink},
                "target": {"uid": target_info.st_uid, "gid": target_info.st_gid,
                           "mode": stat.S_IMODE(target_info.st_mode), "nlink": target_info.st_nlink}}
    except (OSError, ValueError, TypeError) as error:
        raise InstallError("Cannot bind the exact legacy cohort preimage") from error


def _cohort_receiver_state() -> dict:
    # This two-state classifier is private to authenticated static transactions.
    # receiver_binding() itself continues to refuse the legacy receiver.
    try:
        return receiver_binding()
    except InstallError:
        return _legacy_receiver_binding()


def _new_receiver_target() -> Path:
    return RECEIVER_PATH.parent / "versions" / ("2.1.1-" + RECEIVER_SHA256) / "receiver.py"


def _cohort_after_binding() -> dict:
    return {"kind": "qualified-versioned-symlink", "readlink": str(_new_receiver_target()),
            "resolvedTarget": str(_new_receiver_target()), "sha256": RECEIVER_SHA256,
            "entry": {"uid": 0, "gid": 0, "mode": 0o777, "nlink": 1},
            "target": {"uid": 0, "gid": 0, "mode": 0o755, "nlink": 1}}


def _cohort_executor_link(path: Path, allowed_targets: set[str]) -> None:
    """Authenticate the link node itself, separately from the bytes it references."""
    _require_root_path(path.parent, directory=True)
    before = path.lstat()
    if (not stat.S_ISLNK(before.st_mode) or before.st_uid != 0 or before.st_gid != 0
            or before.st_nlink != 1 or stat.S_IMODE(before.st_mode) != 0o777):
        raise InstallError("Foreign executor link owner, group, mode or hardlink count")
    raw = os.readlink(path)
    if raw not in allowed_targets:
        raise InstallError("Executor link points outside the qualified cohort targets")
    target = Path(raw)
    _require_root_path(target)
    info = target.lstat()
    if info.st_gid != 0 or info.st_nlink != 1 or stat.S_IMODE(info.st_mode) != 0o755:
        raise InstallError("Executor target metadata differs from the qualified cohort")
    after = path.lstat()
    if ((before.st_dev, before.st_ino, before.st_uid, before.st_gid, before.st_mode, before.st_nlink)
            != (after.st_dev, after.st_ino, after.st_uid, after.st_gid, after.st_mode, after.st_nlink)
            or os.readlink(path) != raw):
        raise InstallError("Executor link changed during cohort authentication")


def _cohort_plan(source: Path, manifest: dict, paths: InstallPaths, expected_executor_sha256: str) -> dict:
    if os.geteuid() != 0 or os.getegid() != 0:
        raise InstallError("Receiver cohort transition requires the administrative root identity")
    _require_root_path(source)
    if hashlib.sha256(_protected_bytes(source)).hexdigest() != RECEIVER_SHA256:
        raise InstallError("Candidate receiver is not the exact qualified e9 source")
    before = _legacy_receiver_binding()
    if before["entry"]["gid"] != 0 or before["target"]["gid"] != 0:
        raise InstallError("Cohort preimages require the reviewed root group ownership")
    if manifest["files"]["duora_deploy.py"]["sha256"] != expected_executor_sha256:
        raise InstallError("Cohort transition cannot change executor code at the same time")
    _require_root_path(paths.entry.parent, directory=True)
    info = paths.entry.lstat()
    if (not stat.S_ISLNK(info.st_mode) or info.st_uid != 0 or info.st_gid != 0
            or info.st_nlink != 1 or stat.S_IMODE(info.st_mode) != 0o777):
        raise InstallError("Cohort transition requires the qualified installed versioned executor")
    link = os.readlink(paths.entry)
    target = Path(link)
    if (not target.is_absolute() or target.name != "duora_deploy.py"
            or target.parent.parent != paths.versions or not re.fullmatch(r"[a-f0-9]{40}", target.parent.name)):
        raise InstallError("Legacy executor target is outside the qualified version inventory")
    _require_root_path(target)
    target_info = target.lstat()
    if target_info.st_gid != 0 or stat.S_IMODE(target_info.st_mode) != 0o755:
        raise InstallError("Legacy executor target mode or group differs")
    old_receipt_raw = _protected_bytes(paths.state / "executor-install-receipt.json")
    old_receipt = guard.decode_json(old_receipt_raw)
    old_manifest = validate_bundle(target.parent, expected_commit=target.parent.name,
                                   manifest_sha256=old_receipt["manifestSha256"])
    if any(old_manifest["files"][name] != manifest["files"][name]
           for name in FILES if name != "install_executor.py"):
        raise InstallError("Cohort transition must preserve all executor and sidecar bytes")
    baseline_raw = _protected_bytes(paths.state / guard.BASELINE_NAME)
    if (old_receipt.get("status") != "installed" or old_receipt["sourceCommit"] != target.parent.name
            or old_receipt["files"] != old_manifest["files"]
            or old_receipt["executorSha256"] != expected_executor_sha256
            or _sha(paths.entry) != expected_executor_sha256
            or old_receipt["baselineSha256"] != hashlib.sha256(baseline_raw).hexdigest()):
        raise InstallError("Legacy executor receipt, baseline or preimage differs")
    return {"schema": 1, "receiverBefore": before, "receiverAfter": _cohort_after_binding(),
            "executorBefore": {"readlink": link, "sha256": expected_executor_sha256},
            "oldReceiptSha256": hashlib.sha256(old_receipt_raw).hexdigest(),
            "oldBaselineSha256": hashlib.sha256(baseline_raw).hexdigest()}


def _stage_cohort_receiver(source: Path) -> None:
    target = _new_receiver_target()
    _require_root_path(target.parent.parent, directory=True)
    if not target.parent.exists():
        target.parent.mkdir(mode=0o700)
        _sync(target.parent.parent)
    _require_root_path(target.parent, directory=True)
    if target.exists() or target.is_symlink():
        _require_root_path(target)
        if hashlib.sha256(_protected_bytes(target)).hexdigest() != RECEIVER_SHA256:
            raise InstallError("Existing receiver version cannot be overwritten")
    else:
        content = _protected_bytes(source)
        if hashlib.sha256(content).hexdigest() != RECEIVER_SHA256:
            raise InstallError("Candidate receiver changed during staging")
        _write(target, content, mode=0o755)
    info = target.lstat()
    if info.st_gid != 0 or stat.S_IMODE(info.st_mode) != 0o755:
        raise InstallError("Staged receiver ownership or mode differs")


def _cohort_record(journal: dict, paths: InstallPaths, preimage: Path) -> dict | None:
    if "receiverCohort" not in journal:
        return None
    cohort = journal["receiverCohort"]
    if (not isinstance(cohort, dict) or set(cohort) != {"schema", "receiverBefore", "receiverAfter",
            "executorBefore", "oldReceiptSha256", "oldBaselineSha256", "inventorySha256"}
            or any(not isinstance(cohort[name], dict) for name in ("receiverBefore", "receiverAfter", "executorBefore"))
            or type(cohort["schema"]) is not int or cohort["schema"] != 1
            or cohort["receiverBefore"].get("sha256") != LEGACY_RECEIVER_SHA256
            or cohort["receiverAfter"] != _cohort_after_binding()
            or set(cohort["executorBefore"]) != {"readlink", "sha256"}
            or cohort["executorBefore"]["sha256"] != journal["oldExecutorSha256"]
            or cohort["executorBefore"]["sha256"] != journal["installedReceipt"]["executorSha256"]
            or cohort["inventorySha256"] != journal["installedReceipt"]["inventorySha256"]
            or cohort["oldReceiptSha256"] != journal["oldReceiptSha256"]
            or cohort["oldBaselineSha256"] != journal["oldBaselineSha256"]):
        raise InstallError("Unsupported or inconsistent receiver cohort journal")
    old = cohort["receiverBefore"]
    binding_keys = {"kind", "readlink", "resolvedTarget", "sha256", "entry", "target"}
    if (set(old) != binding_keys or set(cohort["receiverAfter"]) != binding_keys
            or any(not isinstance(binding.get(key), dict) or set(binding[key]) != {"uid", "gid", "mode", "nlink"}
                   for binding in (old, cohort["receiverAfter"]) for key in ("entry", "target"))):
        raise InstallError("Receiver cohort binding inventory differs")
    if any(type(value) is not int for key in ("entry", "target") for value in old[key].values()):
        raise InstallError("Receiver preimage metadata must use exact integer fields")
    if any(old[key]["uid"] != 0 or old[key]["gid"] != 0 or old[key]["nlink"] != 1
           for key in ("entry", "target")):
        raise InstallError("Receiver preimage is outside the reviewed root ownership")
    expected_old_target = RECEIVER_PATH.parent / "versions" / ("2.0.2-" + LEGACY_RECEIVER_SHA256) / "receiver.py"
    if (old.get("kind") not in {"qualified-versioned-symlink", "qualified-regular"}
            or old.get("readlink") not in (None, str(expected_old_target))
            or old.get("resolvedTarget") != str(expected_old_target if old["readlink"] is not None else RECEIVER_PATH)):
        raise InstallError("Journal contains an unqualified receiver preimage")
    if (old["kind"] != ("qualified-versioned-symlink" if old["readlink"] is not None else "qualified-regular")
            or (old["readlink"] is None and old["entry"] != old["target"])):
        raise InstallError("Receiver preimage kind and target metadata disagree")
    saved = preimage / "receiver-before"
    info = saved.lstat()
    if (info.st_uid != 0 or info.st_nlink != 1 or info.st_gid != old["entry"]["gid"]
            or stat.S_IMODE(info.st_mode) != old["entry"]["mode"]):
        raise InstallError("Receiver preimage ownership changed")
    if old["readlink"] is not None:
        if not saved.is_symlink() or os.readlink(saved) != old["readlink"]:
            raise InstallError("Receiver preimage link changed")
        _require_root_path(expected_old_target)
        content = _protected_bytes(expected_old_target)
        info_old = expected_old_target.lstat()
        actual_old = {"uid": info_old.st_uid, "gid": info_old.st_gid,
                      "mode": stat.S_IMODE(info_old.st_mode), "nlink": info_old.st_nlink}
        if actual_old != old["target"]:
            raise InstallError("Legacy receiver target metadata changed before recovery")
    else:
        content = _protected_bytes(saved)
        if stat.S_IMODE(info.st_mode) != old["entry"]["mode"]:
            raise InstallError("Receiver regular preimage mode changed")
    if hashlib.sha256(content).hexdigest() != LEGACY_RECEIVER_SHA256:
        raise InstallError("Receiver preimage bytes changed")
    old_executor = Path(cohort["executorBefore"]["readlink"])
    if (old_executor.parent.parent != paths.versions or old_executor.name != "duora_deploy.py"
            or not re.fullmatch(r"[a-f0-9]{40}", old_executor.parent.name)):
        raise InstallError("Receiver cohort journal contains a foreign executor target")
    _require_root_path(old_executor)
    old_executor_info = old_executor.lstat()
    if (old_executor_info.st_gid != 0 or old_executor_info.st_nlink != 1
            or stat.S_IMODE(old_executor_info.st_mode) != 0o755
            or _sha(old_executor) != cohort["executorBefore"]["sha256"]):
        raise InstallError("Legacy executor target bytes or metadata changed")
    commit = journal["installedReceipt"]["sourceCommit"]
    if not isinstance(commit, str) or not re.fullmatch(r"[a-f0-9]{40}", commit):
        raise InstallError("Invalid cohort executor source identity")
    _cohort_executor_link(paths.entry, {str(old_executor), str(paths.versions / commit / "duora_deploy.py")})
    _cohort_executor_link(preimage / "duora-deploy", {str(old_executor)})
    actual = _cohort_receiver_state()
    if actual not in (old, cohort["receiverAfter"]):
        raise InstallError("Intervening receiver entry or target; refusing overwrite")
    temporary = RECEIVER_PATH.with_name(".receiver-cohort-" + preimage.name)
    if temporary.exists() or temporary.is_symlink():
        temp_info = temporary.lstat()
        if (not temporary.is_symlink() or temp_info.st_uid != 0 or temp_info.st_nlink != 1
                or os.readlink(temporary) not in {str(_new_receiver_target()), old["readlink"]}):
            raise InstallError("Intervening receiver temporary entry")
    return cohort


def _cohort_runtime(cohort: dict, inspect_runtime: Callable[[], dict] | None) -> None:
    if inspect_runtime is None:
        raise InstallError("Cohort recovery requires the reviewed read-only runtime and release context")
    actual = inspect_runtime()
    digest = guard.digest({"profile": actual["profile"], "audit": actual["audit"]})
    if digest != actual["inventorySha256"] or digest != cohort["inventorySha256"]:
        raise InstallError("Cohort runtime or current/previous release context changed")


def _cohort_receiver_replace(preimage: Path, binding: dict) -> None:
    temporary = RECEIVER_PATH.with_name(".receiver-cohort-" + preimage.name)
    if temporary.is_symlink():
        temporary.unlink()
    if binding["readlink"] is not None:
        temporary.symlink_to(binding["readlink"])
        _sync(temporary.parent)
        temporary.replace(RECEIVER_PATH)
        _sync(RECEIVER_PATH.parent)
    else:
        _write(RECEIVER_PATH, _protected_bytes(preimage / "receiver-before"), mode=binding["entry"]["mode"])


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


def _recover_locked(paths: InstallPaths, pending: dict, require_idle: Callable[[], None],
                    expected_bundle: tuple[str, str] | None = None,
                    inspect_runtime: Callable[[], dict] | None = None) -> dict:
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
    if expected_bundle is not None and (receipt["sourceCommit"], receipt["manifestSha256"]) != expected_bundle:
        raise InstallError("Recovery bundle differs from the protected own-attempt journal")
    commit = receipt["sourceCommit"]
    if not re.fullmatch(r"[a-f0-9]{40}", commit): raise InstallError("Invalid recovery source")
    version = paths.versions / commit
    validate_bundle(version, expected_commit=commit, manifest_sha256=receipt["manifestSha256"])
    target = version / "duora_deploy.py"
    previous = preimage / "duora-deploy"
    cohort = _cohort_record(journal, paths, preimage)
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
    if cohort is not None:
        if (not previous.is_symlink() or os.readlink(previous) != cohort["executorBefore"]["readlink"]
                or _sha(previous) != cohort["executorBefore"]["sha256"]):
            raise InstallError("Cohort executor preimage differs from its reviewed identity")
        _cohort_runtime(cohort, inspect_runtime)
    # Do not import an unvalidated active receiver through the idle callback.
    # Both receiver and executor states have been authenticated before this call or any write.
    require_idle()
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
    if cohort is not None:
        _cohort_record(journal, paths, preimage)
        _cohort_receiver_replace(preimage, cohort["receiverBefore"])
        if _legacy_receiver_binding() != cohort["receiverBefore"]:
            raise InstallError("Restored receiver differs; keep the durable fence")
        _cohort_runtime(cohort, inspect_runtime)
        _write(preimage / "cohort-phase.json", _json({"attemptSha256": pending["attemptSha256"],
                                                     "phase": "pair-restored"}))
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


def recover_installation(*, paths: InstallPaths, pending: dict, require_idle: Callable[[], None],
                         expected_bundle: tuple[str, str] | None = None,
                         inspect_runtime: Callable[[], dict] | None = None) -> dict:
    try:
        with _locks(paths): return _recover_locked(paths, pending, require_idle, expected_bundle, inspect_runtime)
    except (OSError, ValueError, KeyError, TypeError) as error:
        raise InstallError("Own-attempt recovery refused without overwriting foreign state") from error


def install_bundle(source: Path, *, expected_commit: str, manifest_sha256: str,
                   reviewed_inventory_sha256: str | None, expected_executor_sha256: str,
                   paths: InstallPaths, inspect_runtime: Callable[[], dict],
                   require_idle: Callable[[], None], apply: bool = False,
                   qualification_artifact: int | None = None,
                   _cohort_receiver_source: Path | None = None,
                   reviewed_cohort_sha256: str | None = None) -> dict:
    manifest = validate_bundle(source, expected_commit=expected_commit, manifest_sha256=manifest_sha256)
    try:
        with _locks(paths):
            fence_dir = paths.ingress / FENCE_DIRECTORY
            fence = fence_dir / "pending.json"
            if ((paths.state / "executor-install-pending.json").exists() or fence.exists() or fence.is_symlink()):
                raise InstallError("Resolve the outstanding interrupted installation explicitly first")
            cohort = (_cohort_plan(_cohort_receiver_source, manifest, paths, expected_executor_sha256)
                      if _cohort_receiver_source is not None else None)
            if cohort is None and reviewed_cohort_sha256 is not None:
                raise InstallError("Cohort approval cannot alter ordinary installer binding")
            require_idle()
            if paths.entry.is_symlink() and not Path(os.readlink(paths.entry)).is_absolute():
                raise InstallError("Relative previous executor target requires explicit administrative review")
            if _sha(paths.entry) != expected_executor_sha256: raise InstallError("Executor preimage changed")
            inventory = inspect_runtime()
            guard.require_preserved(inventory["profile"], inventory["profile"])
            actual_inventory = guard.digest({"profile": inventory["profile"], "audit": inventory["audit"]})
            readonly_cohort = cohort is not None and not apply and reviewed_inventory_sha256 is None
            if (actual_inventory != inventory["inventorySha256"]
                    or (not readonly_cohort and actual_inventory != reviewed_inventory_sha256)):
                raise InstallError("Inventory differs from the explicitly reviewed reference")
            cohort_result = {}
            if cohort is not None:
                cohort["inventorySha256"] = actual_inventory
                cohort_sha256 = guard.digest(cohort)
                if apply and cohort_sha256 != reviewed_cohort_sha256:
                    raise InstallError("Cohort differs from its explicitly reviewed static preimages")
                cohort_result = {"cohortSha256": cohort_sha256, "receiverBefore": cohort["receiverBefore"],
                                 "receiverAfter": cohort["receiverAfter"]}
            receipt = {"schema": 1, "status": "preflight", "sourceCommit": expected_commit,
                "manifestSha256": manifest_sha256, "inventorySha256": actual_inventory,
                "baselineSha256": hashlib.sha256(_json(inventory["profile"])).hexdigest(),
                "executorSha256": manifest["files"]["duora_deploy.py"]["sha256"], "files": manifest["files"],
                "qualificationRun": manifest["qualificationRun"], "qualificationArtifactId": qualification_artifact}
            if not apply:
                return {**receipt, "inventory": inventory, **cohort_result}
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
            if cohort is not None:
                assert _cohort_receiver_source is not None
                _stage_cohort_receiver(_cohort_receiver_source)
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
            if cohort is not None:
                saved_receiver = preimage / "receiver-before"
                if cohort["receiverBefore"]["readlink"] is not None:
                    saved_receiver.symlink_to(cohort["receiverBefore"]["readlink"])
                    _sync(preimage)
                else:
                    _write(saved_receiver, _protected_bytes(RECEIVER_PATH), mode=cohort["receiverBefore"]["entry"]["mode"])
            receipt.update(status="installed", appliedAt=int(time.time()), preimage=str(preimage))
            journal = {"installedReceipt": receipt, "oldExecutorSha256": expected_executor_sha256,
                "oldBaselineSha256": hashlib.sha256(old_baseline).hexdigest() if old_baseline is not None else None,
                "oldReceiptSha256": hashlib.sha256(old_receipt).hexdigest() if old_receipt is not None else None}
            if cohort is not None:
                if (journal["oldReceiptSha256"] != cohort["oldReceiptSha256"]
                        or journal["oldBaselineSha256"] != cohort["oldBaselineSha256"]):
                    raise InstallError("Legacy cohort baseline or receipt changed before journaling")
                journal["receiverCohort"] = cohort
            journal_raw = _json(journal)
            _write(preimage / "attempt.json", journal_raw)
            _sync(paths.preimages)
            pending = {"attempt": preimage.name, "attemptSha256": hashlib.sha256(journal_raw).hexdigest()}
            if cohort is not None:
                _cohort_record(journal, paths, preimage)
                if (_legacy_receiver_binding() != cohort["receiverBefore"]
                        or os.readlink(paths.entry) != cohort["executorBefore"]["readlink"]):
                    raise InstallError("Cohort changed before activation")
                _write(preimage / "cohort-phase.json", _json({"attemptSha256": pending["attemptSha256"],
                                                             "phase": "prepared"}))
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
                if cohort is not None:
                    _cohort_receiver_replace(preimage, cohort["receiverAfter"])
                    if receiver_binding() != cohort["receiverAfter"]:
                        raise InstallError("Activated receiver differs from the exact planned e9 binding")
                    _write(preimage / "cohort-phase.json", _json({"attemptSha256": pending["attemptSha256"],
                                                                 "phase": "receiver-active"}))
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
                if cohort is not None:
                    if receiver_binding() != cohort["receiverAfter"]:
                        raise InstallError("Receiver changed before completing the coherent pair")
                    _write(preimage / "cohort-phase.json", _json({"attemptSha256": pending["attemptSha256"],
                                                                 "phase": "pair-verified"}))
                _write(paths.state / "executor-install-receipt.json", _json(receipt))
                (paths.state / "executor-install-pending.json").unlink()
                _sync(paths.state)
                fence.unlink()
                _sync(fence_dir)
                return {**receipt, **cohort_result}
            except Exception:
                _recover_locked(paths, pending, require_idle, inspect_runtime=inspect_runtime)
                raise
            finally:
                if temporary_link.is_symlink() and os.readlink(temporary_link) == str(version / "duora_deploy.py"):
                    temporary_link.unlink()
    except Exception as error:
        raise InstallError("Static installation/preflight refused; no product deployment invoked") from error


def transition_cohort(source: Path, receiver_source: Path, *, expected_commit: str, manifest_sha256: str,
                      reviewed_inventory_sha256: str | None, expected_executor_sha256: str, paths: InstallPaths,
                      inspect_runtime: Callable[[], dict], require_idle: Callable[[], None],
                      reviewed_cohort_sha256: str | None = None, apply: bool = False,
                      qualification_artifact: int | None = None) -> dict:
    """One existing lock/journal transaction; never a nested install or a product deployment."""
    result = install_bundle(source, expected_commit=expected_commit, manifest_sha256=manifest_sha256,
        reviewed_inventory_sha256=reviewed_inventory_sha256, expected_executor_sha256=expected_executor_sha256,
        paths=paths, inspect_runtime=inspect_runtime, require_idle=require_idle, apply=apply,
        qualification_artifact=qualification_artifact, _cohort_receiver_source=receiver_source,
        reviewed_cohort_sha256=reviewed_cohort_sha256)
    return {**result, "status": "cohort-installed" if apply else "cohort-preflight"}


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


def _production_idle(paths: InstallPaths, *, expected_commit: str, manifest_sha256: str,
                     receiver_cohort: bool = False) -> None:
    binding = _cohort_receiver_state() if receiver_cohort else receiver_binding()
    receiver = _module(Path(binding["resolvedTarget"]), "duora_installer_reviewed_receiver")
    require_ingress_idle(receiver, paths, expected_commit=expected_commit, manifest_sha256=manifest_sha256)
    if receiver.api("deployments") != []:
        raise InstallError("Coolify global queue is active or unknown")


def _cohort_recovery_mode(paths: InstallPaths, pending: dict, expected_bundle: tuple[str, str]) -> bool:
    """Authenticate the local attempt before selecting the narrowly scoped legacy binding."""
    if (set(pending) != {"attempt", "attemptSha256"}
            or not isinstance(pending["attempt"], str)
            or not re.fullmatch(r"installation-[a-z0-9_]+", pending["attempt"])
            or not isinstance(pending["attemptSha256"], str)
            or not re.fullmatch(r"[a-f0-9]{64}", pending["attemptSha256"])):
        raise InstallError("Invalid recovery attempt identity")
    fence = paths.ingress / FENCE_DIRECTORY / "pending.json"
    if guard.decode_json(_protected_bytes(fence)) != pending:
        raise InstallError("Recovery fence differs from the exact requested attempt")
    preimage = paths.preimages / pending["attempt"]
    _require_root_path(preimage, directory=True)
    if preimage.stat().st_mode & 0o077:
        raise InstallError("Recovery attempt directory is not private")
    raw = _protected_bytes(preimage / "attempt.json")
    if hashlib.sha256(raw).hexdigest() != pending["attemptSha256"]:
        raise InstallError("Recovery journal hash differs")
    journal = guard.decode_json(raw)
    receipt = journal["installedReceipt"]
    if (receipt["sourceCommit"], receipt["manifestSha256"]) != expected_bundle:
        raise InstallError("Recovery source or bundle differs from the owned attempt")
    validate_bundle(paths.versions / expected_bundle[0], expected_commit=expected_bundle[0],
                    manifest_sha256=expected_bundle[1])
    return _cohort_record(journal, paths, preimage) is not None


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
    parser.add_argument("--cohort-receiver", type=Path)
    parser.add_argument("--reviewed-cohort-sha256")
    args = parser.parse_args()
    try:
        if os.geteuid() != 0: raise InstallError("Administrative root entry required")
        os.umask(0o077)
        _require_root_path(args.bundle, directory=True)
        manifest = validate_bundle(args.bundle, expected_commit=args.source_commit, manifest_sha256=args.manifest_sha256)
        if _sha(Path(__file__)) != manifest["files"]["install_executor.py"]["sha256"]:
            raise InstallError("Invoke the installer from the exact verified bundle")
        if args.artifact_id <= 0: raise InstallError("Exact qualification artifact ID required")
        paths = InstallPaths(Path("/usr/local/sbin/duora-deploy"), Path("/opt/duora-executor/releases"),
            Path("/opt/duora-executor/preimages"), Path("/var/lib/duora"), Path("/var/lib/coolify-release"))
        recovering = bool(args.recover_attempt or args.recover_attempt_sha256)
        pending = None
        cohort_mode = args.cohort_receiver is not None
        if recovering:
            if (args.apply or not args.recover_attempt or not args.recover_attempt_sha256
                    or args.cohort_receiver is not None or args.reviewed_cohort_sha256 is not None):
                raise InstallError("Recovery is separate from apply/new cohort and requires exact attempt and journal SHA")
            pending = {"attempt": args.recover_attempt, "attemptSha256": args.recover_attempt_sha256}
            cohort_mode = _cohort_recovery_mode(paths, pending, (args.source_commit, args.manifest_sha256))
        elif args.reviewed_cohort_sha256 is not None and not cohort_mode:
            raise InstallError("Cohort approval requires the explicit candidate receiver source")
        def idle() -> None:
            _production_idle(paths, expected_commit=args.source_commit, manifest_sha256=args.manifest_sha256,
                             receiver_cohort=cohort_mode)
        if recovering and not cohort_mode:
            assert pending is not None
            result = recover_installation(paths=paths, pending=pending, require_idle=idle,
                expected_bundle=(args.source_commit, args.manifest_sha256))
            print(json.dumps(result, sort_keys=True))
            return 0
        # A new inspection/apply must qualify online. Own-attempt recovery instead relies on the protected
        # local bundle/journal/fence under all three locks, so API outages/retention cannot strand the host.
        if not recovering:
            _verify_provenance(manifest, args.artifact_id)
        executor = _module(args.bundle / "duora_deploy.py", "duora_candidate_executor")
        pinned = Path("/etc/duora/coolify-deploy.json")
        pin_raw = _protected_bytes(pinned)
        pin = guard.decode_json(pin_raw)
        deploy_paths = executor.Paths(pinned_compose(pin), Path("/etc/duora/duora.env"), paths.state)
        preserved = (deploy_paths.compose_file, deploy_paths.host_env, pinned, Path(pin["caddy"]),
                     Path(pin["manifest"]), RECEIVER_PATH, paths.ingress / "duora/current.json")
        if cohort_mode:
            preserved = tuple(path for path in preserved if path != RECEIVER_PATH) + (paths.state / "previous.json",)
        context = installation_context(paths, preserved)
        if hashlib.sha256(pin_raw).hexdigest() != context[str(pinned)]:
            raise InstallError("Pinned configuration changed during initial inspection")
        current = guard.decode_json(_protected_bytes(paths.state / "current.json"))["releaseId"]
        raw = _protected_bytes(paths.state / "releases" / (current + ".json")).decode("utf-8")
        release = executor.parse_manifest(raw, image_prefix="ghcr.io/bipoxdxd/duora-")
        require_installation_context(context, paths, preserved)
        def inspect() -> dict:
            require_installation_context(context, paths, preserved)
            # Cohort inspection must never import an active entry by a matching directory basename.
            # Both pre/post snapshots use the candidate whose complete bundle is already authenticated.
            module = executor if cohort_mode else (_module(paths.entry, "duora_activated_executor")
                if paths.entry.is_symlink() and paths.entry.resolve().parent.name == args.source_commit else executor)
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
        if recovering:
            assert pending is not None and cohort_mode
            result = recover_installation(paths=paths, pending=pending, require_idle=idle,
                expected_bundle=(args.source_commit, args.manifest_sha256), inspect_runtime=inspect)
            print(json.dumps(result, sort_keys=True))
            return 0
        if args.apply and (not args.reviewed_inventory_sha256 or not args.expected_executor_sha256):
            raise InstallError("Apply requires both explicitly reviewed inventory and executor preimage hashes")
        if args.apply and cohort_mode and not args.reviewed_cohort_sha256:
            raise InstallError("Cohort apply requires the explicitly reviewed two-entry preflight digest")
        # Inspection still holds the real locks and idle gate. It never writes enrollment or code.
        if cohort_mode:
            assert args.cohort_receiver is not None
            result = transition_cohort(args.bundle, args.cohort_receiver,
                expected_commit=args.source_commit, manifest_sha256=args.manifest_sha256,
                reviewed_inventory_sha256=args.reviewed_inventory_sha256,
                reviewed_cohort_sha256=args.reviewed_cohort_sha256,
                expected_executor_sha256=args.expected_executor_sha256 or manifest["files"]["duora_deploy.py"]["sha256"], paths=paths,
                inspect_runtime=inspect, require_idle=idle, apply=args.apply, qualification_artifact=args.artifact_id)
        else:
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
