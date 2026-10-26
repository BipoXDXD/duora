#!/usr/bin/env python3
"""Offline, public-input-only packaging. Never invokes a runtime or downloads assets."""
import argparse
import gzip
import hashlib
import io
import json
from pathlib import Path, PurePosixPath
import re
import shutil
import sys
import tarfile
import tempfile

ROOT = Path(__file__).resolve().parent
MAX_ARCHIVE = 64 * 1024 * 1024
MAX_CLOSURE = 512 * 1024 * 1024
MAX_MEMBERS = 30000
RUNNER_SHA256 = "87e09073a60a5c2ab12804c25160fa8d67c8cb53a297a052b0bd4d3733ab8211"
HELPER_FILES = {
    "Duora.IntegrationTests.dll": "11419c7d93a94666d535b5b5cfa2205d2ac0d1f8d0c840297f3eaa2489b044c3",
    "Duora.IntegrationTests.runtimeconfig.json": "fe68161356f78eea34bf996675608947d949623e5e3aded5a5afff9a796185d4",
    "SHA256SUMS": "14a253b4e6c596534f5fde37c8e7be67c1253c7480c15e1916423cd7b914f03a",
}

class Refusal(Exception):
    pass


def sha256(raw):
    return hashlib.sha256(raw).hexdigest()


def canonical(value):
    return (json.dumps(value, sort_keys=True, separators=(",", ":")) + "\n").encode()


def verified_file(path, expected, limit, reason):
    path = Path(path)
    if path.is_symlink() or not path.is_file() or path.stat().st_size > limit:
        raise Refusal(reason)
    with path.open("rb") as stream:
        raw = stream.read(limit + 1)
    if len(raw) > limit or sha256(raw) != expected:
        raise Refusal(reason)
    return raw


def verify_artifact(directory):
    directory = Path(directory)
    if directory.is_symlink() or not directory.is_dir() or {p.name for p in directory.iterdir()} != set(HELPER_FILES):
        raise Refusal("artifact_untrusted")
    return {name: verified_file(directory / name, digest, 1024 * 1024, "artifact_untrusted")
            for name, digest in HELPER_FILES.items()}


def verified_runner(path):
    return verified_file(path, RUNNER_SHA256, 1024 * 1024, "runner_untrusted")


def load_reviewed_aliases():
    lock = json.loads((ROOT / "profile-lock.json").read_bytes())
    metadata = lock["python"]
    if metadata["reviewed_aliases_file"] != "reviewed-python-aliases.json":
        raise Refusal("alias_manifest_invalid")
    raw = verified_file(ROOT / "reviewed-python-aliases.json", metadata["reviewed_aliases_sha256"],
                        512 * 1024, "alias_manifest_untrusted")
    manifest = json.loads(raw)
    aliases = manifest["aliases"]
    if (manifest["schema_version"] != 1 or manifest["archive_sha256"] != metadata["sha256"]
            or manifest["asset_id"] != metadata["asset_id"]
            or len(aliases) != metadata["reviewed_aliases_count"]
            or not all(isinstance(name, str) and isinstance(target, str) for name, target in aliases.items())):
        raise Refusal("alias_manifest_invalid")
    return aliases


REVIEWED_ALIASES = load_reviewed_aliases()


class BoundedReader:
    def __init__(self, stream, limit):
        self.stream, self.remaining = stream, limit

    def read(self, count=-1):
        count = self.remaining + 1 if count < 0 else min(count, self.remaining + 1)
        raw = self.stream.read(count)
        self.remaining -= len(raw)
        if self.remaining < 0:
            raise Refusal("archive_expansion_limit")
        return raw


def unpack_python(stream, destination, expected_sha256, max_bytes=MAX_CLOSURE,
                  reviewed_aliases=None, require_complete_aliases=False):
    """Verify all members before writes; preserve the complete installation as files."""
    destination = Path(destination)
    reviewed_aliases = REVIEWED_ALIASES if reviewed_aliases is None else reviewed_aliases
    raw = stream.read(MAX_ARCHIVE + 1)
    if len(raw) > MAX_ARCHIVE:
        raise Refusal("archive_size_limit")
    if sha256(raw) != expected_sha256:
        raise Refusal("archive_digest_mismatch")
    if destination.exists() or destination.is_symlink():
        raise Refusal("destination_exists")
    files, directories, links, seen, total = {}, set(), {}, set(), 0
    try:
        with gzip.GzipFile(fileobj=io.BytesIO(raw)) as compressed:
            reader = BoundedReader(compressed, max_bytes + 16 * 1024 * 1024)
            with tarfile.open(fileobj=reader, mode="r|") as archive:
                for member in archive:
                    name = member.name.rstrip("/") if member.isdir() else member.name
                    path = PurePosixPath(name)
                    if (not name or "\\" in name or any(ord(c) < 32 for c in name)
                            or path.is_absolute() or ".." in path.parts or str(path) != name
                            or path.parts[0] != "python" or name in seen or len(seen) >= MAX_MEMBERS):
                        raise Refusal("archive_member_invalid")
                    seen.add(name)
                    if member.isdir():
                        directories.add(name)
                    elif member.isfile():
                        if member.size < 0 or member.mode & 0o7000:
                            raise Refusal("archive_member_invalid")
                        total += member.size
                        if total > max_bytes:
                            raise Refusal("archive_expansion_limit")
                        content = archive.extractfile(member).read(member.size + 1)
                        if len(content) != member.size:
                            raise Refusal("archive_member_invalid")
                        files[name] = (content, 0o755 if member.mode & 0o111 else 0o644, None)
                    elif member.issym() and reviewed_aliases.get(name) == member.linkname:
                        links[name] = member.linkname
                    else:
                        raise Refusal("archive_link_or_type_invalid")
        if require_complete_aliases and links != reviewed_aliases:
            raise Refusal("archive_alias_set_invalid")
        regular_files = dict(files)
        for name, link in links.items():
            current, visited = name, set()
            while current in links:
                if current in visited or len(visited) >= 64:
                    raise Refusal("archive_alias_cycle_or_depth")
                visited.add(current)
                target = links[current]
                if (not target or "\\" in target or PurePosixPath(target).is_absolute()
                        or any(ord(char) < 32 for char in target)):
                    raise Refusal("archive_alias_escape")
                components = list(PurePosixPath(current).parent.parts)
                for part in target.split("/"):
                    if part in ("", "."):
                        continue
                    if part == "..":
                        if len(components) <= 1:
                            raise Refusal("archive_alias_escape")
                        components.pop()
                    else:
                        components.append(part)
                if not components or components[0] != "python":
                    raise Refusal("archive_alias_escape")
                current = "/".join(components)
            if current not in regular_files:
                raise Refusal("archive_link_target_invalid")
            content, mode, _ = regular_files[current]
            total += len(content)
            if total > max_bytes:
                raise Refusal("archive_expansion_limit")
            files[name] = (content, mode, link)
        for name in files.keys() | directories:
            if any(str(parent) in files for parent in PurePosixPath(name).parents):
                raise Refusal("archive_parent_invalid")
        if "python/bin/python3.13" not in files or not any(name.startswith("python/lib/python3.13/") for name in files):
            raise Refusal("archive_layout_invalid")
    except (tarfile.TarError, OSError, EOFError, ValueError):
        raise Refusal("archive_invalid") from None
    destination.mkdir(parents=False)
    closure = {}
    try:
        for name in sorted(directories):
            (destination / name).mkdir(parents=True, exist_ok=True)
        for name, (content, mode, link) in sorted(files.items()):
            target = destination / name
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(content)
            target.chmod(mode)
            closure[name] = {"sha256": sha256(content), "bytes": len(content), "mode": mode}
            if link is not None:
                closure[name]["source_link"] = link
    except BaseException:
        shutil.rmtree(destination)
        raise
    return closure


MINIMAL_NATIVE_MODULES = frozenset({
    "_posixsubprocess", "select", "_struct", "_json", "_sha1", "_sha2", "_sha3",
    "_md5", "_blake2", "_random", "_bisect", "math", "binascii", "unicodedata",
})
EXCLUDED_PACKAGES = frozenset({"site-packages", "ensurepip", "idlelib", "tkinter", "turtledemo", "test", "tests"})
RUNTIME_DIRECTORIES = ("python/lib/python3.13/lib-dynload",)


def select_minimal_python(directory, original_inventory=None):
    """Delete only the verified temporary extraction's non-runtime files."""
    directory = Path(directory)
    included, excluded = {}, {}
    names = sorted(original_inventory) if original_inventory is not None else sorted(
        path.relative_to(directory).as_posix() for path in directory.rglob("*") if path.is_file())
    for name in names:
        path = directory / name
        core = name in {"python/bin/python3.13", "python/lib/libpython3.13.so.1.0"}
        stdlib = name.startswith("python/lib/python3.13/")
        native_name = path.name.split(".", 1)[0]
        native = (name.startswith("python/lib/python3.13/lib-dynload/")
                  and native_name in MINIMAL_NATIVE_MODULES
                  and path.name == native_name + ".cpython-313-x86_64-linux-gnu.so")
        pure = stdlib and path.suffix == ".py" and not (set(path.parts) & EXCLUDED_PACKAGES)
        notice = name == "python/lib/python3.13/LICENSE.txt"
        keep = core or native or pure or notice
        if keep:
            raw = path.read_bytes()
            if not (core or native) and raw.startswith(b"\x7fELF"):
                raise Refusal("native_binary_unreviewed")
            record = {"sha256": sha256(raw), "bytes": len(raw)}
            if original_inventory is not None and any(record[k] != original_inventory[name][k] for k in record):
                raise Refusal("minimal_runtime_preimage_changed")
            included[name] = record
        else:
            if original_inventory is not None:
                record = {key: original_inventory[name][key] for key in ("sha256", "bytes")}
            else:
                raw = path.read_bytes()
                record = {"sha256": sha256(raw), "bytes": len(raw)}
            record["reason"] = ("native_not_required" if path.suffix == ".so" else
                                "optional_package_not_required" if set(path.parts) & EXCLUDED_PACKAGES else
                                "outside_minimal_runtime")
            excluded[name] = record
    required = {"python/bin/python3.13", "python/lib/libpython3.13.so.1.0", "python/lib/python3.13/LICENSE.txt"}
    if not required <= included.keys():
        raise Refusal("minimal_runtime_incomplete")
    for name in excluded:
        path = directory / name
        if path.exists():
            path.unlink()
    for path in sorted(directory.rglob("*"), reverse=True):
        if path.is_dir() and not any(path.iterdir()):
            path.rmdir()
    # CPython getpath uses this directory as its exec_prefix landmark even
    # when every selected native module is compiled into the core binaries.
    for name in RUNTIME_DIRECTORIES:
        path = directory / name
        path.mkdir(parents=True, exist_ok=True)
        path.chmod(0o755)
    return {"policy": "explicit_minimal_cpython_runtime_v1", "included": included, "excluded": excluded,
            "required_runtime_directories": list(RUNTIME_DIRECTORIES),
            "compiled_in_modules_removed": False, "native_dependency_license_review": "PENDING"}


def write_context(directory, output):
    """Sorted tar with normalized ownership, time, permissions and no external files."""
    directory, output = Path(directory), Path(output)
    with output.open("xb") as stream:
        try:
            with tarfile.open(fileobj=stream, mode="w", format=tarfile.USTAR_FORMAT) as archive:
                for path in sorted(directory.rglob("*")):
                    if path.is_symlink():
                        raise Refusal("context_link_invalid")
                    if path.is_dir():
                        if path.relative_to(directory).as_posix() in RUNTIME_DIRECTORIES:
                            member = tarfile.TarInfo(path.relative_to(directory).as_posix())
                            member.type, member.mode = tarfile.DIRTYPE, 0o755
                            member.uid, member.gid, member.mtime = 0, 0, 0
                            archive.addfile(member)
                        continue
                    if not path.is_file():
                        raise Refusal("context_type_invalid")
                    raw = path.read_bytes()
                    member = tarfile.TarInfo(path.relative_to(directory).as_posix())
                    member.size, member.uid, member.gid, member.mtime = len(raw), 0, 0, 0
                    member.mode = 0o755 if path.stat().st_mode & 0o111 else 0o644
                    archive.addfile(member, io.BytesIO(raw))
        except BaseException:
            output.unlink()
            raise


def qualification_plan(image):
    if not re.fullmatch(r"sha256:[0-9a-f]{64}", image):
        raise Refusal("image_digest_required")
    return {"state": "NOT_BUILT_NOT_QUALIFIED_NOT_ADMITTED", "image": image,
            "python": "/opt/python/bin/python3.13", "dotnet": "/usr/share/dotnet/dotnet",
            "controls": {"uid_gid": "1654:1654", "network": "none", "read_only": True,
                         "cap_drop": ["ALL"], "no_new_privileges": True,
                         "memory_bytes": 536870912, "memory_swap_bytes": 536870912,
                         "cpus": "0.5", "pids_limit": 64, "tmpfs_bytes": 16777216, "core_bytes": 0},
            "operational_capture_authorized": False}


def notice_inputs(source_root=None):
    """Fixed inventory and flat notice directory only; reject extras and changed bytes."""
    source_root = ROOT if source_root is None else Path(source_root)
    inventory_path = source_root / "python-license-inventory.json"
    notices = source_root / "notices"
    if (inventory_path.is_symlink() or not inventory_path.is_file()
            or inventory_path.stat().st_size > 1024 * 1024 or notices.is_symlink() or not notices.is_dir()):
        raise Refusal("notice_inputs_untrusted")
    inventory_raw = inventory_path.read_bytes()
    try:
        declarations = json.loads(inventory_raw)["supplemental_notice_files"]
        if not isinstance(declarations, list) or not 0 < len(declarations) <= 64:
            raise ValueError()
        files, records = {}, {}
        prefix = "tools/support-contact-runtime/notices/"
        for declaration in declarations:
            name = declaration["path"]
            if not isinstance(name, str) or not name.startswith(prefix):
                raise ValueError()
            name = name[len(prefix):]
            if (not name or "/" in name or "\\" in name or name in (".", "..")
                    or not name.endswith(".txt") or name in files or declaration["verbatim"] is not True
                    or type(declaration["bytes"]) is not int or not 0 < declaration["bytes"] <= 131072):
                raise ValueError()
            raw = verified_file(notices / name, declaration["sha256"], 131072, "notice_inputs_untrusted")
            if len(raw) != declaration["bytes"]:
                raise ValueError()
            files[name] = raw
            records[name] = {"sha256": sha256(raw), "bytes": len(raw)}
        if {path.name for path in notices.iterdir()} != set(files):
            raise ValueError()
    except (OSError, ValueError, KeyError, TypeError):
        raise Refusal("notice_inputs_untrusted") from None
    return inventory_raw, files, {"inventory_sha256": sha256(inventory_raw), "notices": records}


def public_metadata_inputs(lock):
    contract = lock["python"]["profile_license_contract"]
    root = ROOT.parents[1]
    manifest_raw = verified_file(ROOT / "python-license-review-manifest.json", contract["review_manifest_sha256"],
                                 1024 * 1024, "public_metadata_untrusted")
    manifest = json.loads(manifest_raw)
    inventory_raw, notes, _ = notice_inputs()
    fixed = {"tools/support-contact-runtime/python-license-inventory.json": "python-license-inventory.json",
             "tools/support-contact-runtime/upstream-PYTHON.json": "upstream-PYTHON.json",
             "tools/support-contact-runtime/upstream-distribution-provenance.json": "upstream-distribution-provenance.json",
             "docs/testing/support-contact-runtime-licenses.md": "support-contact-runtime-licenses.md"}
    expected = set(fixed) | {"tools/support-contact-runtime/notices/" + name for name in notes}
    rows = manifest["files"]
    if len(rows) != len(expected) or {row["path"] for row in rows} != expected:
        raise Refusal("public_metadata_untrusted")
    files, records = {}, {}
    for row in rows:
        raw = verified_file(root / row["path"], row["sha256"], 1024 * 1024, "public_metadata_untrusted")
        if len(raw) != row["bytes"]:
            raise Refusal("public_metadata_untrusted")
        if row["path"] in fixed:
            name = fixed[row["path"]]
            files[name] = raw
            records[name] = {"sha256": sha256(raw), "bytes": len(raw)}
    if records["upstream-PYTHON.json"]["sha256"] != contract["upstream_python_sha256"]:
        raise Refusal("public_metadata_untrusted")
    provenance = json.loads(files["upstream-distribution-provenance.json"])["full_to_stripped_static_analysis_v2"]
    review = provenance["independent_review"]
    if (provenance["receipt_sha256"] != contract["provenance_v2_receipt_sha256"]
            or review["sha256"] != contract["independent_review_sha256"]
            or sha256(review["content_utf8"].encode()) != contract["independent_review_sha256"]
            or {row["stripped_path"]: row["stripped"]["sha256"] for row in provenance["receipt"]["bindings"]} != contract["core_binaries"]):
        raise Refusal("public_metadata_untrusted")
    files["python-license-review-manifest.json"] = manifest_raw
    files["repository-license-policy.json"] = verified_file(root / "scripts/supply-chain/license-policy.json", contract["policy_sha256"], 1024 * 1024, "public_metadata_untrusted")
    for name in files:
        records[name] = {"sha256": sha256(files[name]), "bytes": len(files[name])}
    return files, records


def prepare(python_archive, artifact_dir, runner, output):
    # No captures, application config, ambient environment or arbitrary directory copies.
    lock_raw = (ROOT / "profile-lock.json").read_bytes()
    lock = json.loads(lock_raw)
    reviewed_aliases = load_reviewed_aliases()
    helper, runner_raw = verify_artifact(artifact_dir), verified_runner(runner)
    inventory_raw, notice_files, notice_manifest = notice_inputs()
    notice_manifest_raw = canonical(notice_manifest)
    public_files, public_records = public_metadata_inputs(lock)
    archive_path = Path(python_archive)
    if archive_path.is_symlink() or not archive_path.is_file() or archive_path.stat().st_size != lock["python"]["size_bytes"]:
        raise Refusal("archive_size_mismatch")
    with tempfile.TemporaryDirectory(prefix="support-contact-public-context-") as tmp:
        context = Path(tmp) / "context"
        with archive_path.open("rb") as stream:
            closure = unpack_python(stream, context, lock["python"]["sha256"],
                                    reviewed_aliases=reviewed_aliases, require_complete_aliases=True)
        original_inventory_raw = canonical(closure)
        selection = select_minimal_python(context, closure)
        closure = {name: record for name, record in closure.items() if name in selection["included"]}
        (context / "python-original-inventory.json").write_bytes(original_inventory_raw)
        selection_raw = canonical(selection)
        (context / "python-selection.json").write_bytes(selection_raw)
        (context / "python-license-inventory.json").write_bytes(inventory_raw)
        (context / "notice-manifest.json").write_bytes(notice_manifest_raw)
        for name, raw in public_files.items():
            (context / name).write_bytes(raw)
        (context / "notices").mkdir()
        for name, raw in notice_files.items():
            (context / "notices" / name).write_bytes(raw)
        artifact = context / "artifact"
        artifact.mkdir()
        for name, raw in helper.items():
            (artifact / name).write_bytes(raw)
        (context / "run_preserved_probe.py").write_bytes(runner_raw)
        (context / "profile-lock.json").write_bytes(lock_raw)
        aliases_raw = verified_file(ROOT / "reviewed-python-aliases.json",
                                    lock["python"]["reviewed_aliases_sha256"], 512 * 1024,
                                    "alias_manifest_untrusted")
        (context / "reviewed-python-aliases.json").write_bytes(aliases_raw)
        dockerfile_raw = (ROOT / "Dockerfile").read_bytes()
        (context / "Dockerfile").write_bytes(dockerfile_raw)
        closure_raw = canonical(closure)
        (context / "python-closure.json").write_bytes(closure_raw)
        manifest = {"state": "NOT_BUILT_NOT_QUALIFIED_NOT_ADMITTED", "schema_version": 1,
                    "base": lock["base"], "python_archive_sha256": lock["python"]["sha256"],
                    "python_closure_sha256": sha256(closure_raw), "profile_lock_sha256": sha256(lock_raw),
                    "python_original_inventory_sha256": sha256(original_inventory_raw),
                    "python_selection_sha256": sha256(selection_raw),
                    "required_runtime_directories": selection["required_runtime_directories"],
                    "python_license_inventory_sha256": sha256(inventory_raw),
                    "notice_manifest_sha256": sha256(notice_manifest_raw),
                    "notices": notice_manifest["notices"],
                    "public_metadata": public_records,
                    "core_binaries": {name: selection["included"][name]["sha256"] for name in lock["python"]["profile_license_contract"]["core_binaries"]},
                    "packaged_public_files": {
                        **{"/opt/" + name: selection["included"][name] for name in lock["python"]["profile_license_contract"]["core_binaries"]},
                        **{"/opt/probe/notices/" + name: item for name, item in notice_manifest["notices"].items()},
                        **{"/opt/probe/provenance/" + name: item for name, item in public_records.items()},
                        "/opt/probe/provenance/notice-manifest.json": {"sha256": sha256(notice_manifest_raw), "bytes": len(notice_manifest_raw)}},
                    "native_dependency_license_review": "PENDING",
                    "reviewed_python_aliases_sha256": sha256(aliases_raw),
                    "dockerfile_sha256": sha256(dockerfile_raw), "packager_sha256": sha256(Path(__file__).read_bytes()),
                    "helper": HELPER_FILES, "runner_sha256": RUNNER_SHA256,
                    "profile_oci_leaf_digest": None, "profile_config_digest": None,
                    "qualification_receipt_sha256": None}
        (context / "construction-manifest.json").write_bytes(canonical(manifest))
        write_context(context, output)
    return manifest


class Parser(argparse.ArgumentParser):
    def error(self, message):
        raise Refusal("arguments_invalid")


def main(argv=None):
    try:
        parser = Parser(description=__doc__)
        subs = parser.add_subparsers(dest="action", required=True)
        package = subs.add_parser("prepare")
        for name in ("python-archive", "artifact-dir", "runner", "output"):
            package.add_argument("--" + name, required=True, type=Path)
        plan = subs.add_parser("qualification-plan")
        plan.add_argument("--image", required=True)
        args = parser.parse_args(argv)
        result = (qualification_plan(args.image) if args.action == "qualification-plan" else
                  prepare(args.python_archive, args.artifact_dir, args.runner, args.output))
        sys.stdout.buffer.write(canonical(result))
        return 0
    except Refusal as exc:
        sys.stdout.write(json.dumps({"status": "refused", "reason": str(exc)}) + "\n")
    except (OSError, ValueError, KeyError):
        sys.stdout.write('{"status":"refused","reason":"public_input_unavailable"}\n')
    return 2


if __name__ == "__main__":
    raise SystemExit(main())
