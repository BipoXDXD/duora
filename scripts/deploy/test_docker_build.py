"""Build nativo local e release com ARM64 e AMD64."""

from __future__ import annotations

import os
import shutil
import subprocess
import tarfile
import tempfile
import unittest
import uuid
from pathlib import Path
from typing import Any

ROOT = next(p for p in Path(__file__).resolve().parents if (p / ".git").exists())
SCRIPT = ROOT / "scripts" / "docker-build.sh"
DOCKER = shutil.which("docker") or "docker"
SHELL = shutil.which("sh") or "/bin/sh"


class DockerBuildContractTests(unittest.TestCase):
    def setUp(self) -> None:
        self.assertTrue(SCRIPT.is_file(), "falta o comando de build com proteção de arquitetura")
        self.directory = tempfile.TemporaryDirectory(prefix="docker-platform-contract-")
        self.addCleanup(self.directory.cleanup)
        self.context = Path(self.directory.name)
        (self.context / "Dockerfile").write_text('FROM scratch\nLABEL io.codex.test="docker-platform-contract"\n'
            'COPY marker /marker\n')
        (self.context / "marker").write_text("fixture sem aplicação nem dados reais\n")
        self.tag = f"docker-platform-contract:{uuid.uuid4().hex}"
        self.created_indices: set[str] = set()
        self.addCleanup(self.remove_image)
        self.native = self.docker("version", "--format", "{{.Server.Os}}/{{.Server.Arch}}")
        self.foreign = "linux/amd64" if self.native == "linux/arm64" else "linux/arm64"

    def call(
        self, arguments: list[str], environment: dict[str, str] | None = None
    ) -> subprocess.CompletedProcess[str]:
        return subprocess.run(  # noqa: S603 - argumentos controlados pelo teste
            arguments,
            capture_output=True,
            text=True,
            check=False,
            timeout=120,
            env={**os.environ, **(environment or {})},
        )

    def docker(self, *arguments: str) -> str:
        result = self.call([DOCKER, *arguments])
        self.assertEqual(result.returncode, 0, result.stderr)
        if arguments[:2] == ("buildx", "build"):
            index = self.call([DOCKER, "image", "inspect", "--format", "{{.Id}}", self.tag])
            if index.returncode == 0:
                self.created_indices.add(index.stdout.strip())
        return result.stdout.strip()

    def command(self, *arguments: str, **environment: str) -> subprocess.CompletedProcess[str]:
        return self.call([SHELL, str(SCRIPT), *arguments], environment)

    def remove_image(self) -> None:
        index = self.call([DOCKER, "image", "inspect", "--format", "{{.Id}}", self.tag])
        if index.returncode == 0:
            self.created_indices.add(index.stdout.strip())
        self.call([DOCKER, "image", "rm", self.tag])
        for image_id in self.created_indices:
            self.call([DOCKER, "image", "rm", image_id])

    def test_local_build_overrides_foreign_default_and_replaces_foreign_image(self) -> None:
        self.docker(
            "buildx", "build", "--platform", self.foreign, "--load",
            "--provenance=false", "-t", self.tag, str(self.context),
        )
        result = self.command(
            "local", "-t", self.tag, str(self.context),
            DOCKER_DEFAULT_PLATFORM=self.foreign,
        )
        self.assertEqual(result.returncode, 0, result.stderr)
        actual = self.docker("image", "inspect", "--format", "{{.Os}}/{{.Architecture}}", self.tag)
        self.assertEqual(actual, self.native)

    def test_local_verification_rejects_an_image_with_only_the_foreign_platform(self) -> None:
        self.docker(
            "buildx", "build", "--platform", self.foreign, "--load",
            "--provenance=false", "-t", self.tag, str(self.context),
        )
        self.assertNotEqual(self.command("check", self.tag).returncode, 0)

    def test_compose_build_overrides_a_foreign_default(self) -> None:
        compose = self.context / "compose.yaml"
        compose.write_text(
            "services:\n  fixture:\n    image: " + self.tag + "\n    build: .\n"
        )
        result = self.command(
            "compose", "--project-name", "platform-contract", "--file", str(compose),
            "build", DOCKER_DEFAULT_PLATFORM=self.foreign,
        )
        self.assertEqual(result.returncode, 0, result.stderr)
        actual = self.docker("image", "inspect", "--format", "{{.Os}}/{{.Architecture}}", self.tag)
        self.assertEqual(actual, self.native)

    def test_caller_cannot_override_platform_or_exporter(self) -> None:
        for option in (
            "--platform=linux/amd64", "--platform", "--push", "--push=false",
            "--load", "--load=false", "--output=type=docker", "-otype=docker",
        ):
            with self.subTest(option=option):
                result = self.command("local", "-t", self.tag, option, str(self.context))
                self.assertNotEqual(result.returncode, 0)
        absent = self.call([DOCKER, "image", "inspect", self.tag])
        self.assertNotEqual(absent.returncode, 0, "a recusa não pode criar imagem")

    def test_stacked_short_options_cannot_add_an_exporter(self) -> None:
        for option in ("-qo", "-Dqo"):
            with self.subTest(option=option):
                result = self.command(
                    "oci", str(self.context / "release.tar"), "-t", self.tag,
                    option, "type=docker", str(self.context),
                )
                self.assertEqual(result.returncode, 2, result.stderr)
        self.assertNotEqual(self.call([DOCKER, "image", "inspect", self.tag]).returncode, 0)

    def test_archive_contains_both_platforms_without_loading_a_local_tag(self) -> None:
        output = self.context / "release.oci.tar"
        # O driver docker clássico dos runners não exporta OCI multiplataforma.
        # Builder exclusivo, sem --use: preserva a seleção e o daemon do usuário.
        builder = "platform-contract-" + uuid.uuid4().hex[:12]
        self.docker(
            "buildx", "create", "--name", builder, "--driver", "docker-container",
            "--driver-opt",
            "image=moby/buildkit@sha256:cec9f139f45e93c5c69c60f8b07cfad9f43f4ef6b6a6cd917527fea5ff2e3dea",
        )
        self.addCleanup(self.call, [DOCKER, "buildx", "rm", builder])
        result = self.command(
            "oci", str(output), "--builder", builder, "-t", self.tag, str(self.context)
        )
        self.assertEqual(result.returncode, 0, result.stderr)
        architectures: set[str] = set()
        with tarfile.open(output) as archive:
            import json

            def read(name: str) -> Any:
                source = archive.extractfile(name)
                self.assertIsNotNone(source)
                assert source is not None
                return json.load(source)

            def visit(descriptor: dict[str, Any]) -> None:
                document = read("blobs/" + descriptor["digest"].replace(":", "/", 1))
                if "manifests" in document:
                    for child in document["manifests"]:
                        visit(child)
                elif "config" in document:
                    config = read("blobs/" + document["config"]["digest"].replace(":", "/", 1))
                    if config.get("os") == "linux":
                        architectures.add(config["architecture"])

            for descriptor in read("index.json")["manifests"]:
                visit(descriptor)
        self.assertEqual(architectures, {"amd64", "arm64"})
        self.assertNotEqual(self.call([DOCKER, "image", "inspect", self.tag]).returncode, 0)

    def test_publication_check_requires_one_manifest_for_each_platform(self) -> None:
        # Só a fronteira remota é um dublê; a validação e seu código de saída são reais.
        fake_bin = self.context / "bin"
        fake_bin.mkdir()
        fake = fake_bin / "docker"
        fake.write_text('#!/bin/sh\nprintf "%s\\n" "$PLATFORM_FIXTURE"\n')
        fake.chmod(0o755)
        for platforms, expected in (
            ("linux/amd64", False),
            ("linux/arm64", False),
            ("linux/amd64\nlinux/arm64", True),
            ("linux/amd64\nunknown/unknown\nlinux/arm64", True),
            ("linux/amd64\nlinux/arm64\nlinux/arm64", False),
        ):
            with self.subTest(platforms=platforms):
                result = self.command(
                    "published", self.tag,
                    PATH=f"{fake_bin}:{os.environ['PATH']}", PLATFORM_FIXTURE=platforms,
                )
                self.assertEqual(result.returncode == 0, expected, result.stderr)


if __name__ == "__main__":
    unittest.main()
