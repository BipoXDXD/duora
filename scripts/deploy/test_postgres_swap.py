"""Synthetic protocol tests for the PostgreSQL digest swap: real files, fake Docker. No lab, host or PITR proof."""
import copy
import contextlib
import dataclasses
import fcntl
import hashlib
import io
import json
import os
import shutil
import signal
import subprocess
import sys
import tempfile
import textwrap
import unittest
from datetime import datetime, timezone
from pathlib import Path
from unittest.mock import patch

import duora_deploy as deploy
import install_executor as installer
import postgres_swap as swap
import runtime_guard as guard
from test_duora_deploy import PREFIX, valid_manifest
from test_runtime_guard import reviewed_profile

OLD_DIGEST = "sha256:" + "a" * 64
NEW_DIGEST = "sha256:" + "b" * 64
OLD_REF = PREFIX + "postgres@" + OLD_DIGEST
NEW_REF = PREFIX + "postgres@" + NEW_DIGEST
MIGRATION = "20261105120000_RegistrationReconciliationManifest"
WINDOW = datetime(2026, 11, 10, 12, 0, tzinfo=timezone.utc)  # A Tuesday inside the allowed window.
COMMIT = "c" * 40
MUTATIONS = (" stop ", " up ", " down", " rm ")
COMMIT_ARTIFACTS = 6


def source_manifest() -> dict:
    raw = valid_manifest()
    raw.update(releaseId="2026.11.05-f6132af-1-amd64", contractVersion="v2", architecture="amd64",
               migrations=[MIGRATION], schema={"min": MIGRATION, "max": MIGRATION})
    return raw


class FakeDocker:
    """Answers like Docker for the commands the swap uses; failures are injected by substring."""

    def __init__(self) -> None:
        self.commands: list[str] = []
        self.timeouts: dict[str, int | None] = {}
        # generation is the container id (recreation); starts is startedAt (any start, same id after a restart).
        self.containers = {role: {"image": OLD_REF, "generation": 1, "starts": 1, "running": True}
                           for role in ("postgres", "backup")}
        self.applications = {"web": True, "worker": True, "edge": True}
        self.fail_on: set[str] = set()
        self.photo = {"systemIdentifier": "7000", "catalogVersion": 202506291, "controlVersion": 1800, "timeline": 1,
                      "head": MIGRATION, "acl": "d41d8", "collation": {"provider": "c", "collate": "en_US.utf8",
                      "ctype": "en_US.utf8", "version": "2.41"}}
        self.live = {"serverVersionNum": "180006", "controlVersion": 1800, "catalogVersion": 202506291,
                     "encoding": "UTF8", "provider": "c", "collate": "en_US.utf8", "ctype": "en_US.utf8",
                     "locale": None, "recorded": "2.41", "actual": "2.41"}
        self.probe = copy.deepcopy(self.live)
        self.labels = {OLD_REF: {"io.duora.postgres.version": "18.6", "io.duora.pgbackrest.version": "2.57.0"},
                       NEW_REF: {"io.duora.postgres.version": "18.6", "io.duora.pgbackrest.version": "2.57.0"}}
        self.local = {OLD_REF, NEW_REF}
        self.postgres_version = "postgres (PostgreSQL) 18.6 (Debian 18.6-1.pgdg13+1)"
        self.live_pgbackrest = "pgBackRest 2.57.0"
        self.target_pgbackrest = "pgBackRest 2.57.0"
        self.literal_compose_digest = False
        self.other_service_drift = False
        self.counts = {"postgres": 1, "backup": 1}
        self.holds: set[str] = set()

    def __call__(self, command: list[str], *, capture: bool = False, input_text: str | None = None,
                 timeout_seconds: int | None = None) -> subprocess.CompletedProcess[str]:
        joined = " ".join(command)
        self.timeouts[command[-1]] = timeout_seconds
        self.commands.append(joined)
        absent = ((command[:3] == ["docker", "image", "inspect"] and command[-1] not in self.local)
                  or (command[:3] == ["docker", "container", "inspect"] and command[-1] not in self.holds))
        if absent or any(failure in joined for failure in self.fail_on):
            return subprocess.CompletedProcess(command, 1, "", "falha simulada")
        return subprocess.CompletedProcess(command, 0, self._answer(command, joined), "")

    def _answer(self, command: list[str], joined: str) -> str:
        if command[:2] == ["docker", "compose"]:
            return self._compose(command, joined)
        if command[:3] == ["docker", "image", "inspect"]:
            reference = command[-1]
            return json.dumps({"Id": "sha256:" + reference[-64:], "Architecture": "amd64",
                               "Config": {"Labels": self.labels[reference]}})
        if command[:2] == ["docker", "ps"]:
            service = [item for item in command if item.startswith("label=com.docker.compose.service=")]
            role = service[0].rsplit("=", 1)[1] if service else ""
            return "\n".join("%064x" % (index + 1) for index in range(self.counts.get(role, 0)))
        if command[:2] == ["docker", "inspect"]:
            return json.dumps([{"Id": identifier, "Config": {"Labels": {"com.docker.compose.project": "duora",
                "com.docker.compose.service": "migrator", "com.docker.compose.oneoff": "True"}},
                "State": {"Status": "running", "Running": True, "Paused": False, "Restarting": False, "Pid": 7}}
                for identifier in command[2:]])
        if command[:2] == ["docker", "top"]:
            return "PID COMMAND\n1 postgres\n"
        if command[:2] == ["docker", "create"]:
            self.holds.add(command[command.index("--name") + 1])
            return "created\n"
        if command[:2] == ["docker", "rm"]:
            self.holds.discard(command[-1])
            return command[-1] + "\n"
        if command[:3] == ["docker", "container", "inspect"]:
            return "exists\n"
        if command[:2] == ["docker", "run"]:
            entry = command[command.index("--entrypoint") + 1]
            if entry == "postgres":
                return self.postgres_version + "\n"
            if entry == "pgbackrest":
                return self.target_pgbackrest + "\n"
            return "noise from initdb\n" + json.dumps(self.probe) + "\n"
        return ""

    def _compose(self, command: list[str], joined: str) -> str:
        environment = [command[i + 1] for i, item in enumerate(command) if item == "--env-file"][-1]
        image = OLD_REF
        for line in Path(environment).read_text().splitlines():
            if line.startswith("DUORA_POSTGRES_IMAGE="):
                image = line.split("=", 1)[1]
        if "config --format json" in joined:
            shown = OLD_REF if self.literal_compose_digest else image
            services = {"postgres": {"image": shown}, "backup": {"image": shown},
                        "web": {"image": "web", "drift": self.other_service_drift and image == NEW_REF}}
            return json.dumps({"name": "duora", "services": services})
        if " exec " in joined:
            return self._execute(joined)
        if " stop " in joined:
            for role in command[command.index("stop") + 1:]:
                if role in self.containers:
                    self.containers[role]["running"] = False
                else:
                    self.applications[role] = False
        if " up " in joined:
            for role in command[command.index("--no-deps") + 1:]:
                if role in self.containers:
                    item = self.containers[role]
                    if item["image"] != image:
                        item.update(image=image, running=True, generation=item["generation"] + 1, starts=item["starts"] + 1)
                    elif not item["running"]:
                        item.update(running=True, starts=item["starts"] + 1)
                else:
                    self.applications[role] = True
        if " ps " in joined:
            return "".join(f"{role}={'running' if up else 'exited'}\n" for role, up in self.applications.items())
        return ""

    def _execute(self, joined: str) -> str:
        if "'systemIdentifier'" in joined:
            return json.dumps(self.photo) + "\n"
        if "'serverVersionNum'" in joined:
            return json.dumps(self.live) + "\n"
        if "to_regclass" in joined:
            return "t\n"
        if 'max("MigrationId")' in joined:
            return MIGRATION + "\n"
        if "pg_stat_activity" in joined:
            return "0\n"
        if " pgbackrest version" in joined:
            return self.live_pgbackrest + "\n"
        return ""

    def restart(self) -> None:
        """Reboot or OOM: same container id, new startedAt."""
        for item in self.containers.values():
            item["starts"] += 1

    def mutating(self) -> list[str]:
        # The disposable probe script mentions "stop" but never reaches the project containers.
        compose = [command for command in self.commands if command.startswith("docker compose")]
        host = [command for command in self.commands if command.startswith(("docker create", "docker rm"))]
        return host + [command for command in compose if any(item in command for item in MUTATIONS)]


class SwapFixture(unittest.TestCase):
    def setUp(self) -> None:
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.root = Path(directory.name).resolve()
        self.state = self.root / "state"
        self.ingress = self.root / "ingress"
        self.opt = self.root / "opt/releases"
        for path in (self.state / "releases", self.ingress / "duora", self.opt, self.root / "etc"):
            path.mkdir(parents=True)
        self.compose_file = self.root / "compose.coolify.json"
        self.compose_file.write_text("{}\n")
        self.host_env = self.root / "duora.env"
        self.host_env.write_text("DUORA_IMAGE_PREFIX=" + PREFIX + "\n")
        self.paths = deploy.Paths(self.compose_file, self.host_env, self.state, Path("/synthetic/duora-backup"),
                                  ingress=self.ingress)
        self.raw = source_manifest()
        self.release = deploy.parse_manifest(json.dumps(self.raw), image_prefix=PREFIX)
        self.docker = FakeDocker()
        self.seal_source_files()
        self.seal(self.observe(self.release, None)["profile"])
        self.processes: list[list[str]] = []
        self.idle_error: Exception | None = None
        self.preflight_error: Exception | None = None
        self.now = WINDOW
        self.identity = {"sourceCommit": COMMIT, "manifestSha256": "e" * 64, "executorSha256": "f" * 64}
        self.before = self.snapshot()

    def seal_source_files(self) -> None:
        rid = self.raw["releaseId"]
        text = json.dumps(self.raw, indent=2, sort_keys=True) + "\n"
        self.write(self.state / "releases" / (rid + ".json"), text)
        self.write(self.opt / (rid + ".json"), text)
        lines = [f"DUORA_{name.upper()}_IMAGE={ref}" for name, ref in self.release.images.items()]
        self.write(self.state / "releases" / (rid + ".env"), "\n".join(lines + ["DUORA_RELEASE_ID=" + rid]) + "\n")
        self.write(self.state / "current.json", json.dumps(
            {"releaseId": rid, "commit": self.raw["commit"], "deployedAt": "2026-11-05T10:00:00+00:00"}, indent=2) + "\n")
        self.write(self.state / "previous.json", '{"releaseId":"older"}\n')
        self.pin = self.root / "etc/coolify-deploy.json"
        pin = {"application_uuid": "app-1", "compose": str(self.compose_file), "compose_sha256": "0" * 64,
               "manifest": str(self.opt / (rid + ".json")),
               "manifest_sha256": hashlib.sha256(text.encode()).hexdigest(),
               "observer_environment": {"DUORA_OBSERVER_IMAGE": self.release.images["edge"]}}
        self.write(self.pin, json.dumps(pin, indent=2) + "\n")

    def write(self, path: Path, text: str) -> None:
        path.write_text(text)
        path.chmod(0o600)

    def seal(self, profile: dict) -> None:
        self.write(self.state / guard.BASELINE_NAME, json.dumps(profile))

    def observe(self, release: deploy.Release, env: Path | None) -> dict:
        wanted = release.images["postgres"]
        if env is not None:
            lines = dict(line.split("=", 1) for line in env.read_text().splitlines())
            if lines["DUORA_POSTGRES_IMAGE"] != wanted:
                raise deploy.DeployError("synthetic Compose differs from the requested component")
        profile = reviewed_profile()
        for role, item in self.docker.containers.items():
            if item["image"] != wanted or not item["running"]:
                raise deploy.DeployError("synthetic runtime differs")
            profile["services"][role].update(
                id="%064x" % (item["generation"] * 2 + (role == "backup")), imageId="sha256:" + item["image"][-64:],
                startedAt=f"2026-11-05T10:{item['starts'] % 60:02d}:00Z")
        return {"profile": profile}

    def host(self) -> swap.SwapHost:
        def require_idle(uuid: str) -> None:
            self.assertEqual(uuid, "app-1")
            if self.idle_error is not None:
                raise self.idle_error
        return swap.SwapHost(core=deploy, image_prefix=PREFIX, releases_root=self.opt, pin_path=self.pin,
                               read_protected=lambda path: path.read_bytes(), inspect_runtime=self.observe,
                               require_ingress_idle=require_idle, executor_identity=lambda: self.identity,
                               list_processes=lambda: [item if isinstance(item, swap.ProcessInfo) else swap.ProcessInfo(item)
                                                       for item in self.processes],
                               now=lambda: self.now, sleep=lambda _: None, preflight=self.require_preflight)

    def require_preflight(self) -> None:
        if self.preflight_error is not None:
            raise self.preflight_error

    def call(self, action: str, *, reviewed: str | None = None, target: str = NEW_REF, release=None) -> dict:
        return swap.postgres_swap_entry(release or self.release, target, self.paths, self.docker, action=action,
                                          reviewed_plan_sha256=reviewed, host=self.host())

    def plan(self) -> str:
        return self.call("plan")["planSha256"]

    def apply(self) -> dict:
        return self.call("apply", reviewed=self.plan())

    def snapshot(self) -> dict:
        tracked = (self.state / "current.json", self.state / "previous.json", self.pin)
        result: dict[str, object] = {str(path): path.read_bytes() for path in tracked}
        result["releases"] = sorted(path.name for path in (self.state / "releases").iterdir())
        result["opt"] = sorted(path.name for path in self.opt.iterdir())
        return result

    def phase(self) -> str:
        return json.loads((self.state / "postgres-swap/phase.json").read_text())["phase"]

    def fence(self) -> Path:
        return self.ingress / "duora-postgres-swap/pending.json"

    def assert_closed(self) -> None:
        self.assertFalse((self.state / "postgres-swap/attempt.json").exists())
        self.assertFalse(self.fence().exists())
        self.assertEqual(self.docker.holds, set())
        self.assertEqual(list((self.state / "staging").glob("*")) if (self.state / "staging").exists() else [], [])
        self.assertEqual(len(list((self.state / "postgres-swap-history").iterdir())), 1)

    def stop_at_commit(self) -> None:
        with patch.object(swap, "_swap_commit", side_effect=deploy.DeployError("synthetic stop before step 7")), \
                self.assertRaises(deploy.DeployError):
            self.apply()
        self.assertEqual(self.phase(), "commit")


class HappyPathTests(SwapFixture):
    def test_plan_is_deterministic_and_changes_nothing(self) -> None:
        first = self.call("plan")
        second = self.call("plan")
        self.assertEqual(first["planSha256"], second["planSha256"])
        self.assertEqual(self.docker.mutating(), [])
        self.assertEqual(self.snapshot(), self.before)
        self.assertFalse((self.state / "postgres-swap").exists())
        self.assertFalse(self.fence().exists())

    def test_steps_run_in_design_order_and_never_down_or_volume_removal(self) -> None:
        self.assertEqual(self.apply()["status"], "awaiting-reseal")
        commands = self.docker.commands
        def position(text: str, start: int = 0) -> int:
            return next(i for i in range(start, len(commands)) if text in commands[i])
        apply_start = position("docker create")
        verify = position("/synthetic/duora-backup verify", apply_start)
        worker = position("stop worker", apply_start)
        web = position("stop web", apply_start)
        photo = next(i for i, c in enumerate(commands) if i > web and "'systemIdentifier'" in c)
        check = position("--stanza=duora check", apply_start)
        postgres = position("stop postgres", apply_start)
        backup = position("stop backup", apply_start)
        up_backup = position("--no-deps backup", apply_start)
        up_postgres = position("--no-deps postgres", apply_start)
        second_verify = position("/synthetic/duora-backup verify", up_postgres)
        application = position("--no-deps web worker edge", apply_start)
        self.assertLess(verify, worker)
        self.assertLess(worker, web)
        self.assertLess(web, photo)
        self.assertLess(photo, check)
        self.assertLess(check, postgres)
        self.assertLess(postgres, backup)
        self.assertLess(backup, up_backup)
        self.assertLess(up_backup, up_postgres)
        self.assertLess(up_postgres, second_verify)
        self.assertLess(second_verify, application)
        self.assertFalse([c for c in commands if " down" in c or " -v" in c.split("--env-file")[-1]])
        self.assertFalse([c for c in commands if " stop " in c and "edge" in c])

    def test_commit_publishes_exact_release_pin_and_current_after_previous(self) -> None:
        original_current = (self.state / "current.json").read_bytes()
        original_pin = json.loads(self.pin.read_text())
        self.apply()
        current = json.loads((self.state / "current.json").read_text())
        rid = current["releaseId"]
        self.assertEqual(set(current), {"releaseId", "commit", "deployedAt"})
        self.assertEqual(current["commit"], self.raw["commit"])
        self.assertEqual((self.state / "previous.json").read_bytes(), original_current)
        manifest = (self.opt / (rid + ".json")).read_bytes()
        self.assertEqual((self.state / "releases" / (rid + ".json")).read_bytes(), manifest)
        target = deploy.parse_manifest(manifest.decode(), image_prefix=PREFIX)
        self.assertEqual(target.images, dict(self.release.images, postgres=NEW_REF))
        self.assertEqual(target.previous_release_id, self.raw["releaseId"])
        self.assertEqual(target.commit, self.raw["commit"])
        self.assertEqual(target.raw["migrations"], self.raw["migrations"])
        unchanged = {k: v for k, v in target.raw.items() if k not in ("releaseId", "createdAt", "images", "sbom", "gates", "previousReleaseId")}
        self.assertEqual(unchanged, {k: self.raw[k] for k in unchanged})
        self.assertEqual({k: v for k, v in target.raw["sbom"].items() if k != "postgres"},
                         {k: v for k, v in self.raw["sbom"].items() if k != "postgres"})
        self.assertIn("DUORA_POSTGRES_IMAGE=" + NEW_REF, (self.state / "releases" / (rid + ".env")).read_text())
        pin = json.loads(self.pin.read_text())
        self.assertEqual(pin["manifest"], str(self.opt / (rid + ".json")))
        self.assertEqual(pin["manifest_sha256"], hashlib.sha256(manifest).hexdigest())
        self.assertEqual({k: v for k, v in pin.items() if not k.startswith("manifest")},
                         {k: v for k, v in original_pin.items() if not k.startswith("manifest")})
        self.assertEqual(self.pin.read_bytes(), deploy._recovery_pin_bytes(pin))
        self.assertEqual(self.phase(), "awaiting-reseal")
        self.assertTrue(self.fence().exists())

    def test_journal_is_private_and_never_copies_the_observer_pin_values(self) -> None:
        pin = json.loads(self.pin.read_text())
        pin["observer_environment"]["CANARY"] = "CANARY-swap-pin"
        self.write(self.pin, json.dumps(pin, indent=2) + "\n")
        self.apply()
        journal = self.state / "postgres-swap"
        self.assertEqual(journal.stat().st_mode & 0o777, 0o700)
        for item in journal.iterdir():
            self.assertEqual(item.stat().st_mode & 0o777, 0o600)
            self.assertNotIn(b"CANARY-swap-pin", item.read_bytes())

    def test_second_process_closes_only_after_reseal_and_verify_repo(self) -> None:
        self.apply()
        target = deploy.parse_manifest(self.opt.joinpath(json.loads(self.pin.read_text())["manifest"]).read_text(), image_prefix=PREFIX)
        with self.assertRaises(deploy.DeployError):
            self.call("resume")
        self.assertEqual(self.phase(), "awaiting-reseal")
        self.seal(self.observe(target, None)["profile"])
        self.docker.commands.clear()
        self.assertEqual(self.call("resume")["status"], "closed")
        self.assert_closed()
        self.assertEqual(sum("/synthetic/duora-backup verify-repo" in c for c in self.docker.commands), 1)
        history = next((self.state / "postgres-swap-history").iterdir())
        self.assertEqual(json.loads((history / "phase.json").read_text())["phase"], "closing")
        self.assertFalse((self.state / "staging" / (target.release_id + ".env")).exists())

    def test_reverse_swap_uses_the_same_command_and_creates_a_new_release(self) -> None:
        self.apply()
        target = deploy.parse_manifest(Path(json.loads(self.pin.read_text())["manifest"]).read_text(), image_prefix=PREFIX)
        self.seal(self.observe(target, None)["profile"])
        self.call("resume")
        self.docker.commands.clear()
        plan = self.call("plan", target=OLD_REF, release=target)["planSha256"]
        self.call("apply", reviewed=plan, target=OLD_REF, release=target)
        reverse = deploy.parse_manifest(Path(json.loads(self.pin.read_text())["manifest"]).read_text(), image_prefix=PREFIX)
        self.assertEqual(reverse.previous_release_id, target.release_id)
        self.assertEqual(reverse.images["postgres"], OLD_REF)
        self.assertNotEqual(reverse.release_id, self.raw["releaseId"])
        self.assertEqual(self.docker.containers["postgres"]["image"], OLD_REF)

    def test_image_holds_exist_until_close(self) -> None:
        self.apply()
        self.assertEqual(len(self.docker.holds), 2)

    def test_executor_stays_inside_the_bundle_member_budget(self) -> None:
        for module in (deploy, swap):
            self.assertLessEqual(Path(str(module.__file__)).stat().st_size, 256 * 1024)


class TargetReleaseTests(SwapFixture):
    def committed_target(self) -> deploy.Release:
        self.apply()
        return deploy.parse_manifest(Path(json.loads(self.pin.read_text())["manifest"]).read_text(), image_prefix=PREFIX)

    def test_target_release_passes_the_existing_manifest_parser_and_v2_history(self) -> None:
        target = self.committed_target()
        self.assertEqual(target.raw["schemaVersion"], 1)
        self.assertEqual(target.raw["gates"]["status"], "passed")
        self.assertRegex(target.release_id, deploy._RELEASE_ID.pattern)
        self.assertLessEqual(len(target.release_id), 64)
        self.assertTrue(deploy._minimum_contract_v2(self.state))

    def test_rollback_from_next_release_to_target_is_eligible(self) -> None:
        target = self.committed_target()
        later = "20261106120000_Later"
        following = copy.deepcopy(target.raw)
        following.update(releaseId="next-release", previousReleaseId=target.release_id, migrations=[MIGRATION, later],
                         schema={"min": MIGRATION, "max": later})
        (self.state / "releases/next-release.json").write_text(json.dumps(following))
        (self.state / "current.json").write_text(json.dumps({"releaseId": "next-release"}))
        self.assertTrue(deploy._is_rollback(target, later, self.state))
        deploy._require_release_not_rewritten(target, self.state)

    def test_source_release_stays_ineligible_against_the_target_digest_rule(self) -> None:
        target = self.committed_target()
        self.assertNotEqual(self.release.images["postgres"], target.images["postgres"])
        with self.assertRaises(deploy.DeployError):
            self.observe(self.release, None)


class PreconditionTests(SwapFixture):
    def refused(self, *, reviewed: str | None = None, target: str = NEW_REF, release=None) -> None:
        self.docker.commands.clear()
        for action, hash_value in (("plan", None), ("apply", reviewed or "9" * 64)):
            with self.assertRaises(deploy.DeployError):
                self.call(action, reviewed=hash_value, target=target, release=release)
        self.assertEqual(self.docker.mutating(), [])
        self.assertFalse((self.state / "postgres-swap/attempt.json").exists())
        self.assertFalse(self.fence().exists())
        self.assertEqual(self.snapshot(), self.before)

    def test_each_docker_observed_precondition_refuses_without_effect(self) -> None:
        def change(attribute, **values):
            def apply(docker):
                getattr(docker, attribute).update(values)
            return apply
        cases = {
            "target major label": lambda d: d.labels[NEW_REF].update({"io.duora.postgres.version": "19.0"}),
            "target pgbackrest label": lambda d: d.labels[NEW_REF].update({"io.duora.pgbackrest.version": "2.58.0"}),
            "missing label": lambda d: d.labels[NEW_REF].pop("io.duora.postgres.version"),
            "postgres binary": lambda d: setattr(d, "postgres_version", "postgres (PostgreSQL) 19.0"),
            "garbled postgres binary": lambda d: setattr(d, "postgres_version", "unexpected"),
            "pgbackrest binary": lambda d: setattr(d, "target_pgbackrest", "pgBackRest 2.58.0"),
            "live pgbackrest binary": lambda d: setattr(d, "live_pgbackrest", "pgBackRest 2.56.0"),
            "catalog": change("probe", catalogVersion=1),
            "control": change("probe", controlVersion=1),
            "collation version": change("probe", actual="2.42"),
            "server major": change("probe", serverVersionNum="190000"),
            "garbled probe": lambda d: d.probe.pop("actual"),
            "literal Compose digest": lambda d: setattr(d, "literal_compose_digest", True),
            "other Compose drift": lambda d: setattr(d, "other_service_drift", True),
            "old image not local": lambda d: d.local.discard(OLD_REF),
            "target image not local": lambda d: d.local.discard(NEW_REF),
        }
        for name, mutate in cases.items():
            with self.subTest(name=name):
                self.docker.__init__()
                mutate(self.docker)
                self.refused()

    def test_each_failed_command_refuses_without_effect(self) -> None:
        for failure in ("pull --quiet " + NEW_REF, "pull --quiet " + OLD_REF, "/synthetic/duora-backup verify",
                        "/synthetic/duora-backup verify-repo", "--network none", "'serverVersionNum'"):
            with self.subTest(failure=failure):
                self.docker.__init__()
                self.docker.fail_on = {failure}
                self.refused()

    def test_repository_verification_needs_verify_repo_and_verify(self) -> None:
        self.call("plan")
        self.assertTrue(any(c.endswith("duora-backup verify") for c in self.docker.commands))
        self.assertTrue(any(c.endswith("duora-backup verify-repo") for c in self.docker.commands))

    def test_invalid_targets_refuse(self) -> None:
        for target in (OLD_REF, PREFIX + "web@" + NEW_DIGEST, "ghcr.io/evil/duora-postgres@" + NEW_DIGEST,
                       PREFIX + "postgres:18", PREFIX + "postgres:18@" + NEW_DIGEST, ""):
            with self.subTest(target=target):
                self.refused(target=target)

    def test_identity_preconditions_refuse_without_effect(self) -> None:
        with self.subTest("v1"):
            raw = dict(self.raw, contractVersion="v1")
            self.write(self.state / "releases" / (raw["releaseId"] + ".json"), json.dumps(raw))
            self.before = self.snapshot()
            self.refused(release=deploy.parse_manifest(json.dumps(raw), image_prefix=PREFIX))
            self.seal_source_files()
            self.before = self.snapshot()
        with self.subTest("manifest argument is not the recorded current"):
            self.refused(release=deploy.parse_manifest(json.dumps(dict(self.raw, commit="d" * 40)), image_prefix=PREFIX))
        with self.subTest("arm64"):
            raw = dict(self.raw, architecture="arm64")
            self.write(self.state / "releases" / (raw["releaseId"] + ".json"), json.dumps(raw))
            self.before = self.snapshot()
            self.refused(release=deploy.parse_manifest(json.dumps(raw), image_prefix=PREFIX))
            self.seal_source_files()
            self.before = self.snapshot()

    def test_sealed_digest_or_runtime_drift_refuses(self) -> None:
        profile = self.observe(self.release, None)["profile"]
        drifted = copy.deepcopy(profile)
        drifted["services"]["postgres"]["restartCount"] = 9
        self.seal(drifted)
        self.refused()
        self.seal(profile)
        self.docker.containers["postgres"]["image"] = NEW_REF
        self.refused()

    def test_ingress_and_state_gates_refuse(self) -> None:
        self.idle_error = deploy.DeployError("active Coolify deployment")
        self.refused()
        self.idle_error = ValueError("Unresolved release intent")
        self.refused()
        self.idle_error = None
        install = self.state / "executor-install-pending.json"
        install.write_text("{}")
        self.refused()
        install.unlink()
        recovery = self.state / "current-v2-recovery"
        recovery.mkdir(mode=0o700)
        (recovery / "pending.json").write_text("{}")
        self.refused()
        shutil.rmtree(recovery)

    def test_unverifiable_executor_identity_refuses(self) -> None:
        def absent() -> dict:
            raise deploy.DeployError("no installed executor receipt")
        broken = dataclasses.replace(self.host(), executor_identity=absent)
        for action, reviewed in (("plan", None), ("apply", "9" * 64)):
            with self.assertRaises(deploy.DeployError):
                swap.postgres_swap_entry(self.release, NEW_REF, self.paths, self.docker, action=action,
                                           reviewed_plan_sha256=reviewed, host=broken)
        self.assertEqual(self.docker.mutating(), [])

    def test_time_windows(self) -> None:
        refused = [datetime(2026, 11, 10, 6, 59), datetime(2026, 11, 10, 21, 0), datetime(2026, 11, 10, 3, 0),
                   datetime(2026, 11, 11, 7, 59), datetime(2026, 11, 12, 7, 59)]
        for moment in refused:
            with self.subTest(moment=moment):
                self.now = moment.replace(tzinfo=timezone.utc)
                self.refused()
        for moment in (datetime(2026, 11, 10, 7, 0), datetime(2026, 11, 10, 20, 59), datetime(2026, 11, 11, 8, 0)):
            with self.subTest(moment=moment):
                self.now = moment.replace(tzinfo=timezone.utc)
                self.assertEqual(self.call("plan")["status"], "plan")

    def test_apply_refuses_a_different_plan_hash_and_changed_world(self) -> None:
        reviewed = self.plan()
        self.docker.commands.clear()
        self.docker.live["recorded"] = "2.40"
        self.docker.probe["actual"] = "2.40"
        self.docker.live["actual"] = "2.40"
        with self.assertRaisesRegex(deploy.DeployError, "revisado"):
            self.call("apply", reviewed=reviewed)
        self.assertEqual(self.docker.mutating(), [])
        self.assertFalse((self.state / "postgres-swap/attempt.json").exists())

    def test_open_journal_refuses_a_second_swap(self) -> None:
        self.apply()
        for action, reviewed in (("plan", None), ("apply", "9" * 64)):
            with self.assertRaises(deploy.DeployError):
                self.call(action, reviewed=reviewed)
        self.assertEqual(self.phase(), "awaiting-reseal")


class FenceTests(SwapFixture):
    def receiver(self):
        path = Path(__file__).resolve().parents[1] / "release/coolify/receiver.py"
        receiver = installer._module(path, "swap_test_receiver")
        setattr(receiver, "STATE", self.ingress)
        return receiver

    def test_fence_blocks_the_real_receiver_until_close(self) -> None:
        receiver = self.receiver()
        receiver.require_host_idle()
        self.apply()
        with self.assertRaises(ValueError):
            receiver.require_host_idle()
        target = deploy.parse_manifest(Path(json.loads(self.pin.read_text())["manifest"]).read_text(), image_prefix=PREFIX)
        self.seal(self.observe(target, None)["profile"])
        self.call("resume")
        receiver.require_host_idle()

    def test_installer_exempts_the_exact_journal_this_executor_writes(self) -> None:
        """The two sides of the contract, without a hand-made journal."""
        version = self.root / "versions" / COMMIT
        version.mkdir(parents=True)
        (version / "duora_deploy.py").write_text("# installed\n")
        entry = self.root / "duora-deploy"
        entry.symlink_to(version / "duora_deploy.py")
        self.identity["executorSha256"] = hashlib.sha256(b"# installed\n").hexdigest()
        paths = installer.InstallPaths(entry, self.root / "versions", self.root / "preimages", self.state, self.ingress)
        def exempted():
            with patch.object(installer, "validate_bundle"):
                return installer._resealing_swap_fence(paths, expected_commit=COMMIT, manifest_sha256="e" * 64)
        self.stop_at_commit()
        with self.assertRaises(installer.InstallError):
            exempted()
        self.call("resume")
        self.assertEqual(exempted(), self.fence().parent)

    def test_runtime_recheck_refuses_with_open_journal_or_orphan_fence(self) -> None:
        self.stop_at_commit()
        with patch.object(deploy, "runtime_inventory", side_effect=AssertionError("must refuse before observing")), \
                self.assertRaisesRegex(deploy.DeployError, "swap"):
            deploy.runtime_recheck(self.release, self.paths)
        attempt = self.state / "postgres-swap/attempt.json"
        attempt.rename(self.root / "elsewhere")
        with patch.object(deploy, "runtime_inventory", side_effect=AssertionError("must refuse before observing")), \
                self.assertRaisesRegex(deploy.DeployError, "swap"):
            deploy.runtime_recheck(self.release, self.paths)

    def test_no_release_acts_while_the_journal_is_open(self) -> None:
        self.stop_at_commit()
        with patch.object(deploy, "runtime_inventory", side_effect=AssertionError("must refuse before observing")), \
                self.assertRaises(deploy.DeployError):
            deploy.runtime_preflight(self.release, self.paths, self.docker)

    def test_crash_after_journal_before_fence_is_recovered_by_explicit_command(self) -> None:
        with patch.object(swap, "_swap_ensure_fence", side_effect=OSError("synthetic")), \
                self.assertRaises(OSError):
            self.apply()
        self.assertFalse(self.fence().exists())
        with self.assertRaises(deploy.DeployError):
            deploy.runtime_recheck(self.release, self.paths)
        self.assertEqual(self.call("abort")["status"], "aborted")
        self.assert_closed()

    def test_crash_between_archive_and_fence_removal_is_finished_by_resume(self) -> None:
        self.apply()
        target = deploy.parse_manifest(Path(json.loads(self.pin.read_text())["manifest"]).read_text(), image_prefix=PREFIX)
        self.seal(self.observe(target, None)["profile"])
        with patch.object(swap, "_swap_release_fence", side_effect=OSError("synthetic")), self.assertRaises(OSError):
            self.call("resume")
        self.assertFalse((self.state / "postgres-swap/attempt.json").exists())
        self.assertTrue(self.fence().exists())
        self.assertEqual(self.call("resume")["status"], "closed")
        self.assert_closed()

    def test_unknown_fence_is_never_removed(self) -> None:
        self.fence().parent.mkdir(mode=0o700)
        self.fence().write_text('{"schema":1}\n')
        self.fence().chmod(0o600)
        for action in ("resume", "abort"):
            with self.assertRaises(deploy.DeployError):
                self.call(action)
        self.assertTrue(self.fence().exists())

    def test_resume_and_abort_without_open_swap_refuse(self) -> None:
        for action in ("resume", "abort"):
            with self.assertRaises(deploy.DeployError):
                self.call(action)

    def test_resume_refuses_another_source_target_or_executor(self) -> None:
        self.stop_at_commit()
        other = deploy.parse_manifest(json.dumps(dict(self.raw, commit="d" * 40)), image_prefix=PREFIX)
        with self.assertRaises(deploy.DeployError):
            self.call("resume", release=other)
        with self.assertRaises(deploy.DeployError):
            self.call("resume", target=PREFIX + "postgres@sha256:" + "d" * 64)
        self.identity = dict(self.identity, sourceCommit="d" * 40)
        with self.assertRaises(deploy.DeployError):
            self.call("abort")
        self.assertEqual(self.phase(), "commit")

    def test_damaged_journal_refuses(self) -> None:
        self.stop_at_commit()
        path = self.state / "postgres-swap/attempt.json"
        raw = path.read_bytes()
        path.write_bytes(raw.replace(b'"schema":1', b'"schema":2', 1))
        for action in ("resume", "abort"):
            with self.assertRaises(deploy.DeployError):
                self.call(action)
        self.assertEqual(self.phase(), "commit")


class OrphanComposeTests(SwapFixture):
    def test_live_project_compose_process_blocks_resume_and_abort(self) -> None:
        self.stop_at_commit()
        for process in (["docker", "compose", "--file", str(self.compose_file), "up"],
                        ["/usr/libexec/docker/cli-plugins/docker-compose", "compose", "--file", str(self.compose_file), "up"],
                        ["docker", "compose", "-p", "duora", "up"], ["docker", "compose", "--project-name=duora", "stop"]):
            self.processes = [["sshd"], process]
            self.docker.commands.clear()
            for action in ("resume", "abort"):
                with self.subTest(process=process, action=action), self.assertRaisesRegex(deploy.DeployError, "compose"):
                    self.call(action)
            self.assertEqual(self.docker.mutating(), [])
        self.assertEqual(self.phase(), "commit")

    def test_other_projects_compose_does_not_block(self) -> None:
        self.stop_at_commit()
        self.processes = [["docker", "compose", "--file", "/opt/stillon/compose.yml", "up"], ["vim", str(self.compose_file)]]
        self.assertEqual(self.call("resume")["status"], "awaiting-reseal")

    def test_two_database_or_backup_containers_block_even_when_stopped(self) -> None:
        self.stop_at_commit()
        for role in ("postgres", "backup"):
            self.docker.counts = {"postgres": 1, "backup": 1, role: 2}
            self.docker.commands.clear()
            for action in ("resume", "abort"):
                with self.subTest(role=role, action=action), self.assertRaisesRegex(deploy.DeployError, "container"):
                    self.call(action)
            self.assertEqual(self.docker.mutating(), [])

    def test_unknown_container_inventory_refuses(self) -> None:
        self.stop_at_commit()
        self.docker.fail_on = {"docker ps --all"}
        with self.assertRaises(deploy.DeployError):
            self.call("resume")

    def test_unreadable_process_table_refuses(self) -> None:
        self.stop_at_commit()
        def unreadable() -> list:
            raise OSError("no /proc")
        host = dataclasses.replace(self.host(), list_processes=unreadable)
        with self.assertRaises(OSError):
            swap.postgres_swap_entry(self.release, NEW_REF, self.paths, self.docker, action="resume",
                                       reviewed_plan_sha256=None, host=host)

    def test_process_table_reader_parses_nul_separated_argv(self) -> None:
        root = self.root / "proc"
        (root / "10").mkdir(parents=True)
        (root / "10/cmdline").write_bytes(b"docker\0compose\0up\0")
        (root / "self").mkdir()
        (root / "11").mkdir()
        self.assertEqual([item.argv for item in swap.list_processes(root)], [["docker", "compose", "up"]])


class AbortTests(SwapFixture):
    def failing_at(self, failure: str) -> None:
        self.docker.fail_on = {failure}
        with self.assertRaises(deploy.DeployError):
            self.apply()
        self.docker.fail_on = set()

    def test_abort_before_anything_stopped_leaves_services_alone(self) -> None:
        reviewed = self.plan()
        self.docker.fail_on = {"docker create"}
        with self.assertRaises(deploy.DeployError):
            self.call("apply", reviewed=reviewed)
        self.docker.fail_on = set()
        self.docker.commands.clear()
        self.assertEqual(self.call("abort")["status"], "aborted")
        self.assertEqual(self.docker.mutating(), [])
        self.assert_closed()
        self.assertEqual(self.snapshot(), self.before)

    def test_abort_at_quiesce_reopens_application_without_reseal(self) -> None:
        self.failing_at("--stanza=duora check")
        self.assertEqual(self.phase(), "quiesce")
        self.assertFalse(self.docker.applications["web"])
        self.assertTrue(self.docker.containers["postgres"]["running"])
        self.assertEqual(self.call("abort")["status"], "aborted")
        self.assertTrue(all(self.docker.applications.values()))
        self.assert_closed()
        self.assertEqual(self.snapshot(), self.before)

    def test_abort_from_step_four_recreates_old_digest_and_waits_reseal_with_fence(self) -> None:
        self.failing_at("--no-deps postgres")
        self.assertEqual(self.phase(), "swap-runtime")
        self.docker.commands.clear()
        self.assertEqual(self.call("abort")["status"], "aborted-awaiting-reseal")
        self.assertEqual(self.phase(), "aborted-awaiting-reseal")
        self.assertTrue(self.fence().exists())
        self.assertEqual(self.docker.containers["postgres"]["image"], OLD_REF)
        self.assertEqual(self.docker.containers["backup"]["image"], OLD_REF)
        self.assertTrue(all(self.docker.applications.values()))
        self.assertEqual(self.snapshot(), self.before)
        self.assertFalse([c for c in self.docker.commands if " down" in c])
        with self.assertRaises(deploy.DeployError):
            self.call("resume")
        self.assertEqual(self.phase(), "aborted-awaiting-reseal")

    def test_aborted_swap_closes_only_after_reseal_against_source(self) -> None:
        self.failing_at("--no-deps postgres")
        self.call("abort")
        with self.assertRaises(deploy.DeployError):
            self.call("abort")
        self.seal(self.observe(self.release, None)["profile"])
        self.docker.commands.clear()
        self.assertEqual(self.call("resume")["status"], "closed")
        self.assert_closed()
        self.assertTrue(any(c.endswith("duora-backup verify") for c in self.docker.commands))
        self.assertTrue(any(c.endswith("duora-backup verify-repo") for c in self.docker.commands))
        self.assertEqual(self.snapshot(), self.before)

    def test_abort_after_target_is_running_stops_application_first(self) -> None:
        self.failing_at("wget")
        self.assertEqual(self.phase(), "reopen")
        self.docker.commands.clear()
        self.call("abort")
        commands = self.docker.commands
        worker = next(i for i, c in enumerate(commands) if "stop worker" in c)
        web = next(i for i, c in enumerate(commands) if "stop web" in c)
        database = next(i for i, c in enumerate(commands) if "stop postgres" in c)
        self.assertLess(worker, database)
        self.assertLess(web, database)

    def test_abort_detects_changed_photo_and_keeps_fence(self) -> None:
        self.failing_at("--no-deps postgres")
        self.docker.photo["systemIdentifier"] = "other"
        with self.assertRaisesRegex(deploy.DeployError, "foto"):
            self.call("abort")
        self.assertEqual(self.phase(), "aborting")
        self.assertTrue(self.fence().exists())
        self.docker.photo["systemIdentifier"] = "7000"
        self.assertEqual(self.call("abort")["status"], "aborted-awaiting-reseal")

    def test_resume_cannot_turn_an_interrupted_abort_into_forward(self) -> None:
        self.failing_at("--no-deps postgres")
        self.docker.fail_on = {"--no-deps web worker edge"}
        with self.assertRaises(deploy.DeployError):
            self.call("abort")
        self.docker.fail_on = set()
        self.assertEqual(self.phase(), "aborting")
        self.docker.commands.clear()
        with self.assertRaisesRegex(deploy.DeployError, "só continua com --abort"):
            self.call("resume")
        self.assertEqual(self.phase(), "aborting")
        self.assertEqual(self.docker.mutating(), [])
        self.assertEqual(self.call("abort")["status"], "aborted-awaiting-reseal")

    def test_abort_after_commit_is_refused(self) -> None:
        self.apply()
        with self.assertRaises(deploy.DeployError):
            self.call("abort")
        self.assertEqual(self.phase(), "awaiting-reseal")


class ResumeTests(SwapFixture):
    def test_each_interrupted_step_resumes_to_awaiting_reseal(self) -> None:
        for failure, phase in (("docker create", "journaled"), ("--stanza=duora check", "quiesce"),
                               ("--no-deps backup", "swap-runtime"), ("wget", "reopen")):
            with self.subTest(phase=phase):
                self.setUp()
                self.docker.fail_on = {failure}
                with self.assertRaises(deploy.DeployError):
                    self.apply()
                self.assertEqual(self.phase(), phase)
                self.docker.fail_on = set()
                self.assertEqual(self.call("resume")["status"], "awaiting-reseal")
                self.assertEqual(self.docker.containers["postgres"]["image"], NEW_REF)

    def test_external_repository_failure_at_step_five_pauses_for_a_decision(self) -> None:
        reviewed = self.plan()
        calls = []
        original = self.docker.__call__
        def failing(command, *, capture=False, input_text=None, timeout_seconds=None):
            if command[-1:] == ["verify"] and self.docker.containers["postgres"]["image"] == NEW_REF:
                calls.append(command)
                return subprocess.CompletedProcess(command, 1, "", "B2 unavailable")
            return original(command, capture=capture, input_text=input_text, timeout_seconds=timeout_seconds)
        with self.assertRaisesRegex(deploy.DeployError, "--resume ou --abort"):
            swap.postgres_swap_entry(self.release, NEW_REF, self.paths, failing, action="apply",
                                       reviewed_plan_sha256=reviewed, host=self.host())
        self.assertEqual(self.phase(), "verify-runtime")
        self.assertEqual(len(calls), 1)
        self.assertEqual(self.docker.containers["postgres"]["image"], NEW_REF)
        self.assertFalse(self.docker.applications["web"])
        self.assertEqual(self.call("resume")["status"], "awaiting-reseal")

    def test_changed_photo_after_swap_stops_before_reopening(self) -> None:
        reviewed = self.plan()
        original = self.docker._execute
        def changed(joined: str) -> str:
            if "'systemIdentifier'" in joined and self.docker.containers["postgres"]["image"] == NEW_REF:
                self.docker.photo = dict(self.docker.photo, timeline=2)
            return original(joined)
        with patch.object(self.docker, "_execute", changed), self.assertRaisesRegex(deploy.DeployError, "foto"):
            self.call("apply", reviewed=reviewed)
        self.assertEqual(self.phase(), "verify-runtime")
        self.assertFalse(self.docker.applications["web"])

    def test_wrong_volume_or_host_context_after_swap_stops(self) -> None:
        for mutation in ("volume", "compose"):
            with self.subTest(mutation=mutation):
                self.setUp()
                reviewed = self.plan()
                real = self.observe
                def drifted(release, env):
                    result = real(release, env)
                    if release.images["postgres"] == NEW_REF:
                        if mutation == "volume": result["profile"]["volumes"][0]["createdAt"] = "2026-11-05T00:00:00Z"
                        else: result["profile"]["context"]["composeSha256"] = "1" * 64
                    return result
                with patch.object(self, "observe", drifted), self.assertRaises(deploy.DeployError):
                    self.call("apply", reviewed=reviewed)
                self.assertEqual(self.phase(), "verify-runtime")
                self.assertFalse(self.docker.applications["web"])

    def test_application_not_ready_stays_before_commit(self) -> None:
        self.docker.fail_on = {"wget"}
        with self.assertRaises(deploy.DeployError):
            self.apply()
        self.assertEqual(self.snapshot(), self.before)


class ChildGroupTests(unittest.TestCase):
    def test_child_runs_in_its_own_session_and_returns_output(self) -> None:
        result = swap.run_in_own_group([sys.executable, "-c", "import os;print(os.getsid(0))"], capture=True)
        self.assertEqual(result.returncode, 0)
        self.assertNotEqual(int(result.stdout), os.getsid(0))

    def test_input_text_reaches_child(self) -> None:
        result = swap.run_in_own_group([sys.executable, "-c", "print(input())"], capture=True, input_text="payload\n")
        self.assertEqual(result.stdout.strip(), "payload")

    def test_timeout_kills_the_whole_group(self) -> None:
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        marker = Path(directory.name) / "grandchild"
        script = ("import os,subprocess,sys,time\n"
                  "p=subprocess.Popen([sys.executable,'-c','import time;time.sleep(60)'])\n"
                  f"open({str(marker)!r},'w').write(str(p.pid))\ntime.sleep(60)\n")
        with patch.object(swap, "COMMAND_TIMEOUT_SECONDS", 1), self.assertRaises(deploy.DeployError):
            swap.run_in_own_group([sys.executable, "-c", script])
        pid = int(marker.read_text())
        for _ in range(100):
            try:
                os.kill(pid, 0)
            except ProcessLookupError:
                return
            import time
            time.sleep(0.05)
        self.fail("grandchild survived the group kill")

    def test_parent_death_signal_is_requested_on_linux_and_orphans_exit(self) -> None:
        calls = []
        class Libc:
            def prctl(self, *arguments):
                calls.append(arguments)
                return 0
        with patch.object(swap.sys, "platform", "linux"), patch.object(swap.ctypes, "CDLL", return_value=Libc()), \
                patch.object(swap.os, "getppid", return_value=123), patch.object(deploy.os, "_exit") as leave:
            swap._die_with_parent(123)
            leave.assert_not_called()
            swap._die_with_parent(999)
            leave.assert_called_once()
        self.assertEqual(calls[0][:2], (1, signal.SIGKILL))


class CommandLineTests(unittest.TestCase):
    def invoke(self, *arguments: str) -> int:
        import contextlib
        import io
        with contextlib.redirect_stderr(io.StringIO()), contextlib.redirect_stdout(io.StringIO()):
            try:
                return deploy.main(["--manifest", "/nonexistent", *arguments])
            except SystemExit as error:
                return 100 + int(error.code or 0)

    def test_flag_combinations_refuse_before_any_effect(self) -> None:
        swap = ("--swap-postgres-component", "--target-postgres", NEW_REF)
        plan = "--reviewed-plan-sha256=" + "a" * 64
        for arguments in (("--resume",), ("--abort",), ("--target-postgres", NEW_REF), (plan,),
                          ("--swap-postgres-component",), swap, (*swap, "--resume", "--abort"),
                          (*swap, "--preflight-only", plan), (*swap, "--resume", plan), (*swap, "--abort", "--preflight-only"),
                          (*swap, "--reviewed-plan-sha256=short"), (*swap, plan, "--accept-missing-backup-check"),
                          (*swap, plan, "--inventory-only"), (*swap, plan, "--recover-current-v2")):
            with self.subTest(arguments=arguments):
                self.assertEqual(self.invoke(*arguments), 102)

    def test_delegated_or_unprivileged_invocation_is_refused(self) -> None:
        swap = ("--swap-postgres-component", "--target-postgres", NEW_REF, "--preflight-only")
        self.assertEqual(self.invoke(*swap), 1)
        with patch.object(deploy.os, "getuid", return_value=0), patch.object(deploy.os, "geteuid", return_value=0), \
                patch.dict(os.environ, {"SSH_ORIGINAL_COMMAND": "duora-deploy"}):
            self.assertEqual(self.invoke(*swap), 1)


class CrashAtCommitTests(SwapFixture):
    """A real SIGKILL at each boundary of step 7, followed by a fresh explicit command."""

    def killed_resume(self, *, cut: int, between_temp_and_rename: bool = False) -> None:
        journal = self.state / "postgres-swap"
        pid = os.fork()
        if pid == 0:
            try:
                written = 0
                original = swap._swap_write_atomic
                def write(path, raw, **arguments):
                    nonlocal written
                    if path.parent == journal and written < cut:
                        return original(path, raw, **arguments)
                    if written == cut and not between_temp_and_rename:
                        os.kill(os.getpid(), signal.SIGKILL)
                    if written == cut:
                        with patch.object(deploy.os, "replace", side_effect=lambda *_: os.kill(os.getpid(), signal.SIGKILL)):
                            return original(path, raw, **arguments)
                    written += 1
                    return original(path, raw, **arguments)
                swap._swap_write_atomic = write
                self.call("resume")
            finally:
                os._exit(91)
        _, status = os.waitpid(pid, 0)
        self.assertTrue(os.WIFSIGNALED(status), "child was not killed at the boundary")
        self.assertEqual(os.WTERMSIG(status), signal.SIGKILL)

    def test_every_boundary_resumes_to_the_exact_commit(self) -> None:
        for cut in range(COMMIT_ARTIFACTS + 1):
            for temp in (False, True):
                if cut == COMMIT_ARTIFACTS and temp:
                    continue
                with self.subTest(cut=cut, temp=temp):
                    self.setUp()
                    self.stop_at_commit()
                    self.killed_resume(cut=cut, between_temp_and_rename=temp)
                    self.assertEqual(self.phase(), "commit")
                    self.assertEqual(self.call("resume")["status"], "awaiting-reseal")
                    self.assertEqual(self.phase(), "awaiting-reseal")
                    actual = self.snapshot()
                    self.assertEqual(len(actual["opt"]), 2)
                    self.assertEqual(len(actual["releases"]), 4)
                    current = json.loads((self.state / "current.json").read_text())
                    self.assertEqual(json.loads(self.pin.read_text())["manifest"], str(self.opt / (current["releaseId"] + ".json")))
                    self.assertEqual(self.leftovers(), [])

    def leftovers(self) -> list[str]:
        directories = (self.state, self.state / "releases", self.opt, self.pin.parent, self.state / "postgres-swap")
        return [item.name for directory in directories for item in directory.iterdir() if item.name.endswith(".tmp")]

    def test_every_boundary_before_current_aborts_to_original_bytes(self) -> None:
        for cut in range(COMMIT_ARTIFACTS):
            for temp in (False, True):
                with self.subTest(cut=cut, temp=temp):
                    self.setUp()
                    self.stop_at_commit()
                    self.killed_resume(cut=cut, between_temp_and_rename=temp)
                    self.assertEqual(self.call("abort")["status"], "aborted-awaiting-reseal")
                    self.assertEqual(self.snapshot(), self.before)
                    self.assertEqual(self.docker.containers["postgres"]["image"], OLD_REF)
                    self.assertEqual(self.leftovers(), [])

    def test_after_current_rename_only_resume_is_possible(self) -> None:
        self.stop_at_commit()
        self.killed_resume(cut=COMMIT_ARTIFACTS)
        self.assertNotEqual((self.state / "current.json").read_bytes(), self.before[str(self.state / "current.json")])
        with self.assertRaisesRegex(deploy.DeployError, "--resume"):
            self.call("abort")
        self.assertEqual(self.phase(), "commit")
        self.assertEqual(self.call("resume")["status"], "awaiting-reseal")

    def test_killed_abort_can_be_repeated(self) -> None:
        self.stop_at_commit()
        self.killed_resume(cut=4)
        pid = os.fork()
        if pid == 0:
            try:
                original = swap._swap_write_atomic
                def write(path, raw, **arguments):
                    if path == self.pin:
                        os.kill(os.getpid(), signal.SIGKILL)
                    return original(path, raw, **arguments)
                swap._swap_write_atomic = write
                self.call("abort")
            finally:
                os._exit(91)
        _, status = os.waitpid(pid, 0)
        self.assertEqual(os.WTERMSIG(status), signal.SIGKILL)
        self.assertEqual(self.phase(), "aborting")
        self.assertEqual(self.call("abort")["status"], "aborted-awaiting-reseal")
        self.assertEqual(self.snapshot(), self.before)

    def test_foreign_file_at_any_boundary_refuses_both_commands(self) -> None:
        self.stop_at_commit()
        self.killed_resume(cut=3)
        foreign = self.state / "previous.json"
        foreign.write_text('{"releaseId":"foreign"}\n')
        for action in ("resume", "abort"):
            with self.assertRaisesRegex(deploy.DeployError, "previous"):
                self.call(action)
        self.assertEqual(foreign.read_text(), '{"releaseId":"foreign"}\n')

    def test_foreign_pin_change_is_not_overwritten(self) -> None:
        self.stop_at_commit()
        self.killed_resume(cut=4)
        pin = json.loads(self.pin.read_text())
        pin["application_uuid"] = "foreign"
        self.write(self.pin, json.dumps(pin, indent=2) + "\n")
        before = self.pin.read_bytes()
        for action in ("resume", "abort"):
            with self.assertRaisesRegex(deploy.DeployError, "pin"):
                self.call(action)
        self.assertEqual(self.pin.read_bytes(), before)

    def test_writer_fsyncs_file_and_directory_before_acknowledging(self) -> None:
        path = self.root / "durable.json"
        synced = []
        real = os.fsync
        def fsync(descriptor):
            synced.append(os.path.isdir(f"/dev/fd/{descriptor}") or os.fstat(descriptor).st_mode & 0o170000 == 0o040000)
            return real(descriptor)
        with patch.object(deploy.os, "fsync", fsync):
            swap._swap_write_atomic(path, b"{}\n", mode=0o600)
        self.assertEqual(synced, [False, True])
        self.assertEqual(path.stat().st_mode & 0o777, 0o600)


class RepositoryVerifyLockTests(SwapFixture):
    """Real flock and a real child that runs duora_backup's own locking; nothing here is mocked."""

    def setUp(self) -> None:
        super().setUp()
        lock = self.state / ".deploy.lock"
        lock.touch(mode=0o600)
        self.descriptor = os.open(lock, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
        self.addCleanup(os.close, self.descriptor)
        fcntl.flock(self.descriptor, fcntl.LOCK_EX | fcntl.LOCK_NB)
        self.lease = deploy.RecoveryAdministrativeLease(self.descriptor)
        script = self.root / "duora-backup"
        script.write_text("#!" + sys.executable + "\n" + textwrap.dedent(f"""
            import subprocess, sys
            from pathlib import Path
            sys.path.insert(0, {str(Path(__file__).resolve().parent)!r})
            import duora_backup as backup
            if sys.argv[1] == "verify": raise SystemExit(0)
            def run(command, **arguments):
                return subprocess.CompletedProcess(command, 0, "", "")
            backup._find_backup = lambda *_: "container"
            backup._verify_repository = lambda *_, **__: None
            backup.run_action(sys.argv[1], run, project="duora", state_dir=Path({str(self.state)!r}), egress_env=None,
                              lock_wait_seconds=2)
            """))
        script.chmod(0o755)
        self.paths = dataclasses.replace(self.paths, backup_command=script)

    def runner(self, command, *, capture=False, input_text=None, timeout_seconds=None):
        if command[0] == str(self.paths.backup_command):
            return swap.run_in_own_group(command, capture=capture, input_text=input_text, timeout_seconds=timeout_seconds)
        return self.docker(command, capture=capture, input_text=input_text, timeout_seconds=timeout_seconds)

    def entry(self, action: str, reviewed: str | None = None) -> dict:
        return swap.postgres_swap_entry(self.release, NEW_REF, self.paths, self.runner, action=action,
                                        reviewed_plan_sha256=reviewed, host=self.host(), lease=self.lease)

    def locked(self) -> bool:
        other = os.open(self.state / ".deploy.lock", os.O_RDONLY)
        try:
            fcntl.flock(other, fcntl.LOCK_EX | fcntl.LOCK_NB)
            return False
        except BlockingIOError:
            return True
        finally:
            os.close(other)

    def test_verify_repo_child_passes_and_only_the_deploy_lock_was_released(self) -> None:
        self.assertEqual(self.entry("plan")["status"], "plan")
        self.assertTrue(self.lease.held)
        self.assertTrue(self.locked())

    def test_closing_a_swap_verifies_the_repository_under_the_same_rule(self) -> None:
        plan = self.entry("plan")["planSha256"]
        self.entry("apply", plan)
        target = deploy.parse_manifest(Path(json.loads(self.pin.read_text())["manifest"]).read_text(), image_prefix=PREFIX)
        self.seal(self.observe(target, None)["profile"])
        self.assertEqual(self.entry("resume")["status"], "closed")
        self.assertTrue(self.locked())

    def test_failed_verify_repo_takes_the_lock_back(self) -> None:
        self.paths.backup_command.write_text("#!/bin/sh\n[ \"$1\" = verify ]\n")
        with self.assertRaises(deploy.DeployError):
            self.entry("plan")
        self.assertTrue(self.lease.held)
        self.assertTrue(self.locked())

    def stolen_lock(self, script: str) -> None:
        """A real competitor takes .deploy.lock inside the released window and keeps it past the child's exit."""
        self.paths.backup_command.write_text("#!" + sys.executable + "\n" + textwrap.dedent(f"""
            import fcntl, os, sys
            if sys.argv[1] == "verify": raise SystemExit(0)
            holder = os.fork()
            if holder == 0:
                os.setsid()
                lock = os.open({str(self.state / ".deploy.lock")!r}, os.O_RDONLY)
                fcntl.flock(lock, fcntl.LOCK_EX)
                open({str(self.root / "holding")!r}, "w").close()
                os.close(1); os.close(2)
                import time; time.sleep(30)
                os._exit(0)
            while not os.path.exists({str(self.root / "holding")!r}): pass
            {script}
            """))
        self.addCleanup(self.release_competitor)

    def release_competitor(self) -> None:
        subprocess.run(["pkill", "-f", str(self.paths.backup_command)], check=False)

    def test_failed_verify_repo_keeps_its_own_error_when_the_lock_is_stolen(self) -> None:
        self.stolen_lock('print("lacuna de WAL", file=sys.stderr); raise SystemExit(1)')
        self.docker.commands.clear()
        with self.assertRaises(deploy.DeployError) as refused:
            self.entry("plan")
        message = str(refused.exception)
        self.assertIn("duora-backup verify-repo falhou", message)
        self.assertNotIn("do not repeat", message)
        self.assertIn(".deploy.lock", message)

    def test_passed_verify_repo_with_stolen_lock_has_its_own_message(self) -> None:
        self.stolen_lock("raise SystemExit(0)")
        with self.assertRaises(deploy.DeployError) as refused:
            self.entry("plan")
        self.assertIn(".deploy.lock", str(refused.exception))
        self.assertNotIn("do not repeat", str(refused.exception))

    def test_verify_repo_has_its_own_ceiling_beyond_the_command_one(self) -> None:
        self.call("plan")
        self.assertGreater(swap.SWAP_VERIFY_REPO_TIMEOUT_SECONDS, deploy.COMMAND_TIMEOUT_SECONDS)
        self.assertEqual(self.docker.timeouts["verify-repo"], swap.SWAP_VERIFY_REPO_TIMEOUT_SECONDS)
        self.assertIsNone(self.docker.timeouts["verify"])

    def test_group_runner_honours_the_explicit_ceiling(self) -> None:
        with patch.object(swap, "COMMAND_TIMEOUT_SECONDS", 0.01):
            result = swap.run_in_own_group([sys.executable, "-c", "import time;time.sleep(0.3)"], timeout_seconds=30)
        self.assertEqual(result.returncode, 0)


class RestartAfterEarlyPhaseTests(SwapFixture):
    def stopped_at(self, phase: str) -> None:
        steps = dict(swap._SWAP_STEPS)
        def stop(_context):
            raise deploy.DeployError("synthetic stop")
        steps[phase] = stop
        with patch.object(swap, "_SWAP_STEPS", steps), self.assertRaises(deploy.DeployError):
            self.apply()
        self.assertEqual(self.phase(), phase)

    def test_abort_after_restart_waits_reseal_instead_of_dead_ending(self) -> None:
        for phase in ("journaled", "verify-backup", "quiesce"):
            with self.subTest(phase=phase):
                self.setUp()
                self.stopped_at(phase)
                self.docker.restart()
                self.assertEqual(self.call("abort")["status"], "aborted-awaiting-reseal")
                self.assertEqual(self.phase(), "aborted-awaiting-reseal")
                self.assertTrue(self.fence().exists())
                self.seal(self.observe(self.release, None)["profile"])
                self.assertEqual(self.call("resume")["status"], "closed")
                self.assert_closed()

    def test_installer_can_reseal_the_aborted_phase_a_restart_leaves(self) -> None:
        version = self.root / "versions" / COMMIT
        version.mkdir(parents=True)
        (version / "duora_deploy.py").write_text("# installed\n")
        entry = self.root / "duora-deploy"
        entry.symlink_to(version / "duora_deploy.py")
        self.identity["executorSha256"] = hashlib.sha256(b"# installed\n").hexdigest()
        paths = installer.InstallPaths(entry, self.root / "versions", self.root / "preimages", self.state, self.ingress)
        self.stopped_at("quiesce")
        self.docker.restart()
        self.call("abort")
        with patch.object(installer, "validate_bundle"):
            exempt = installer._resealing_swap_fence(paths, expected_commit=COMMIT, manifest_sha256="e" * 64)
        self.assertEqual(exempt, self.fence().parent)

    def test_abort_without_restart_still_closes_alone(self) -> None:
        self.stopped_at("verify-backup")
        self.assertEqual(self.call("abort")["status"], "aborted")

    def test_abort_never_recreates_the_database_for_an_early_restart(self) -> None:
        self.stopped_at("verify-backup")
        self.docker.restart()
        self.docker.commands.clear()
        self.call("abort")
        self.assertFalse([c for c in self.docker.commands if "stop postgres" in c or "--no-deps postgres" in c])

    def test_unhealthy_runtime_is_not_mistaken_for_a_restart(self) -> None:
        self.stopped_at("verify-backup")
        self.docker.containers["postgres"]["running"] = False
        with self.assertRaises(deploy.DeployError):
            self.call("abort")
        self.assertEqual(self.phase(), "aborting")


class RestartAfterCommitTests(CrashAtCommitTests):
    def test_restart_after_current_rename_reaches_reseal(self) -> None:
        self.stop_at_commit()
        self.killed_resume(cut=COMMIT_ARTIFACTS)
        self.docker.restart()
        self.assertEqual(self.call("resume")["status"], "awaiting-reseal")
        target = deploy.parse_manifest(Path(json.loads(self.pin.read_text())["manifest"]).read_text(), image_prefix=PREFIX)
        self.seal(self.observe(target, None)["profile"])
        self.assertEqual(self.call("resume")["status"], "closed")

    def test_restart_before_current_rename_still_refuses_to_publish(self) -> None:
        self.stop_at_commit()
        self.killed_resume(cut=3)
        self.docker.restart()
        with self.assertRaisesRegex(deploy.DeployError, "nada será publicado"):
            self.call("resume")
        self.assertEqual((self.state / "current.json").read_bytes(), self.before[str(self.state / "current.json")])


class ResumeGateTests(SwapFixture):
    stopped_at = RestartAfterEarlyPhaseTests.stopped_at

    def test_resume_before_stopping_the_database_requires_window_and_space(self) -> None:
        for phase in ("journaled", "verify-backup", "quiesce"):
            with self.subTest(phase=phase, gate="window"):
                self.setUp()
                self.stopped_at(phase)
                self.now = datetime(2026, 11, 11, 3, 0, tzinfo=timezone.utc)
                self.docker.commands.clear()
                with self.assertRaisesRegex(deploy.DeployError, "janela"):
                    self.call("resume")
                self.assertEqual(self.docker.mutating(), [])
            with self.subTest(phase=phase, gate="space"):
                self.setUp()
                self.stopped_at(phase)
                self.preflight_error = deploy.DeployError("espaço em disco insuficiente")
                with self.assertRaisesRegex(deploy.DeployError, "espaço"):
                    self.call("resume")

    def test_gate_at_quiesce_says_the_application_is_stopped_and_points_to_abort(self) -> None:
        self.stopped_at("quiesce")
        self.now = datetime(2026, 11, 11, 3, 0, tzinfo=timezone.utc)
        with self.assertRaises(deploy.DeployError) as refused:
            self.call("resume")
        self.assertIn("--abort", str(refused.exception))
        self.assertNotIn("ada foi parado", str(refused.exception))
        self.now = WINDOW
        self.preflight_error = deploy.DeployError("área de fotos sem o marcador. Nada foi parado.")
        with self.assertRaises(deploy.DeployError) as refused:
            self.call("resume")
        self.assertIn("--abort", str(refused.exception))
        self.assertNotIn("ada foi parado", str(refused.exception))

    def test_gate_before_quiesce_keeps_saying_nothing_was_stopped(self) -> None:
        self.stopped_at("verify-backup")
        self.now = datetime(2026, 11, 11, 3, 0, tzinfo=timezone.utc)
        with self.assertRaisesRegex(deploy.DeployError, "nada foi parado"):
            self.call("resume")

    def test_resume_after_stopping_the_database_ignores_window_and_space(self) -> None:
        self.docker.fail_on = {"--no-deps postgres"}
        with self.assertRaises(deploy.DeployError):
            self.apply()
        self.docker.fail_on = set()
        self.now = datetime(2026, 11, 11, 3, 0, tzinfo=timezone.utc)
        self.preflight_error = deploy.DeployError("espaço em disco insuficiente")
        self.assertEqual(self.call("resume")["status"], "awaiting-reseal")

    def test_abort_never_requires_window_or_space(self) -> None:
        self.stopped_at("verify-backup")
        self.now = datetime(2026, 11, 11, 3, 0, tzinfo=timezone.utc)
        self.preflight_error = deploy.DeployError("espaço em disco insuficiente")
        self.assertEqual(self.call("abort")["status"], "aborted")

    def test_close_refuses_while_an_installation_is_pending(self) -> None:
        self.apply()
        target = deploy.parse_manifest(Path(json.loads(self.pin.read_text())["manifest"]).read_text(), image_prefix=PREFIX)
        self.seal(self.observe(target, None)["profile"])
        pending = self.state / "executor-install-pending.json"
        pending.write_text("{}")
        with self.assertRaisesRegex(deploy.DeployError, "[Ii]nstalação"):
            self.call("resume")
        self.assertEqual(self.phase(), "awaiting-reseal")
        pending.unlink()
        self.assertEqual(self.call("resume")["status"], "closed")


class OrphanDetectorTests(unittest.TestCase):
    paths = deploy.Paths(Path("/opt/duora/compose.coolify.json"), Path("/etc/duora/duora.env"), Path("/var/lib/duora"))

    def detected(self, argv, cwd="/root", environ=None) -> bool:
        return swap._swap_is_project_compose(swap.ProcessInfo(argv, cwd, environ or {}), self.paths)

    def test_project_clients_that_name_nothing_on_the_command_line(self) -> None:
        self.assertTrue(self.detected(["docker", "compose", "up", "-d"], cwd="/opt/duora"))
        self.assertTrue(self.detected(["docker", "compose", "-f", "compose.coolify.json", "up"], cwd="/opt/duora"))
        self.assertTrue(self.detected(["docker", "compose", "-pduora", "up"]))
        self.assertTrue(self.detected(["docker", "compose", "up"], environ={"COMPOSE_PROJECT_NAME": "duora"}))
        self.assertTrue(self.detected(["docker", "compose", "up"], environ={"COMPOSE_FILE": "/opt/duora/compose.coolify.json"}))

    def test_other_projects_and_non_compose_processes_pass(self) -> None:
        self.assertFalse(self.detected(["docker", "compose", "up"], cwd="/opt/stillon"))
        self.assertFalse(self.detected(["docker", "compose", "up"], environ={"COMPOSE_PROJECT_NAME": "stillon"}))
        self.assertFalse(self.detected(["vim", "/opt/duora/compose.coolify.json"], cwd="/opt/duora"))

    def test_reader_returns_cwd_and_environment(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            entry = Path(directory) / "12"
            entry.mkdir()
            (entry / "cmdline").write_bytes(b"docker\0compose\0up\0")
            (entry / "environ").write_bytes(b"COMPOSE_PROJECT_NAME=duora\0OTHER\0")
            (entry / "cwd").symlink_to("/opt/duora")
            process = swap.list_processes(Path(directory))[0]
        self.assertEqual((process.cwd, process.environ), ("/opt/duora", {"COMPOSE_PROJECT_NAME": "duora"}))


class StrengthenedProtocolTests(SwapFixture):
    def sealed_target(self) -> None:
        self.apply()
        target = deploy.parse_manifest(Path(json.loads(self.pin.read_text())["manifest"]).read_text(), image_prefix=PREFIX)
        self.seal(self.observe(target, None)["profile"])

    def test_every_recreation_waits_for_health(self) -> None:
        self.apply()
        ups = [c for c in self.docker.commands if " up " in c]
        self.assertGreaterEqual(len(ups), 3)
        self.assertTrue(all("up --detach --wait --no-deps" in c for c in ups))

    def test_hold_removal_also_removes_anonymous_volumes(self) -> None:
        self.sealed_target()
        self.call("resume")
        removals = [c for c in self.docker.commands if c.startswith("docker rm")]
        self.assertEqual(len(removals), 2)
        self.assertTrue(all(c.startswith("docker rm --force --volumes ") for c in removals))

    def close_with_restart_during(self, action: str, status_phase: str) -> None:
        real = self.docker
        def run(command, *, capture=False, input_text=None, timeout_seconds=None):
            result = real(command, capture=capture, input_text=input_text, timeout_seconds=timeout_seconds)
            if command[-1] == action:
                real.restart()
            return result
        with self.assertRaisesRegex(deploy.DeployError, "ressela"):
            swap.postgres_swap_entry(self.release, NEW_REF, self.paths, run, action="resume",
                                     reviewed_plan_sha256=None, host=self.host())
        self.assertEqual(self.phase(), status_phase)
        self.assertTrue(self.fence().exists())

    def test_restart_during_verify_repo_does_not_close_with_a_divergent_baseline(self) -> None:
        self.sealed_target()
        self.close_with_restart_during("verify-repo", "awaiting-reseal")

    def test_restart_during_verify_repo_of_an_aborted_swap_keeps_it_open(self) -> None:
        self.docker.fail_on = {"--no-deps postgres"}
        with self.assertRaises(deploy.DeployError):
            self.apply()
        self.docker.fail_on = set()
        self.call("abort")
        self.seal(self.observe(self.release, None)["profile"])
        self.close_with_restart_during("verify-repo", "aborted-awaiting-reseal")

    def test_close_waits_for_real_readiness(self) -> None:
        self.sealed_target()
        self.docker.fail_on = {"wget"}
        with self.assertRaises(deploy.DeployError):
            self.call("resume")
        self.assertEqual(self.phase(), "awaiting-reseal")

    def test_reopen_refuses_a_database_recreated_during_the_smoke(self) -> None:
        real = swap._swap_open_application
        def recreated(*arguments):
            real(*arguments)
            self.docker.containers["postgres"]["generation"] += 1
        with patch.object(swap, "_swap_open_application", recreated), \
                self.assertRaisesRegex(deploy.DeployError, "nada será publicado"):
            self.apply()
        self.assertEqual(self.phase(), "reopen")

    def test_commit_refuses_a_database_recreated_after_reopen(self) -> None:
        real = swap._swap_step_reopen
        def recreated(context):
            real(context)
            self.docker.containers["postgres"]["generation"] += 1
        steps = dict(swap._SWAP_STEPS, reopen=recreated)
        with patch.object(swap, "_SWAP_STEPS", steps), self.assertRaisesRegex(deploy.DeployError, "nada será publicado"):
            self.apply()
        self.assertEqual(self.phase(), "commit")
        self.assertEqual(self.snapshot(), self.before)

    def test_plan_verifies_the_repository_before_the_checks_that_must_stay_true(self) -> None:
        order: list[str] = []
        real_run, real_inspect = self.docker, self.observe
        def run(command, *, capture=False, input_text=None, timeout_seconds=None):
            if command[-1] == "verify-repo":
                order.append("verify-repo")
            return real_run(command, capture=capture, input_text=input_text, timeout_seconds=timeout_seconds)
        def inspect(release, environment):
            order.append("preserved")
            return real_inspect(release, environment)
        host = dataclasses.replace(self.host(), inspect_runtime=inspect,
                                   require_ingress_idle=lambda _uuid: order.append("idle"))
        swap.postgres_swap_entry(self.release, NEW_REF, self.paths, run, action="plan", reviewed_plan_sha256=None, host=host)
        self.assertEqual(order, ["verify-repo", "idle", "preserved"])

    def test_live_migrator_stops_the_swap_before_the_database(self) -> None:
        self.docker.counts["migrator"] = 1
        with self.assertRaises(deploy.DeployError):
            self.apply()
        self.assertEqual(self.phase(), "quiesce")
        self.assertFalse([c for c in self.docker.commands if "stop postgres" in c])

    def test_commit_out_of_order_is_refused(self) -> None:
        self.stop_at_commit()
        attempt = json.loads((self.state / "postgres-swap/attempt.json").read_text())
        artifact = next(item for item in attempt["commit"]["artifacts"] if item["name"] == "release-environment")
        target = deploy.parse_manifest(json.dumps(attempt["target"]), image_prefix=PREFIX)
        Path(artifact["path"]).write_text(deploy._release_env_text(target))
        with self.assertRaisesRegex(deploy.DeployError, "fora de ordem"):
            self.call("resume")

    def test_commit_preserves_each_file_mode(self) -> None:
        self.pin.chmod(0o640)
        (self.state / "current.json").chmod(0o644)
        self.apply()
        self.assertEqual(self.pin.stat().st_mode & 0o777, 0o640)
        self.assertEqual((self.state / "current.json").stat().st_mode & 0o777, 0o644)
        self.assertEqual((self.state / "previous.json").stat().st_mode & 0o777, 0o644)

    def test_every_directory_change_of_undo_and_close_is_synced(self) -> None:
        self.stop_at_commit()
        CrashAtCommitTests.killed_resume(self, cut=3)  # type: ignore[arg-type]
        synced: list[Path] = []
        real = swap._swap_sync_directory
        def sync(path):
            synced.append(path)
            real(path)
        with patch.object(swap, "_swap_sync_directory", sync):
            self.call("abort")
        self.assertIn(self.state / "releases", synced)
        self.assertIn(self.opt, synced)
        self.seal(self.observe(self.release, None)["profile"])
        synced.clear()
        with patch.object(swap, "_swap_sync_directory", sync):
            self.call("resume")
        self.assertIn(self.state / "postgres-swap-history", synced)
        self.assertIn(self.state, synced)
        self.assertIn(self.fence().parent, synced)

    def test_orphan_fence_needs_the_exact_archived_hash(self) -> None:
        self.sealed_target()
        with patch.object(swap, "_swap_release_fence", side_effect=OSError("synthetic")), self.assertRaises(OSError):
            self.call("resume")
        history = next((self.state / "postgres-swap-history").iterdir())
        (history / "attempt.json").write_text("{}\n")
        with self.assertRaisesRegex(deploy.DeployError, "Fence desconhecida"):
            self.call("resume")
        self.assertTrue(self.fence().exists())

    def test_same_prefix_other_repository_is_refused_by_the_repository_rule(self) -> None:
        with self.assertRaisesRegex(deploy.DeployError, "mesmo repositório"):
            self.call("plan", target=PREFIX + "web@" + NEW_DIGEST)


class ProductionWiringTests(unittest.TestCase):
    def setUp(self) -> None:
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.state = Path(directory.name)
        self.paths = deploy.Paths(self.state / "compose.yml", self.state / "duora.env", self.state)

    def test_executor_identity_requires_the_installed_receipt_of_this_exact_file(self) -> None:
        own = hashlib.sha256(Path(deploy.__file__).resolve().read_bytes()).hexdigest()
        sidecar = hashlib.sha256(Path(str(swap.__file__)).resolve().read_bytes()).hexdigest()
        receipt = {"status": "installed", "sourceCommit": COMMIT, "manifestSha256": "e" * 64, "executorSha256": own,
                   "files": {"postgres_swap.py": {"sha256": sidecar, "mode": 384}}, "extra": 1}
        def read(value):
            return patch.object(deploy, "_recovery_root_read", return_value=json.dumps(value).encode())
        with read(receipt):
            self.assertEqual(deploy._swap_executor_identity(self.paths),
                             {"sourceCommit": COMMIT, "manifestSha256": "e" * 64, "executorSha256": own})
        for changed in (dict(receipt, status="preflight"), dict(receipt, executorSha256="0" * 64),
                        dict(receipt, files={"postgres_swap.py": {"sha256": "0" * 64, "mode": 384}}), dict(receipt, files={})):
            with read(changed), self.assertRaises(deploy.DeployError):
                deploy._swap_executor_identity(self.paths)

    def test_ingress_gate_is_the_host_scan_and_the_coolify_queue(self) -> None:
        calls = []
        class Receiver:
            def require_host_idle(self): calls.append("host")
            def require_idle(self, uuid): calls.append(uuid)
        with patch.object(deploy, "_recovery_verified_receiver", return_value=Receiver()):
            deploy._swap_require_ingress_idle("app-1")
        self.assertEqual(calls, ["host", "app-1"])

    def main(self, *arguments: str) -> tuple[int, str]:
        stderr = io.StringIO()
        with contextlib.redirect_stderr(stderr), contextlib.redirect_stdout(io.StringIO()):
            code = deploy.main(["--manifest", "/nonexistent", "--swap-postgres-component", "--target-postgres", NEW_REF, *arguments])
        return code, stderr.getvalue()

    def test_only_direct_root_reaches_the_locks(self) -> None:
        reached = []
        @contextlib.contextmanager
        def locks(_paths):
            reached.append(True)
            raise deploy.DeployError("reached the locks")
            yield
        pin = patch.object(deploy, "_recovery_root_read", return_value=b'{"compose":"/x"}')
        cases = [(1000, 1000, {}, False), (0, 0, {"SUDO_UID": "1000"}, False), (0, 0, {"SSH_ORIGINAL_COMMAND": "x"}, False),
                 (0, 1000, {}, False), (0, 0, {}, True)]
        for uid, euid, environment, allowed in cases:
            with self.subTest(uid=uid, euid=euid, environment=environment):
                reached.clear()
                clean = {k: v for k, v in os.environ.items() if k not in ("SUDO_UID", "SSH_ORIGINAL_COMMAND")}
                with patch.object(deploy.os, "getuid", return_value=uid), patch.object(deploy.os, "geteuid", return_value=euid), \
                        patch.dict(os.environ, {**clean, **environment}, clear=True), pin, \
                        patch.object(deploy, "recovery_administrative_locks", locks):
                    code, stderr = self.main("--preflight-only")
                self.assertEqual(code, 1)
                self.assertEqual(bool(reached), allowed)
                if not allowed:
                    self.assertIn("Direct administrative root", stderr)

    def test_main_runs_the_swap_in_own_group_with_the_administrative_lease(self) -> None:
        manifest = self.state / "manifest.json"
        manifest.write_text(json.dumps(source_manifest()))
        self.paths.host_env.write_text("DUORA_IMAGE_PREFIX=" + PREFIX + "\n")
        captured = {}
        @contextlib.contextmanager
        def locks(_paths):
            yield "the-lease"
        def entry(release, reference, paths, run, **arguments):
            captured.update(run=run, reference=reference, **arguments)
            return {"status": "plan"}
        with patch.object(deploy, "_require_recovery_administrator"), patch.object(deploy, "recovery_administrative_locks", locks), \
                patch.object(swap, "postgres_swap_entry", entry), contextlib.redirect_stdout(io.StringIO()):
            code = deploy.main(["--manifest", str(manifest), "--compose-file", str(self.paths.compose_file),
                                "--host-env", str(self.paths.host_env), "--state-dir", str(self.state),
                                "--swap-postgres-component", "--target-postgres", NEW_REF, "--resume"])
        self.assertEqual(code, 0)
        self.assertIs(captured["run"], swap.run_in_own_group)
        with patch.object(deploy, "preflight") as check:
            captured["host"].preflight()
        check.assert_called_once()
        self.assertEqual(captured["lease"], "the-lease")
        self.assertEqual(captured["action"], "resume")


if __name__ == "__main__":
    unittest.main()
