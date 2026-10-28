"""Testes do patch da selfie no Compose do Coolify. Rodar da raiz: python3 -m unittest discover -s scripts/deploy -v"""

import copy
import json
import subprocess
import sys
import unittest
from pathlib import Path
from tempfile import TemporaryDirectory

import selfie_compose

SCRIPT = Path(__file__).with_name("selfie_compose.py")


def coolify_compose(secrets_file: str = "/etc/duora/secrets/duora_app_connection") -> dict:
    """Forma do `docker compose config --format json`: environment em dict, secrets em lista de objetos."""
    return {
        "name": "duora",
        "services": {
            "web": {
                "image": "${DUORA_WEB_IMAGE}",
                "environment": {"ASPNETCORE_ENVIRONMENT": "Production", "SecretsDirectory": "/run/secrets"},
                "secrets": [{"source": "duora_app_connection", "target": "ConnectionStrings__Duora"}],
            },
            "worker": {
                "image": "${DUORA_WORKER_IMAGE}",
                "environment": {"DOTNET_ENVIRONMENT": "Production", "SecretsDirectory": "/run/secrets"},
                "secrets": [{"source": "duora_app_connection", "target": "ConnectionStrings__Duora"}],
            },
            "postgres": {"image": "${DUORA_POSTGRES_IMAGE}"},
        },
        "secrets": {"duora_app_connection": {"file": secrets_file, "name": "duora_duora_app_connection"}},
    }


class EnableTests(unittest.TestCase):
    def test_each_service_gets_only_its_own_key_pair_and_the_switch(self) -> None:
        patched = selfie_compose.enable(coolify_compose())

        web, worker = patched["services"]["web"], patched["services"]["worker"]
        self.assertEqual("true", web["environment"]["SelfieVerification__Enabled"])
        self.assertEqual("true", worker["environment"]["SelfieVerification__Enabled"])
        self.assertEqual("true", web["environment"]["SelfieVerification__PrivacyApproved"])
        self.assertEqual("us-east-1", worker["environment"]["SelfieVerification__Region"])
        self.assertEqual("arn:aws:iam::394877252106:role/duora-selfie-capture",
                         web["environment"]["SelfieVerification__CaptureRoleArn"])
        self.assertNotIn("SelfieVerification__CaptureRoleArn", worker["environment"])
        self.assertIn({"source": "selfie_web_aws_access_key_id", "target": "SelfieVerification__AwsAccessKeyId"},
                      web["secrets"])
        self.assertIn({"source": "selfie_web_aws_secret_access_key",
                       "target": "SelfieVerification__AwsSecretAccessKey"}, web["secrets"])
        self.assertIn({"source": "selfie_worker_aws_access_key_id", "target": "SelfieVerification__AwsAccessKeyId"},
                      worker["secrets"])
        self.assertFalse(any("worker" in item["source"] for item in web["secrets"]))
        self.assertFalse(any("selfie_web" in item["source"] for item in worker["secrets"]))

    def test_secret_files_follow_the_directory_of_the_existing_secrets(self) -> None:
        for existing, expected in [
            ("/etc/duora/secrets/duora_app_connection", "/etc/duora/secrets/selfie-web-aws-access-key-id"),
            ("${DUORA_SECRETS_DIR:?defina DUORA_SECRETS_DIR}/duora_app_connection",
             "${DUORA_SECRETS_DIR:?defina DUORA_SECRETS_DIR}/selfie-web-aws-access-key-id"),
        ]:
            with self.subTest(existing=existing):
                patched = selfie_compose.enable(coolify_compose(existing))
                self.assertEqual({"file": expected}, patched["secrets"]["selfie_web_aws_access_key_id"])
                self.assertEqual(4, sum(1 for name in patched["secrets"] if name.startswith("selfie_")))

    def test_enable_is_idempotent_and_leaves_other_services_and_the_input_untouched(self) -> None:
        original = coolify_compose()
        snapshot = copy.deepcopy(original)

        once = selfie_compose.enable(original)
        twice = selfie_compose.enable(once)

        self.assertEqual(snapshot, original)
        self.assertEqual(once, twice)
        self.assertEqual(snapshot["services"]["postgres"], once["services"]["postgres"])
        self.assertEqual(snapshot["name"], once["name"])

    def test_disable_restores_the_exact_original(self) -> None:
        original = coolify_compose()

        self.assertEqual(original, selfie_compose.disable(selfie_compose.enable(original)))
        self.assertEqual(original, selfie_compose.disable(original))

    def test_refuses_conflicting_or_unexpected_shapes(self) -> None:
        conflicting = coolify_compose()
        conflicting["services"]["web"]["environment"]["SelfieVerification__Enabled"] = "false"
        plain_text_key = coolify_compose()
        plain_text_key["services"]["worker"]["environment"]["AWS_ACCESS_KEY_ID"] = "x"
        missing_worker = coolify_compose()
        del missing_worker["services"]["worker"]
        list_environment = coolify_compose()
        list_environment["services"]["web"]["environment"] = ["A=b"]
        other_target = coolify_compose()
        other_target["services"]["web"]["secrets"].append(
            {"source": "other", "target": "SelfieVerification__AwsAccessKeyId"})
        no_secret_directory = coolify_compose()
        no_secret_directory["secrets"] = {}
        for name, compose in [("conflicting", conflicting), ("plain AWS key", plain_text_key),
                              ("missing worker", missing_worker), ("list environment", list_environment),
                              ("other secret target", other_target), ("no secrets", no_secret_directory)]:
            with self.subTest(name), self.assertRaises(selfie_compose.ComposeError):
                selfie_compose.enable(compose)


def _docker_available() -> bool:
    try:
        return subprocess.run(["docker", "compose", "version"], capture_output=True, check=False).returncode == 0
    except OSError:
        return False


@unittest.skipUnless(_docker_available(), "precisa do docker compose")
class RepositoryComposeTests(unittest.TestCase):
    """O patch do Coolify reproduz a configuração da selfie do compose.prod.yml revisado, serviço a serviço."""

    def test_patched_coolify_json_matches_the_reviewed_compose(self) -> None:
        repository = Path(__file__).resolve().parents[2]
        with TemporaryDirectory() as directory:
            environment = {"DUORA_SECRETS_DIR": directory + "/secrets", "DUORA_MEDIA_DIR": directory,
                           "DUORA_DOMAIN": "x", "DUORA_TLS": "internal", "DUORA_HTTP_PORT": "80",
                           "DUORA_HTTPS_PORT": "443", "DUORA_SELFIE_ENABLED": "true",
                           "PATH": "/usr/local/bin:/usr/bin:/bin:/opt/homebrew/bin"}
            environment |= {f"DUORA_{role}_IMAGE": "synthetic" for role in
                            ("WEB", "WORKER", "EDGE", "POSTGRES", "MIGRATOR", "BACKUP")}
            rendered = json.loads(subprocess.run(
                ["docker", "compose", "-f", str(repository / "infra/compose/compose.prod.yml"), "config",
                 "--format", "json"], capture_output=True, text=True, check=True, env=environment).stdout)

        patched = selfie_compose.enable(selfie_compose.disable(rendered))

        for service in ("web", "worker"):
            with self.subTest(service=service):
                def selfie(compose: dict) -> tuple[dict, list]:
                    block = compose["services"][service]
                    return ({k: v for k, v in block["environment"].items() if k.startswith("SelfieVerification")},
                            sorted(json.dumps(item, sort_keys=True) for item in block["secrets"]
                                   if item["source"].startswith("selfie_")))
                self.assertEqual(selfie(rendered), selfie(patched))
        self.assertEqual({name: value["file"] for name, value in rendered["secrets"].items() if name.startswith("selfie_")},
                         {name: value["file"] for name, value in patched["secrets"].items() if name.startswith("selfie_")})


class CliTests(unittest.TestCase):
    def test_cli_writes_the_patched_json_and_reports_its_sha256(self) -> None:
        with TemporaryDirectory() as directory:
            source = Path(directory) / "compose.coolify.json"
            source.write_text(json.dumps(coolify_compose()), encoding="utf-8")

            result = subprocess.run([sys.executable, "-I", str(SCRIPT), "enable", str(source)],
                                    capture_output=True, text=True, check=False)

            self.assertEqual(0, result.returncode, result.stderr)
            patched = json.loads(result.stdout)
            self.assertEqual("true", patched["services"]["web"]["environment"]["SelfieVerification__Enabled"])
            self.assertRegex(result.stderr, r"sha256 [0-9a-f]{64}")
            self.assertEqual(coolify_compose(), json.loads(source.read_text(encoding="utf-8")))

    def test_cli_refusal_exits_non_zero_without_output(self) -> None:
        with TemporaryDirectory() as directory:
            source = Path(directory) / "compose.coolify.json"
            source.write_text(json.dumps({"services": {}}), encoding="utf-8")

            result = subprocess.run([sys.executable, "-I", str(SCRIPT), "enable", str(source)],
                                    capture_output=True, text=True, check=False)

            self.assertEqual(1, result.returncode)
            self.assertEqual("", result.stdout)


if __name__ == "__main__":
    unittest.main()
