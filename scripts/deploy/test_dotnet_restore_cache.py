"""O publish real deve sobreviver à perda do cache mutável entre etapas."""

from __future__ import annotations

import os
import re
import shutil
import subprocess
import tempfile
import unittest
import uuid
from pathlib import Path

ROOT = next(p for p in Path(__file__).resolve().parents if (p / ".git").exists())
DOCKERFILE = ROOT / "infra/images/dotnet/Dockerfile"
DOCKER = shutil.which("docker") or "docker"


class DotnetRestoreCacheTests(unittest.TestCase):
    def setUp(self) -> None:
        directory = tempfile.TemporaryDirectory(prefix="nuget-cache-contract-")
        self.addCleanup(directory.cleanup)
        self.context = Path(directory.name)
        source = DOCKERFILE.read_text()
        sdk = re.search(r"^FROM --platform=\$BUILDPLATFORM (\S+) AS build$", source, re.M)
        self.assertIsNotNone(sdk)
        assert sdk is not None
        self.sdk = sdk.group(1)
        self.cache_id = "nuget-contract-" + uuid.uuid4().hex

        for filename in ("global.json", ".editorconfig", ".dockerignore"):
            shutil.copyfile(ROOT / filename, self.context / filename)
        (self.context / ".config").mkdir()
        shutil.copyfile(ROOT / ".config/dotnet-tools.json", self.context / ".config/dotnet-tools.json")
        # Pacote sintético local: restore/publish reais, sem nuget.org, licença ou dados reais.
        (self.context / "nuget.config").write_text(
            '<configuration><packageSources><clear />'
            '<add key="fixture" value="/src/backend/src/fixture-feed" />'
            '</packageSources></configuration>\n'
        )
        (self.context / "Directory.Build.props").write_text(
            '<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework>'
            '<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>'
            '<NuGetAudit>false</NuGetAudit></PropertyGroup></Project>\n'
        )
        (self.context / "Directory.Packages.props").write_text("<Project />\n")
        feed = self.context / "backend/src/fixture-feed"
        feed.mkdir(parents=True)
        library = self.context / "backend/src/FixturePackage"
        library.mkdir()
        (library / "FixturePackage.csproj").write_text(
            '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>'
            '<PackageId>Duora.CacheFixture</PackageId><Version>1.0.0</Version>'
            '</PropertyGroup></Project>\n'
        )
        (library / "Marker.cs").write_text("namespace CacheFixture; public class Marker { }\n")
        for project in ("Duora.Web", "Duora.Worker"):
            path = self.context / "backend/src" / project
            path.mkdir()
            (path / f"{project}.csproj").write_text(
                '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType>'
                '</PropertyGroup><ItemGroup><PackageReference Include="Duora.CacheFixture" '
                'Version="1.0.0" /></ItemGroup></Project>\n'
            )
            (path / "Program.cs").write_text(
                'System.Console.WriteLine(typeof(CacheFixture.Marker).Name);\n'
            )
        restored = self.call(
            "run", "--rm", "--network=none", "--user", f"{os.getuid()}:{os.getgid()}",
            "-e", "DOTNET_CLI_HOME=/tmp/dotnet-cli", "-e", "NUGET_PACKAGES=/tmp/nuget",
            "-e", "XDG_RUNTIME_DIR=/tmp/nuget-runtime",
            "-v", f"{self.context}:/src", "-w", "/src",
            self.sdk, "sh", "-ec",
            "mkdir -m 700 /tmp/nuget-runtime && "
            "dotnet pack backend/src/FixturePackage -c Release -o backend/src/fixture-feed && "
            "dotnet restore backend/src/Duora.Web --use-lock-file && "
            "dotnet restore backend/src/Duora.Worker --use-lock-file",
        )
        self.assertEqual(restored.returncode, 0, restored.stdout + restored.stderr)

        # Injeta a perda de um cache exclusivo deste teste depois do restore de produção.
        # Não apaga caches do usuário nem usa prune global. A camada de restore fica intacta.
        for mount in re.findall(r"--mount=type=cache,[^\s]+", source):
            options = dict(option.split("=", 1) for option in mount.removeprefix("--mount=").split(","))
            if options.get("target") == "/root/.nuget/packages":
                options["id"] = self.cache_id
                isolated = "--mount=" + ",".join(f"{key}={value}" for key, value in options.items())
                source = source.replace(mount, isolated)
        source = source.replace(
            "FROM build AS publish-web",
            f"RUN --mount=type=cache,id={self.cache_id},target=/root/.nuget/packages "
            "find /root/.nuget/packages -mindepth 1 -delete\n\nFROM build AS publish-web",
            1,
        )
        source += (
            "\nFROM scratch AS fixture-web\nCOPY --from=publish-web /out/ /\n"
            "\nFROM scratch AS fixture-worker\nCOPY --from=publish-worker /out/ /\n"
        )
        (self.context / "Dockerfile").write_text(source)
        (self.context / "fixture-license").write_text("synthetic test value\n")

    def call(self, *arguments: str) -> subprocess.CompletedProcess[str]:
        return subprocess.run(  # noqa: S603 - somente fixtures e argumentos controlados
            [DOCKER, *arguments], capture_output=True, text=True, check=False,
            timeout=180, env={**os.environ, "DOCKER_DEFAULT_PLATFORM": ""},
        )

    def build(self, target: str) -> subprocess.CompletedProcess[str]:
        return self.call(
            "buildx", "build", "--network=none", "--progress=plain", "--target", target,
            "--secret", f"id=sixlabors_license,src={self.context / 'fixture-license'}",
            "--output", f"type=local,dest={self.context / target}", str(self.context),
        )

    def test_publish_survives_loss_of_the_mutable_package_cache(self) -> None:
        for service in ("web", "worker"):
            with self.subTest(service=service):
                target = f"fixture-{service}"
                result = self.build(target)
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                assembly = "Duora.Web.dll" if service == "web" else "Duora.Worker.dll"
                published = self.context / target
                self.assertTrue((published / assembly).is_file())
                executed = self.call(
                    "run", "--rm", "--network=none", "-v", f"{published}:/app:ro", "-w", "/app",
                    self.sdk, "dotnet", assembly,
                )
                self.assertEqual(executed.returncode, 0, executed.stdout + executed.stderr)
                self.assertEqual(executed.stdout.strip(), "Marker")

    def test_a_changed_dependency_still_fails_the_locked_restore(self) -> None:
        project = self.context / "backend/src/Duora.Web/Duora.Web.csproj"
        project.write_text(project.read_text().replace('Version="1.0.0"', 'Version="2.0.0"'))
        result = self.build("fixture-web")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("NU1004", result.stdout + result.stderr)


if __name__ == "__main__":
    unittest.main()
