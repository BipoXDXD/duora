#!/usr/bin/env python3
"""Finite GitHub-hosted synthetic profile qualification; never production admission."""
import argparse
import base64
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import platform
import selectors
import shutil
import signal
import subprocess
import sys
import tarfile
import tempfile
import time
import urllib.parse
import urllib.request
import uuid
import zipfile

ROOT = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("support_profile_packager", ROOT / "profile.py")
packager = importlib.util.module_from_spec(spec)
spec.loader.exec_module(packager)
Refusal = packager.Refusal
C934 = "ghcr.io/bipoxdxd/duora-web@sha256:2dfea2ca945197c09bdd4f6d924b2a9c44e82c317061df1d59dcd967dee2138d"
HELPER_ZIP = "29d949db278a7976b16b02da55c05203ce9b99d2e9988b8dbb6790163be3dd03"


PROFILE_CASES = ("bzip2-1.0.6", "X11", "Zlib", "SQLite-public-domain", "Python-2.0-history-summary")


def validate_profile_license_receipt(receipt, expected):
    required = {"schema_version", "status", "scope", "bindings", "global_policy_effect", "operational_effect", "case_decisions"}
    if (not isinstance(receipt, dict) or set(receipt) != required or receipt["schema_version"] != 2
            or receipt["status"] != "PROFILE_ONLY_ADMITTED"
            or receipt["scope"] != "synthetic_support_contact_profile"
            or receipt["global_policy_effect"] != "UNCHANGED" or receipt["operational_effect"] != "NONE"
            or receipt["bindings"] != expected):
        raise Refusal("LICENSE_NOT_ADMITTED")
    decisions = receipt["case_decisions"]
    binding_digest = hashlib.sha256(packager.canonical(expected)).hexdigest()
    if (not isinstance(decisions, list) or len(decisions) != len(PROFILE_CASES)
            or any(not isinstance(item, dict) for item in decisions)
            or {item.get("case_id") for item in decisions} != set(PROFILE_CASES)):
        raise Refusal("LICENSE_NOT_ADMITTED")
    for item in decisions:
        proof = item.get("decision_receipt_sha256")
        if (set(item) != {"case_id", "decision", "bindings_sha256", "decision_receipt_sha256"}
                or item["decision"] != "PROFILE_ONLY_APPROVED" or item["bindings_sha256"] != binding_digest
                or not isinstance(proof, str) or len(proof) != 64 or any(c not in "0123456789abcdef" for c in proof)):
            raise Refusal("LICENSE_NOT_ADMITTED")
    return True


def profile_license_bindings(lock):
    """Unsigned public input plan for independent review; grants no admission."""
    _, public = packager.public_metadata_inputs(lock)
    inventory_raw, _, notice_manifest = packager.notice_inputs()
    contract = lock["python"]["profile_license_contract"]
    return {"archive_sha256": lock["python"]["sha256"], "base_reference": lock["base"]["reference"],
                "selected_python_closure_sha256": contract["selected_python_closure_sha256"],
                "selection_sha256": contract["selection_sha256"], "core_binaries": contract["core_binaries"],
                "runner_sha256": packager.RUNNER_SHA256, "helper_files": packager.HELPER_FILES,
                "inventory_sha256": hashlib.sha256(inventory_raw).hexdigest(),
                "notice_manifest_sha256": hashlib.sha256(packager.canonical(notice_manifest)).hexdigest(),
                "review_manifest_sha256": contract["review_manifest_sha256"],
                "upstream_python_sha256": contract["upstream_python_sha256"],
                "provenance_v2_receipt_sha256": contract["provenance_v2_receipt_sha256"],
                "independent_review_sha256": contract["independent_review_sha256"],
                "policy_sha256": contract["policy_sha256"], "public_metadata": public,
                "source_hashes": {name: hashlib.sha256((ROOT / name).read_bytes()).hexdigest()
                                  for name in ("profile.py", "Dockerfile", "reviewed-python-aliases.json", "qualify.py")}}


def require_license_admission(lock):
    admission = lock.get("python", {}).get("license_admission", {})
    expected = admission.get("receipt_sha256")
    if admission.get("status") != "PROFILE_ONLY_ADMITTED" or not isinstance(expected, str) or len(expected) != 64:
        raise Refusal("LICENSE_NOT_ADMITTED")
    try:
        receipt = json.loads(packager.verified_file(ROOT / "license-admission.json", expected, 262144, "LICENSE_NOT_ADMITTED"))
        bindings = profile_license_bindings(lock)
        validate_profile_license_receipt(receipt, bindings)
        return receipt
    except (OSError, ValueError, KeyError, TypeError, Refusal):
        raise Refusal("LICENSE_NOT_ADMITTED") from None


def verify_constructed_admission(receipt, manifest):
    bound = receipt["bindings"]
    fields = {"python_closure_sha256": "selected_python_closure_sha256", "python_selection_sha256": "selection_sha256",
              "python_archive_sha256": "archive_sha256", "runner_sha256": "runner_sha256",
              "notice_manifest_sha256": "notice_manifest_sha256", "core_binaries": "core_binaries",
              "public_metadata": "public_metadata", "helper": "helper_files"}
    if (any(manifest.get(actual) != bound[expected] for actual, expected in fields.items())
            or manifest.get("base", {}).get("reference") != bound["base_reference"]
            or manifest.get("packager_sha256") != bound["source_hashes"]["profile.py"]
            or manifest.get("dockerfile_sha256") != bound["source_hashes"]["Dockerfile"]):
        raise Refusal("LICENSE_NOT_ADMITTED")


def verify_image_public_files(raw, expected):
    try:
        if json.loads(raw) != expected:
            raise ValueError()
    except (ValueError, TypeError):
        raise Refusal("image_public_metadata_mismatch") from None
    return True


def bounded_download(stream, size, digest):
    if type(size) is not int or not 0 < size <= packager.MAX_ARCHIVE:
        raise Refusal("download_size_invalid")
    raw = stream.read(size + 1)
    if len(raw) != size or hashlib.sha256(raw).hexdigest() != digest:
        raise Refusal("download_untrusted")
    return raw


def helper_bytes(record):
    try:
        raw = base64.b64decode(record["archive"], validate=True)
        if len(raw) != 9505 or hashlib.sha256(raw).hexdigest() != HELPER_ZIP:
            raise ValueError()
        with zipfile.ZipFile(io.BytesIO(raw)) as archive:
            entries = archive.infolist()
            if len(entries) != 3 or {entry.filename for entry in entries} != set(packager.HELPER_FILES):
                raise ValueError()
            files = {}
            for entry in entries:
                if entry.file_size > 1024 * 1024 or entry.is_dir() or (entry.external_attr >> 16) & 0o170000 == 0o120000:
                    raise ValueError()
                value = archive.read(entry)
                if hashlib.sha256(value).hexdigest() != packager.HELPER_FILES[entry.filename]:
                    raise ValueError()
                files[entry.filename] = value
            return files
    except (ValueError, KeyError, zipfile.BadZipFile):
        raise Refusal("helper_untrusted") from None


def verify_isolation(value):
    host = value["HostConfig"]
    expected = {"NetworkMode": "none", "ReadonlyRootfs": True, "CapDrop": ["ALL"],
                "Memory": 536870912, "MemorySwap": 536870912, "NanoCpus": 500000000,
                "PidsLimit": 64, "Privileged": False, "PidMode": "", "IpcMode": "private"}
    if (value["Config"]["User"] != "1654:1654" or any(host.get(k) != v for k, v in expected.items())
            or host.get("CapAdd") not in (None, [])
            or host.get("SecurityOpt") != ["no-new-privileges:true"]
            or host.get("LogConfig", {}).get("Type") != "none"
            or host.get("Tmpfs") != {"/tmp": "rw,noexec,nosuid,nodev,size=16777216,mode=1777"}
            or host.get("Ulimits") != [{"Name": "core", "Hard": 0, "Soft": 0}]
            or any(not ((mount.get("Type") == "tmpfs" and mount.get("Destination") == "/tmp")
                            or (mount.get("Type") == "bind" and mount.get("RW") is False
                                and mount.get("Destination") in {"/app", "/fixture"}))
                   for mount in value.get("Mounts", []))):
        raise Refusal("isolation_mismatch")
    return True


def mechanical_reply(raw):
    try:
        value = json.loads(raw)
        if (value["status"] != "replay_verified"
                or any(value.get(k) is not True for k in ("configuration_matches_approved", "ioptions_matches_approved"))
                or any(value.get(k) is not False for k in ("cached_runtime_options_observed", "runtime_effective_proven", "application_readiness_observed"))):
            raise ValueError()
        return True
    except (ValueError, KeyError, TypeError):
        raise Refusal("mechanical_pair_failed") from None


def command(args, work, payload=b"", timeout=120, output_limit=262144, strict_stderr=False):
    process = subprocess.Popen(args, cwd=work, env={"PATH": "/usr/bin:/bin", "HOME": str(work)},
                               stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                               close_fds=True, start_new_session=True)
    selector, stdout, sent, total, diagnostics = selectors.DefaultSelector(), bytearray(), 0, 0, False
    deadline = time.monotonic() + timeout
    try:
        for pipe, event in ((process.stdin, selectors.EVENT_WRITE), (process.stdout, selectors.EVENT_READ), (process.stderr, selectors.EVENT_READ)):
            os.set_blocking(pipe.fileno(), False)
            selector.register(pipe, event)
        while selector.get_map():
            if time.monotonic() >= deadline:
                raise Refusal("command_timeout")
            for key, _ in selector.select(0.1):
                pipe = key.fileobj
                if pipe is process.stdin:
                    if sent < len(payload):
                        sent += os.write(pipe.fileno(), payload[sent:sent + 4096])
                    if sent == len(payload):
                        selector.unregister(pipe)
                        pipe.close()
                else:
                    raw = os.read(pipe.fileno(), 4096)
                    if not raw:
                        selector.unregister(pipe)
                        pipe.close()
                        continue
                    total += len(raw)
                    if total > output_limit:
                        raise Refusal("command_output_limit")
                    if pipe is process.stdout:
                        stdout.extend(raw)
                    else:
                        diagnostics = True
        remaining = deadline - time.monotonic()
        if remaining <= 0 or process.wait(timeout=remaining) != 0 or (strict_stderr and diagnostics):
            raise Refusal("command_failed")
        return bytes(stdout)
    except (OSError, subprocess.TimeoutExpired):
        raise Refusal("command_failed") from None
    finally:
        selector.close()
        try:
            os.killpg(process.pid, signal.SIGKILL)
        except ProcessLookupError:
            pass
        process.wait()
        for pipe in (process.stdin, process.stdout, process.stderr):
            pipe.close()


def oci_archive(source_path, target_path, expected_config):
    with tarfile.open(source_path) as source:
        descriptor = json.load(source.extractfile("manifest.json"))
        if len(descriptor) != 1:
            raise Refusal("oci_manifest_invalid")
        record = descriptor[0]
        config = source.extractfile(record["Config"]).read(1024 * 1024 + 1)
        if len(config) > 1024 * 1024 or "sha256:" + hashlib.sha256(config).hexdigest() != expected_config:
            raise Refusal("oci_config_mismatch")
        config_value = json.loads(config)
        layers, blobs = [], []
        for name in record["Layers"]:
            member = source.getmember(name)
            if not member.isfile() or member.size > 1024 * 1024 * 1024:
                raise Refusal("oci_layer_invalid")
            digest = hashlib.sha256()
            with source.extractfile(member) as stream:
                for chunk in iter(lambda: stream.read(1024 * 1024), b""):
                    digest.update(chunk)
            value = "sha256:" + digest.hexdigest()
            layers.append({"mediaType": "application/vnd.oci.image.layer.v1.tar", "digest": value, "size": member.size})
            blobs.append((value[7:], member))
        if ([item["digest"] for item in layers] != config_value["rootfs"]["diff_ids"]
                or config_value.get("os") != "linux" or config_value.get("architecture") != "amd64"):
            raise Refusal("oci_layer_binding_mismatch")
        manifest = packager.canonical({"schemaVersion": 2, "mediaType": "application/vnd.oci.image.manifest.v1+json", "config": {"mediaType": "application/vnd.oci.image.config.v1+json", "digest": expected_config, "size": len(config)}, "layers": layers})
        leaf = "sha256:" + hashlib.sha256(manifest).hexdigest()
        index = packager.canonical({"schemaVersion": 2, "manifests": [{"mediaType": "application/vnd.oci.image.manifest.v1+json", "digest": leaf, "size": len(manifest), "platform": {"os": "linux", "architecture": "amd64"}}]})
        with tarfile.open(target_path, "w", format=tarfile.USTAR_FORMAT) as target:
            public = {"oci-layout": b'{"imageLayoutVersion":"1.0.0"}\n', "index.json": index,
                      "blobs/sha256/" + expected_config[7:]: config, "blobs/sha256/" + leaf[7:]: manifest}
            for name, raw in sorted(public.items()):
                entry = tarfile.TarInfo(name)
                entry.size, entry.mode = len(raw), 0o644
                target.addfile(entry, io.BytesIO(raw))
            for digest, member in blobs:
                entry = tarfile.TarInfo("blobs/sha256/" + digest)
                entry.size, entry.mode = member.size, 0o644
                with source.extractfile(member) as stream:
                    target.addfile(entry, stream)
    return leaf


class PublicRedirects(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, response, code, message, headers, newurl):
        parsed = urllib.parse.urlparse(newurl)
        if parsed.scheme != "https" or parsed.hostname not in {"github.com", "release-assets.githubusercontent.com", "objects.githubusercontent.com"}:
            raise Refusal("download_redirect_refused")
        return super().redirect_request(request, response, code, message, headers, newurl)


def qualify(output):
    if (os.environ.get("GITHUB_ACTIONS") != "true" or os.environ.get("RUNNER_ENVIRONMENT") != "github-hosted"
            or sys.platform != "linux" or platform.machine() != "x86_64"):
        raise Refusal("hosted_linux_amd64_required")
    output.mkdir(parents=True, exist_ok=False)
    receipt = {"state": "NOT_QUALIFIED", "production_accessed": False, "operational_capture_authorized": False,
               "runtime_effective_proven": False, "cached_runtime_options_observed": False,
               "protected_capture_proven": False, "process_start_stability_proven": False, "permissions_proven": False,
               "native_license_review_proven": False, "supply_chain_admitted": False}
    owned = []
    with tempfile.TemporaryDirectory(prefix="support-runtime-") as temporary:
        work = Path(temporary)
        def docker(*args, **kwargs):
            return command(["docker", *args], work, **kwargs)
        try:
            lock = json.loads((ROOT / "profile-lock.json").read_bytes())
            admission = require_license_admission(lock)
            with urllib.request.build_opener(PublicRedirects()).open(lock["python"]["url"], timeout=60) as response:
                raw = bounded_download(response, lock["python"]["size_bytes"], lock["python"]["sha256"])
            archive = work / "python.tar.gz"
            archive.write_bytes(raw)
            helper = work / "helper"
            helper.mkdir()
            for name, value in helper_bytes(json.loads((ROOT / "preserved-helper.json").read_bytes())).items():
                (helper / name).write_bytes(value)
            context = work / "context.tar"
            manifest = packager.prepare(archive, helper, ROOT.parent / "support-contact-probe/run_preserved_probe.py", context)
            verify_constructed_admission(admission, manifest)
            image = "support-contact-qualified:" + uuid.uuid4().hex
            with context.open("rb") as source:
                docker("build", "--network=none", "--quiet", "--tag", image, "-", payload=source.read(), timeout=180)
            image_info = json.loads(docker("image", "inspect", image))[0]
            image_id = image_info["Id"]
            if image_info.get("Os") != "linux" or image_info.get("Architecture") != "amd64":
                raise Refusal("profile_architecture_mismatch")
            environment = image_info["Config"].get("Env", [])
            allowed = {"PATH", "APP_UID", "ASPNETCORE_HTTP_PORTS", "DOTNET_RUNNING_IN_CONTAINER", "DOTNET_VERSION", "ASPNET_VERSION", "DOTNET_NOLOGO", "DOTNET_CLI_TELEMETRY_OPTOUT", "DOTNET_EnableDiagnostics", "COMPlus_DbgEnableMiniDump"}
            if any(item.partition("=")[0] not in allowed for item in environment):
                raise Refusal("image_environment_unreviewed")
            controls = ["--user=1654:1654", "--network=none", "--read-only", "--cap-drop=ALL", "--security-opt=no-new-privileges:true", "--log-driver=none", "--memory=536870912", "--memory-swap=536870912", "--cpus=0.5", "--pids-limit=64", "--tmpfs=/tmp:rw,noexec,nosuid,nodev,size=16777216,mode=1777", "--ulimit=core=0:0", "--ipc=private"]
            def sandbox(extra, payload=b"", entrypoint=None):
                name = "support-probe-" + uuid.uuid4().hex
                owned.append(name)
                options = ["--entrypoint=" + entrypoint] if entrypoint else []
                docker("create", "--name", name, "-i", *controls, *options, *extra)
                verify_isolation(json.loads(docker("inspect", name))[0])
                return docker("start", "-a", "-i", name, payload=payload, timeout=15, output_limit=16384, strict_stderr=True)
            smoke = """import errno,json,os,sys
assert sys.version_info[:2] == (3,13) and os.getuid() == os.getgid() == 1654
try:
 open('/opt/probe/write-denied','w')
 raise AssertionError()
except OSError as error:
 assert error.errno in (errno.EROFS,errno.EACCES)
assert not os.path.exists('/var/run/docker.sock')
assert not any(line.split()[1] != '00000000' for line in open('/proc/net/route').read().splitlines()[1:])
print(json.dumps({'python_stdlib':True,'readonly_denied':True,'network_none':True}))"""
            smoke_result = json.loads(sandbox([image_id, "-I", "-B", "-c", smoke], entrypoint="/opt/python/bin/python3.13"))
            if smoke_result != {"python_stdlib": True, "readonly_denied": True, "network_none": True}:
                raise Refusal("runtime_smoke_failed")
            runtimes = sandbox([image_id, "--list-runtimes"], entrypoint="/usr/share/dotnet/dotnet")
            if b"Microsoft.AspNetCore.App 10.0.13 " not in runtimes or b"Microsoft.NETCore.App 10.0.13 " not in runtimes:
                raise Refusal("dotnet_runtime_mismatch")
            docker("pull", "--platform=linux/amd64", C934, timeout=180)
            source_name = "support-source-" + uuid.uuid4().hex
            owned.append(source_name)
            docker("create", "--name", source_name, "--network=none", C934)
            app = work / "app"
            docker("cp", source_name + ":/app", str(app))
            fixture = work / "fixture"
            fixture.mkdir(mode=0o755)
            (fixture / "appsettings.json").write_text(json.dumps({"Duora": {"SupportContact": "support@meetduora.com"}}))
            for role in ("web", "worker"):
                capture = {"Role": role, "ApplicationAssembly": "/app/Duora.Web.dll", "ContentRoot": "/fixture", "Environment": {"DOTNET_ENVIRONMENT": "Production", "ASPNETCORE_ENVIRONMENT": "Production"}, "Arguments": []}
                mounts = ["--mount=type=bind,src=" + str(app) + ",dst=/app,readonly", "--mount=type=bind,src=" + str(fixture) + ",dst=/fixture,readonly"]
                mechanical_reply(sandbox([*mounts, image_id], packager.canonical(capture)))
            image_files = manifest["packaged_public_files"]
            image_verifier = """import hashlib,json,sys
from pathlib import Path
expected=json.load(sys.stdin)
observed={}
for name in sorted(expected):
 raw=Path(name).read_bytes()
 observed[name]={'sha256':hashlib.sha256(raw).hexdigest(),'bytes':len(raw)}
print(json.dumps(observed,sort_keys=True,separators=(',',':')))"""
            observed_files = sandbox([image_id, "-I", "-B", "-c", image_verifier],
                                     packager.canonical(image_files), entrypoint="/opt/python/bin/python3.13")
            verify_image_public_files(observed_files, image_files)
            saved = output / "profile-image.tar"
            docker("image", "save", "--output", str(saved), image_id, timeout=120)
            if saved.stat().st_size > 1024 * 1024 * 1024:
                raise Refusal("profile_archive_limit")
            oci = output / "profile-oci.tar"
            leaf = oci_archive(saved, oci, image_id)
            saved.unlink()
            digest = hashlib.sha256()
            with oci.open("rb") as source:
                for chunk in iter(lambda: source.read(1024 * 1024), b""):
                    digest.update(chunk)
            receipt.update(state="SYNTHETIC_PROFILE_QUALIFIED_ONLY", construction=manifest,
                           profile_config_digest=image_id, profile_oci_leaf_digest=leaf,
                           profile_archive_sha256=digest.hexdigest(), source_head=os.environ.get("GITHUB_SHA"),
                           public_image_environment=environment, synthetic_mechanical_roles=["web", "worker"],
                           app_graph=C934, actual_worker_graph_proven=False, runtime_controls_verified=True,
                           profile_only_license_receipt_sha256=lock["python"]["license_admission"]["receipt_sha256"],
                           image_public_file_hashes=json.loads(observed_files),
                           qualification_source_sha256=hashlib.sha256(Path(__file__).read_bytes()).hexdigest())
        except Refusal as error:
            receipt["failure_reason"] = str(error)
            raise
        except Exception:
            receipt["failure_reason"] = "qualification_failed"
            raise
        finally:
            cleaned = True
            for name in reversed(owned):
                try:
                    docker("rm", "--force", name, timeout=15)
                except Exception:
                    cleaned = False
            if not cleaned:
                receipt["state"] = "NOT_QUALIFIED"
            receipt["owned_containers_cleaned"] = cleaned
            (output / "qualification.json").write_bytes(packager.canonical(receipt))
            if not cleaned:
                raise Refusal("cleanup_unconfirmed")
    return receipt


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    try:
        qualify(args.output)
        print("Synthetic profile qualification completed; operational admission remains blocked.")
        return 0
    except Exception:
        print("Synthetic profile qualification failed; operational admission remains blocked.")
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
