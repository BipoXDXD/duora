"""Partida do daemon rootless e do launcher depois de reboot (#174): só com opção explícita, nunca por padrão.

PyYAML já faz parte das ferramentas Ansible do job ops. Rodar da raiz: python3 -m unittest discover -s scripts/deploy
"""

import unittest
from pathlib import Path

import yaml

ROOT = Path(__file__).resolve().parents[2]
ROLE = ROOT / "infra/ansible/roles/media_sanitizer"
SWITCH = "media_sanitizer_start_on_boot | bool"
LAUNCHER_LINK = "/etc/systemd/system/multi-user.target.wants/duora-media-launcher.service"
LAUNCHER_DROP_IN = "/etc/systemd/system/duora-media-launcher.service.d/50-boot.conf"
DAEMON_LINK = "{{ media_sanitizer_home }}/.config/systemd/user/default.target.wants/docker.service"


def _tasks(name: str) -> list[dict]:
    return yaml.safe_load((ROLE / "tasks" / name).read_text(encoding="utf-8"))


def _conditions(task: dict) -> list[str]:
    when = task.get("when", [])
    return [when] if isinstance(when, str) else list(when)


def _module(task: dict) -> dict:
    for key in ("ansible.builtin.file", "ansible.builtin.template", "ansible.builtin.assert"):
        if key in task:
            return task[key]
    return {}


def _touching(tasks: list[dict], path: str) -> list[dict]:
    found = []
    for task in tasks:
        module = _module(task)
        targets = [module.get("path"), module.get("dest"), *(task.get("loop") or [])]
        if path in targets:
            found.append(task)
    return found


class BootSwitchTests(unittest.TestCase):
    def setUp(self) -> None:
        self.install = _tasks("install.yml")

    def test_default_keeps_everything_off_after_reboot(self) -> None:
        defaults = yaml.safe_load((ROLE / "defaults/main.yml").read_text(encoding="utf-8"))

        self.assertIs(False, defaults["media_sanitizer_start_on_boot"])

    def test_every_boot_link_is_created_only_behind_the_switch_and_removed_without_it(self) -> None:
        for path in (LAUNCHER_LINK, LAUNCHER_DROP_IN, DAEMON_LINK):
            with self.subTest(path=path):
                tasks = _touching(self.install, path)
                creators = [t for t in tasks if _module(t).get("state") != "absent"]
                removers = [t for t in tasks if _module(t).get("state") == "absent"]
                self.assertEqual(1, len(creators))
                self.assertEqual(1, len(removers))
                self.assertIn(SWITCH, _conditions(creators[0]))
                self.assertIn(f"not ({SWITCH})", _conditions(removers[0]))

    def test_boot_changes_inside_the_account_home_never_run_as_root(self) -> None:
        """Root seguiria um symlink plantado pela conta em .config; só a própria conta mexe no seu home."""
        boot = [t for t in self.install if SWITCH in " ".join(_conditions(t))]
        touching_home = [t for t in boot if "media_sanitizer_home" in yaml.safe_dump(t)]

        self.assertEqual(3, len(touching_home))
        for task in touching_home:
            with self.subTest(task=task["name"]):
                self.assertIs(True, task.get("become"))
                self.assertEqual("{{ media_sanitizer_user }}", task.get("become_user"))
                self.assertNotIn("/etc/", yaml.safe_dump(task))

    def test_switch_requires_a_pinned_image_and_the_started_runtime(self) -> None:
        guards = [t for t in self.install if "ansible.builtin.assert" in t and SWITCH in _conditions(t)]

        self.assertEqual(1, len(guards))
        that = guards[0]["ansible.builtin.assert"]["that"]
        self.assertIn("media_sanitizer_image | length > 0", that)
        self.assertIn("media_sanitizer_runtime_started | bool", that)
        self.assertLess(self.install.index(guards[0]), 3, "a recusa vem antes de qualquer mudança no host")

    def test_inactive_launcher_is_still_required_without_the_switch(self) -> None:
        checks = [t for t in self.install if "ansible.builtin.assert" in t
                  and "ActiveState == 'inactive'" in " ".join(t["ansible.builtin.assert"]["that"])]

        self.assertEqual(1, len(checks))
        self.assertEqual([f"not ({SWITCH})"], _conditions(checks[0]))

    def test_launcher_template_still_cannot_enable_itself(self) -> None:
        unit = (ROOT / "infra/media/systemd/duora-media-launcher.service").read_text(encoding="utf-8")

        self.assertNotIn("[Install]", unit)
        self.assertNotIn("WantedBy", unit)

    def test_boot_drop_in_orders_the_launcher_after_the_account_manager(self) -> None:
        drop_in = (ROLE / "templates/launcher-boot.conf.j2").read_text(encoding="utf-8")

        self.assertIn("After=user@{{ media_sanitizer_uid }}.service\n", drop_in)
        self.assertIn("Wants=user@{{ media_sanitizer_uid }}.service\n", drop_in)
        self.assertNotIn("[Install]", drop_in)


class UninstallTests(unittest.TestCase):
    def test_uninstall_removes_the_launcher_boot_link_and_drop_in(self) -> None:
        removal = [t for t in _tasks("uninstall.yml") if _module(t).get("state") == "absent" and t.get("loop")]
        paths = {item for task in removal for item in task["loop"]}

        self.assertIn(LAUNCHER_LINK, paths)
        self.assertIn("/etc/systemd/system/duora-media-launcher.service.d", paths)


if __name__ == "__main__":
    unittest.main()
