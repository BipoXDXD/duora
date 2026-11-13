"""Testes do patch opt-in do media-sanitizer no Compose do Coolify (#174).

Rodar da raiz: python3 -m unittest discover -s scripts/deploy -v
"""

import copy
import json
import re
import subprocess
import sys
import unittest
from pathlib import Path
from tempfile import TemporaryDirectory

import media_sanitizer_compose as patch

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = Path(__file__).with_name("media_sanitizer_compose.py")
PROFILE = "ab" * 32
LAUNCHER_UID = 997
CLIENT_GID = 988


def coolify_compose() -> dict:
    """Forma do `docker compose config --format json`: environment em dict, volumes em lista de objetos."""
    media = [{"type": "bind", "source": f"/var/lib/duora-media/{area}", "target": f"/var/lib/duora-media/{area}",
              "read_only": False, "bind": {"create_host_path": False}} for area in ("quarantine", "derivatives")]
    return {
        "name": "duora",
        "services": {
            "web": {"image": "${DUORA_WEB_IMAGE}", "user": "1654:1654",
                    "environment": {"Media__Review": ""}, "volumes": copy.deepcopy(media)},
            "worker": {"image": "${DUORA_WORKER_IMAGE}", "user": "1654:1654",
                       "environment": {"Media__Review": "", "Media__Storage__Provider": "FileSystem"},
                       "volumes": copy.deepcopy(media)},
            "postgres": {"image": "${DUORA_POSTGRES_IMAGE}"},
        },
    }


def enable(compose: dict) -> dict:
    return patch.enable(compose, launcher_uid=LAUNCHER_UID, client_gid=CLIENT_GID, profile_sha256=PROFILE)


class EnableTests(unittest.TestCase):
    def test_only_the_worker_gets_the_socket_group_bind_and_configuration(self) -> None:
        patched = enable(coolify_compose())

        worker = patched["services"]["worker"]
        self.assertEqual({"Media__Processing__Sanitizer__LauncherSocket": "/run/duora-media/control.sock",
                          "Media__Processing__Sanitizer__LauncherUid": "997",
                          "Media__Processing__Sanitizer__ProfileSha256": PROFILE},
                         {k: v for k, v in worker["environment"].items() if "Sanitizer" in k})
        self.assertEqual(["988"], worker["group_add"])
        self.assertIn({"type": "bind", "source": "/run/duora-media", "target": "/run/duora-media",
                       "read_only": True, "bind": {"create_host_path": False}}, worker["volumes"])
        self.assertEqual(coolify_compose()["services"]["web"], patched["services"]["web"])
        self.assertEqual(coolify_compose()["services"]["postgres"], patched["services"]["postgres"])

    def test_upload_switch_and_other_worker_settings_are_untouched(self) -> None:
        patched = enable(coolify_compose())

        worker = patched["services"]["worker"]
        self.assertEqual("", worker["environment"]["Media__Review"])
        self.assertEqual("1654:1654", worker["user"])
        self.assertEqual(coolify_compose()["services"]["worker"]["volumes"], worker["volumes"][:2])

    def test_enable_is_idempotent_and_never_mutates_its_input(self) -> None:
        original = coolify_compose()
        snapshot = copy.deepcopy(original)

        once = enable(original)

        self.assertEqual(snapshot, original)
        self.assertEqual(once, enable(once))

    def test_disable_restores_the_exact_original_and_is_a_no_op_when_off(self) -> None:
        original = coolify_compose()

        self.assertEqual(original, patch.disable(enable(original)))
        self.assertEqual(original, patch.disable(original))

    def test_invalid_identities_or_profile_are_refused(self) -> None:
        for name, arguments in [
            ("root launcher", {"launcher_uid": 0}), ("negative gid", {"client_gid": -1}),
            ("root gid", {"client_gid": 0}), ("worker uid as launcher", {"launcher_uid": 1654}),
            ("short profile", {"profile_sha256": "ab" * 31}), ("uppercase profile", {"profile_sha256": "AB" * 32}),
            ("non hex profile", {"profile_sha256": "zz" * 32}), ("bool uid", {"launcher_uid": True}),
        ]:
            values = {"launcher_uid": LAUNCHER_UID, "client_gid": CLIENT_GID, "profile_sha256": PROFILE} | arguments
            with self.subTest(name), self.assertRaises(patch.ComposeError):
                patch.enable(coolify_compose(), **values)

    def test_conflicting_or_unexpected_shapes_are_refused(self) -> None:
        conflicting = coolify_compose()
        conflicting["services"]["worker"]["environment"]["Media__Processing__Sanitizer__ProfileSha256"] = "cd" * 32
        other_group = coolify_compose()
        other_group["services"]["worker"]["group_add"] = ["999"]
        other_mount = coolify_compose()
        other_mount["services"]["worker"]["volumes"].append(
            {"type": "bind", "source": "/run", "target": "/run/duora-media", "read_only": False})
        docker_socket = coolify_compose()
        docker_socket["services"]["worker"]["volumes"].append(
            {"type": "bind", "source": "/var/run/docker.sock", "target": "/var/run/docker.sock"})
        web_mount = coolify_compose()
        web_mount["services"]["web"]["volumes"].append(
            {"type": "bind", "source": "/run/duora-media", "target": "/run/duora-media"})
        missing_worker = coolify_compose()
        del missing_worker["services"]["worker"]
        list_environment = coolify_compose()
        list_environment["services"]["worker"]["environment"] = ["A=b"]
        string_volume = coolify_compose()
        string_volume["services"]["worker"]["volumes"].append("/run/duora-media:/run/duora-media")
        for name, compose in [("conflicting profile", conflicting), ("other group", other_group),
                              ("other mount on target", other_mount), ("docker socket", docker_socket),
                              ("socket on web", web_mount), ("missing worker", missing_worker),
                              ("list environment", list_environment), ("short volume syntax", string_volume)]:
            with self.subTest(name), self.assertRaises(patch.ComposeError):
                enable(compose)


class RoleContractTests(unittest.TestCase):
    """Os valores fixos do patch são os do role e do template do launcher, não uma segunda fonte."""

    def test_socket_path_and_directory_match_the_role_and_the_unit(self) -> None:
        defaults = (ROOT / "infra/ansible/roles/media_sanitizer/defaults/main.yml").read_text(encoding="utf-8")
        unit = (ROOT / "infra/media/systemd/duora-media-launcher.service").read_text(encoding="utf-8")

        self.assertIn(f"media_sanitizer_control_socket: {patch.CONTROL_SOCKET}\n", defaults)
        self.assertIn(f"RuntimeDirectory={patch.SOCKET_DIRECTORY.removeprefix('/run/')}\n", unit)
        self.assertTrue(patch.CONTROL_SOCKET.startswith(patch.SOCKET_DIRECTORY + "/"))

    def test_worker_uid_matches_the_production_compose(self) -> None:
        compose = (ROOT / "infra/compose/compose.prod.yml").read_text(encoding="utf-8")
        worker = compose.split("\n  worker:\n", 1)[1].split("\n  edge:\n", 1)[0]

        self.assertIn(f'user: "{patch.WORKER_UID}:{patch.WORKER_UID}"', worker)

    def test_repository_compose_stays_without_the_sanitizer(self) -> None:
        compose = (ROOT / "infra/compose/compose.prod.yml").read_text(encoding="utf-8")

        self.assertNotIn("Sanitizer", compose)
        self.assertNotIn("group_add", compose)
        self.assertIsNone(re.search(r"/run/duora-media", compose))


def _docker_available() -> bool:
    try:
        return subprocess.run(["docker", "compose", "version"], capture_output=True, check=False).returncode == 0
    except OSError:
        return False


@unittest.skipUnless(_docker_available(), "precisa do docker compose")
class RenderedComposeTests(unittest.TestCase):
    """O patch aceita o compose.prod.yml renderizado (a forma do JSON do Coolify) e o devolve intacto no disable."""

    def test_enable_then_disable_round_trips_the_rendered_production_compose(self) -> None:
        with TemporaryDirectory() as directory:
            environment = {"DUORA_SECRETS_DIR": directory + "/secrets", "DUORA_MEDIA_DIR": directory,
                           "DUORA_DOMAIN": "x", "DUORA_TLS": "internal", "DUORA_HTTP_PORT": "80",
                           "DUORA_HTTPS_PORT": "443", "PATH": "/usr/local/bin:/usr/bin:/bin:/opt/homebrew/bin"}
            environment |= {f"DUORA_{role}_IMAGE": "synthetic" for role in
                            ("WEB", "WORKER", "EDGE", "POSTGRES", "MIGRATOR", "BACKUP")}
            rendered = json.loads(subprocess.run(
                ["docker", "compose", "-f", str(ROOT / "infra/compose/compose.prod.yml"), "config", "--format",
                 "json"], capture_output=True, text=True, check=True, env=environment).stdout)

        patched = enable(rendered)

        self.assertEqual(rendered, patch.disable(patched))
        self.assertEqual(["988"], patched["services"]["worker"]["group_add"])
        changed = [name for name in rendered["services"] if rendered["services"][name] != patched["services"][name]]
        self.assertEqual(["worker"], changed)


class CliTests(unittest.TestCase):
    def _run(self, *arguments: str) -> subprocess.CompletedProcess[str]:
        return subprocess.run([sys.executable, "-I", str(SCRIPT), *arguments], capture_output=True, text=True,
                              check=False)

    def test_enable_writes_the_patched_json_and_reports_its_sha256(self) -> None:
        with TemporaryDirectory() as directory:
            source = Path(directory) / "compose.coolify.json"
            source.write_text(json.dumps(coolify_compose()), encoding="utf-8")

            result = self._run("enable", "--launcher-uid", str(LAUNCHER_UID), "--client-gid", str(CLIENT_GID),
                               "--profile-sha256", PROFILE, str(source))

            self.assertEqual(0, result.returncode, result.stderr)
            self.assertEqual(enable(coolify_compose()), json.loads(result.stdout))
            self.assertRegex(result.stderr, r"sha256 [0-9a-f]{64}")
            self.assertEqual(coolify_compose(), json.loads(source.read_text(encoding="utf-8")))

    def test_disable_needs_no_identity(self) -> None:
        with TemporaryDirectory() as directory:
            source = Path(directory) / "compose.coolify.json"
            source.write_text(json.dumps(enable(coolify_compose())), encoding="utf-8")

            result = self._run("disable", str(source))

            self.assertEqual(0, result.returncode, result.stderr)
            self.assertEqual(coolify_compose(), json.loads(result.stdout))

    def test_refusal_or_missing_option_exits_non_zero_without_output(self) -> None:
        with TemporaryDirectory() as directory:
            source = Path(directory) / "compose.coolify.json"
            source.write_text(json.dumps(coolify_compose()), encoding="utf-8")

            for arguments in [("enable", str(source)),
                              ("enable", "--launcher-uid", "0", "--client-gid", "988", "--profile-sha256", PROFILE,
                               str(source))]:
                with self.subTest(arguments=arguments):
                    result = self._run(*arguments)
                    self.assertNotEqual(0, result.returncode)
                    self.assertEqual("", result.stdout)


if __name__ == "__main__":
    unittest.main()
