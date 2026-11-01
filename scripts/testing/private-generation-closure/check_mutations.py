"""Mutate disposable copies only; requires the image built by run_linux.sh."""
from pathlib import Path
import shutil
import subprocess
import tempfile

REPO = Path(__file__).resolve().parents[3]
PROBE = Path("scripts/testing/private-generation-closure")
CASES = [
    ("unchecked-admission", "Program.cs", "            protocol.RequireOpen();",
     "            // MUTANT: skipped admission", "test_late_write_is_rejected_after_restart", True),
    ("early-unlock", "Program.cs", "            await protocol.WriteAsync();",
     "            lease.Dispose();\n            await protocol.WriteAsync();",
     "test_paused_writer_blocks_close_and_sweep_for_each_variant", True),
    ("lost-marker", "Program.cs", "            protocol.Close();",
     "            // MUTANT: acknowledged without closing", "test_close_response_blocks_late_write_without_sweep", True),
    # Deliberately expose the limit of live process tests. This MUST be reported as unqualified durability.
    ("missing-sync-survives-live-test", "GenerationProtocol.cs", "        LinuxStorage.SyncDirectory(_control);",
     "        // MUTANT: missing directory sync", "test_late_write_is_rejected_after_restart", False),
]
for name, filename, old, new, test, must_fail in CASES:
    with tempfile.TemporaryDirectory(prefix="duora-closure-mutation-") as scratch:
        root = Path(scratch)
        for path in ("Directory.Build.props", "Directory.Packages.props", ".editorconfig", "global.json"):
            shutil.copy2(REPO / path, root / path)
        for relative in ("IObjectStore.cs", "ObjectKey.cs", "PhotoVariants.cs"):
            dest = root / "backend/src/Duora.Core/Media" / relative
            dest.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(REPO / "backend/src/Duora.Core/Media" / relative, dest)
        for relative in ("ExactLengthStream.cs", "FileSystemObjectStore.cs"):
            dest = root / "backend/src/Duora.Infrastructure/Media" / relative
            dest.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(REPO / "backend/src/Duora.Infrastructure/Media" / relative, dest)
        shutil.copytree(REPO / PROBE, root / PROBE, ignore=shutil.ignore_patterns("bin", "obj", "__pycache__"))
        target = root / PROBE / filename
        source = target.read_text()
        assert old in source, name
        target.write_text(source.replace(old, new))
        build = subprocess.run(["dotnet", "build", str(root / PROBE / "Probe.csproj"),
                                "-p:RestoreLockedMode=true"], capture_output=True, text=True)
        assert build.returncode == 0, build.stdout + build.stderr
        result = subprocess.run(["docker", "run", "--rm", "--network", "none", "--read-only",
                                 "--cap-drop", "ALL", "--security-opt", "no-new-privileges",
                                 "--tmpfs", "/tmp:rw,nosuid,nodev,mode=1777",
                                 "--mount", "type=volume,destination=/scratch",
                                 "--mount", f"type=bind,source={root / PROBE / 'bin/Debug/net10.0'},destination=/mutant,readonly",
                                 "-e", "DUORA_CLOSURE_PROBE=/mutant/Probe.dll",
                                 "duora-closure-probe:local", "ProtocolTests." + test],
                                capture_output=True, text=True, timeout=90)
        output = result.stdout + result.stderr
        if must_fail:
            assert result.returncode == 1 and "AssertionError" in output and "ERROR:" not in output, output
            print(f"DETECTED {name}: {test}", flush=True)
        else:
            assert result.returncode == 0, output
            print(f"SURVIVED {name}: process tests cannot prove host-crash durability", flush=True)
