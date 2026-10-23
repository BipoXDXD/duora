"""Contrato de produção da issue #69; PyYAML já faz parte das ferramentas Ansible do job ops."""

import json
import os
import shutil
import subprocess
import unittest
from pathlib import Path
from tempfile import TemporaryDirectory
from unittest.mock import patch

import yaml

import duora_deploy as deploy

ROOT = Path(__file__).resolve().parents[2]


class ProductionMediaComposeTests(unittest.TestCase):
    def setUp(self) -> None:
        self.compose = yaml.safe_load((ROOT / "infra/compose/compose.prod.yml").read_text())

    def test_only_host_switch_supplies_review_and_no_alternative_providers(self) -> None:
        for name, service in self.compose["services"].items():
            with self.subTest(service=name):
                environment = service.get("environment", {})
                review = {k: v for k, v in environment.items() if "review" in k.lower()}
                expected = {"Media__Review": "${DUORA_MEDIA_REVIEW:-}"} if name in ("web", "worker") else {}
                self.assertEqual(expected, review)
                self.assertNotIn("env_file", service)
                if name in ("web", "worker"):
                    self.assertNotIn("command", service)
                    self.assertNotIn("entrypoint", service)
                    self.assertEqual("FileSystem", environment["Media__Storage__Provider"])
                    self.assertEqual("/var/lib/duora-media", environment["Media__Storage__FileSystemRoot"])
                    for secret in service.get("secrets", []):
                        self.assertNotIn("media", str(secret).lower())
        # Default and Production appsettings are both loaded in production. Development is deliberately exempt.
        for project in ("Duora.Web", "Duora.Worker"):
            for path in (ROOT / "backend/src" / project).glob("appsettings*.json"):
                if path.name not in ("appsettings.json", "appsettings.Production.json"):
                    continue
                def keys(value, prefix=""):
                    if isinstance(value, dict):
                        for key, child in value.items():
                            full = (prefix + ":" + key).strip(":").replace("__", ":").lower()
                            yield full
                            yield from keys(child, full)
                self.assertNotIn("media:review", list(keys(json.loads(path.read_text()))), str(path))

    def test_mounts_have_only_the_required_permissions_and_never_create_host_paths(self) -> None:
        for name, service in self.compose["services"].items():
            mounts = [v for v in service.get("volumes", []) if isinstance(v, dict)]
            expected = []
            if name in ("web", "worker"):
                for area in ("quarantine", "derivatives"):
                    expected.append({"type": "bind", "source": "${DUORA_MEDIA_DIR:?defina DUORA_MEDIA_DIR}/" + area,
                                     "target": "/var/lib/duora-media/" + area,
                                     "read_only": name == "web" and area == "derivatives",
                                     "bind": {"create_host_path": False}})
                self.assertEqual("1654:1654", service["user"])
                self.assertTrue(service["read_only"])
            self.assertEqual(expected, mounts, name)
            for mount in service.get("volumes", []):
                if isinstance(mount, str):
                    self.assertNotIn("media", mount.lower(), name)

    def test_ansible_uses_same_directory_and_keeps_upload_disabled(self) -> None:
        variables = yaml.safe_load((ROOT / "infra/ansible/group_vars/all.yml").read_text())
        self.assertEqual("", variables["duora_media_review"])
        self.assertEqual("/var/lib/duora-media", variables["duora_media_dir"])
        template = (ROOT / "infra/ansible/roles/app_dirs/templates/duora.env.j2").read_text()
        self.assertIn("DUORA_MEDIA_DIR={{ duora_media_dir }}", template)
        self.assertIn("DUORA_MEDIA_REVIEW={{ duora_media_review }}", template)
        tasks = yaml.safe_load((ROOT / "infra/ansible/roles/app_dirs/tasks/main.yml").read_text())
        directories = [item for task in tasks for item in task.get("loop", [])
                       if isinstance(item, dict) and item.get("path", "").startswith("{{ duora_media_dir }}/")]
        self.assertEqual(2, len(directories))
        for item in directories:
            self.assertEqual("1654", item["owner"])
            self.assertEqual("0700", item["mode"])

    def test_host_parser_rejects_alternative_keys_and_dotenv_ambiguity(self) -> None:
        for setting in ("export DUORA_MEDIA_REVIEW=TechnicalOnly", "Media__Review=TechnicalOnly", "Media:Review=TechnicalOnly",
                        "ASPNETCORE_Media__Review=TechnicalOnly", 'DUORA_MEDIA_REVIEW=""',
                        "DUORA_MEDIA_REVIEW=${OTHER}", "DUORA_MEDIA_REVIEW= # comment",
                        "DUORA_MEDIA_REVIEW=Editorial\nDUORA_MEDIA_REVIEW=", "DUORA_MEDIA_REVIEW='Editorial'"):
            with self.subTest(setting=setting), self.assertRaises(deploy.DeployError):
                deploy.parse_host_config("DUORA_IMAGE_PREFIX=x\n" + setting + "\n")

    def test_host_parser_names_the_format_problem_even_for_an_accepted_policy(self) -> None:
        # Com a allowlist vazia, todo valor não vazio já cai nela; aqui uma política aceita (sintética) isola a regra
        # do formato, que diz ao operador por que o valor com aspas, expansão ou comentário não vale.
        with patch.object(deploy, "PRODUCTION_MEDIA_REVIEW_POLICIES", frozenset({"Editorial"})):
            for value in ('"Editorial"', "'Editorial'", "${Editorial}", "Editorial # ligado", "Edit orial"):
                with self.subTest(value=value), self.assertRaisesRegex(deploy.DeployError, "sem aspas/expansão"):
                    deploy.parse_host_config(f"DUORA_IMAGE_PREFIX=x\nDUORA_MEDIA_REVIEW={value}\n")

    def test_compose_cannot_inherit_a_switch_or_override_file_from_the_shell(self) -> None:
        environment = {"PATH": "/usr/bin", "DUORA_MEDIA_REVIEW": "TechnicalOnly",
                       "DUORA_MEDIA_DIR": "/wrong", "COMPOSE_FILE": "override.yml",
                       "COMPOSE_ENV_FILES": "override.env"}
        with patch.dict(os.environ, environment, clear=True), patch.object(subprocess, "run") as run:
            deploy.run_command(["docker", "compose", "config"])
            self.assertEqual({"PATH": "/usr/bin:/bin", "LC_ALL": "C"}, run.call_args.kwargs["env"])


@unittest.skipUnless(shutil.which("docker"), "Docker Compose não instalado")
class RenderedMediaComposeTests(unittest.TestCase):
    def test_rendered_configuration_tracks_host_switch_even_with_a_poisoned_shell(self) -> None:
        with TemporaryDirectory() as temporary:
            env_file = Path(temporary) / "host.env"
            values = {f"DUORA_{name.upper()}_IMAGE": "example/" + name + "@sha256:" + "a" * 64
                      for name in deploy.IMAGE_NAMES}
            values.update(DUORA_SECRETS_DIR=temporary, DUORA_DOMAIN="test.invalid", DUORA_TLS="internal",
                          DUORA_HTTP_PORT="8080", DUORA_HTTPS_PORT="8443", DUORA_MEDIA_DIR=temporary)
            for review in (None, "", "Editorial"):
                with self.subTest(review=review):
                    current = dict(values)
                    if review is not None:
                        current["DUORA_MEDIA_REVIEW"] = review
                    env_file.write_text("".join(f"{k}={v}\n" for k, v in current.items()))
                    with patch.dict(os.environ, {"DUORA_MEDIA_REVIEW": "TechnicalOnly",
                                                 "COMPOSE_FILE": "/nonexistent/override.yml"}):
                        result = deploy.run_command(["docker", "compose", "--file",
                            str(ROOT / "infra/compose/compose.prod.yml"), "--env-file", str(env_file),
                            "config", "--format", "json"], capture=True)
                    self.assertEqual(0, result.returncode, result.stderr)
                    services = json.loads(result.stdout)["services"]
                    for name in ("web", "worker"):
                        self.assertEqual(review or "", services[name]["environment"]["Media__Review"])
                        mounts = {v["target"]: v for v in services[name]["volumes"]}
                        derivative = mounts["/var/lib/duora-media/derivatives"]
                        self.assertEqual(name == "web", derivative.get("read_only", False))
                        self.assertFalse(derivative["bind"].get("create_host_path", False))


if __name__ == "__main__":
    unittest.main()
