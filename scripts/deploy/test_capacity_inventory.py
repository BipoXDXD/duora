"""Contrato do coletor C1 somente leitura (issue #86), com host sintético; sem Docker, SSH ou VPS."""
from __future__ import annotations

import copy
import json
import unittest

import capacity_inventory as c1

GIB = 1024**3
MIB = 1024**2
CANARY = "CANARY-0f5e9c1e-secret"
ROOT = "/sys/fs/cgroup"
ALL = "cpuset cpu io memory pids"


def cgroup(files: dict[str, str], path: str, *, procs: str = "", populated: bool = True,
           controllers: str = ALL, **limits: str) -> None:
    base = ROOT + path if path != "/" else ROOT
    files[f"{base}/cgroup.controllers"] = controllers
    files[f"{base}/cgroup.subtree_control"] = ALL
    files[f"{base}/cgroup.events"] = f"populated {1 if populated else 0}\nfrozen 0\n"
    files[f"{base}/cgroup.procs"] = procs
    if path == "/":
        return
    defaults = {"cpu.max": "max 100000", "cpu.weight": "100", "memory.max": "max", "memory.high": "max",
                "memory.swap.max": "max", "pids.max": "max", "io.max": "", "memory.current": str(10 * MIB),
                "memory.peak": str(20 * MIB), "pids.current": "3",
                "cpu.stat": "usage_usec 1000000\nnr_periods 10\nnr_throttled 0\nthrottled_usec 0\n",
                "memory.events": "low 0\nhigh 0\nmax 0\noom 0\noom_kill 0\n"}
    defaults.update({key.replace("_", "."): value for key, value in limits.items()})
    for name, value in defaults.items():
        files[f"{base}/{name}"] = value


def container(name: str, identifier: str, pid: int, *, labels: dict[str, str] | None = None,
              memory: int = 0, nano_cpus: int = 0, pids: int | None = None, shares: int = 0,
              parent: str = "", running: bool = True) -> dict:
    return {
        "Id": identifier, "Name": "/" + name,
        "State": {"Status": "running" if running else "exited", "Running": running, "Pid": pid if running else 0,
                  "OOMKilled": False, "RestartCount": 0},
        "Config": {"Image": "registry.example/app@sha256:" + "a" * 64, "Env": ["TOKEN=" + CANARY],
                   "Cmd": ["--password", CANARY], "Labels": {**(labels or {}), "traefik.secret": CANARY}},
        "HostConfig": {"Memory": memory, "MemoryReservation": 0, "MemorySwap": memory * 2 if memory else 0,
                       "NanoCpus": nano_cpus, "CpuQuota": 0, "CpuPeriod": 0, "CpuShares": shares,
                       "PidsLimit": pids, "BlkioWeight": 0, "BlkioDeviceReadBps": None,
                       "BlkioDeviceWriteBps": None, "BlkioDeviceReadIOps": None, "BlkioDeviceWriteIOps": None,
                       "CgroupParent": parent, "RestartPolicy": {"Name": "unless-stopped"}},
        "Mounts": [{"Source": "/etc/secret-" + CANARY}],
    }


def compose_labels(project: str, service: str, workdir: str, files: str, env: str | None = None) -> dict[str, str]:
    labels = {"com.docker.compose.project": project, "com.docker.compose.service": service,
              "com.docker.compose.project.working_dir": workdir, "com.docker.compose.project.config_files": files}
    if env is not None:
        labels["com.docker.compose.project.environment_file"] = env
    return labels


class SyntheticHost:
    """Sistema de arquivos e daemon sintéticos; qualquer comando fora do roteiro reprova o teste."""

    def __init__(self) -> None:
        self.files: dict[str, str] = {}
        self.unreadable: set[str] = set()
        self.missing: set[str] = set()
        self.commands: list[list[str]] = []
        self.clock = 0.0
        self.stat_samples = ["cpu  1000 0 1000 7000 100 0 0 900 0 0\nbtime 1790000000\n",
                             "cpu  1100 0 1100 7600 120 0 0 1080 0 0\nbtime 1790000000\n"]
        self.disk_samples = ["   8       0 sda 100 0 800 50 200 0 1600 80 0 120 130 0 0 0 0\n",
                             "   8       0 sda 150 0 1200 70 400 0 3200 160 0 220 230 0 0 0 0\n"]
        self.psi_cpu = "some avg10=12.00 avg60=20.00 avg300=25.00 total=1\nfull avg10=0.00 avg60=0.00 avg300=0.00 total=0\n"
        self._populate()

    def _populate(self) -> None:
        f = self.files
        f.update({
            "/proc/meminfo": "MemTotal:        8131464 kB\nMemAvailable:    3758960 kB\nSwapTotal:             0 kB\nSwapFree:              0 kB\n",
            "/proc/sys/kernel/random/boot_id": "11111111-2222-3333-4444-555555555555\n",
            "/etc/machine-id": "0123456789abcdef0123456789abcdef\n",
            "/proc/sys/kernel/hostname": "synthetic-vps\n",
            "/proc/sys/kernel/osrelease": "6.8.0-87-generic\n",
            "/proc/sys/kernel/version": "#88-Ubuntu SMP PREEMPT_DYNAMIC\n",
            "/proc/sys/kernel/pid_max": "4194304\n",
            "/proc/uptime": "950000.00 1800000.00\n",
            "/proc/cpuinfo": "processor\t: 0\nmodel name\t: AMD EPYC 9354P\nprocessor\t: 1\nmodel name\t: AMD EPYC 9354P\n",
            "/etc/os-release": 'PRETTY_NAME="Ubuntu 24.04.3 LTS"\nVERSION_ID="24.04"\n',
            "/proc/pressure/memory": "some avg10=0.00 avg60=0.00 avg300=0.00 total=1\nfull avg10=0.00 avg60=0.00 avg300=0.00 total=1\n",
            "/proc/pressure/io": "some avg10=1.00 avg60=1.00 avg300=1.00 total=1\nfull avg10=0.50 avg60=0.50 avg300=0.50 total=1\n",
            "/sys/block/sda/size": "209715200\n", "/sys/block/sda/queue/rotational": "0\n",
            "/sys/block/sda/queue/scheduler": "[mq-deadline] none\n",
            "/sys/block/loop0/size": "100\n",
            "/proc/101/cgroup": "0::/system.slice/docker-aaa.scope\n",
            "/proc/102/cgroup": "0::/system.slice/docker-bbb.scope\n",
            "/proc/103/cgroup": "0::/openclaw.slice/openclaw-private.slice/docker-ccc.scope\n",
            "/proc/104/cgroup": "0::/openclaw.slice/openclaw-private.slice/docker-ddd.scope\n",
            "/proc/105/cgroup": "0::/system.slice/docker-fff.scope\n",
            "/srv/stillon/compose.yaml.json": json.dumps({"services": {"db": {
                "image": "postgres", "environment": {"POSTGRES_PASSWORD": CANARY},
                "deploy": {"resources": {"limits": {"memory": "1g", "cpus": "0.5", "pids": 128}}}}}}),
            "/srv/goblin/compose.json": json.dumps({"services": {"api": {"image": "api", "environment": [CANARY]}}}),
        })
        cgroup(f, "/", procs="1\n2\n")
        cgroup(f, "/system.slice")
        cgroup(f, "/system.slice/docker-aaa.scope", procs="101\n", memory_max=str(GIB), cpu_max="50000 100000",
               pids_max="128", memory_current=str(300 * MIB))
        cgroup(f, "/system.slice/docker-bbb.scope", procs="102\n", memory_current=str(500 * MIB))
        cgroup(f, "/system.slice/docker-fff.scope", procs="105\n", memory_max=str(256 * MIB),
               io_max="8:0 rbps=max wbps=10485760 riops=max wiops=max")
        cgroup(f, "/system.slice/docker.service", procs="200\n", memory_current=str(80 * MIB))
        cgroup(f, "/system.slice/duora-backup.service", procs="210\n", memory_max=str(512 * MIB))
        cgroup(f, "/system.slice/idle.service", populated=False)
        cgroup(f, "/openclaw.slice")
        cgroup(f, "/openclaw.slice/openclaw-private.slice", memory_max=str(2 * GIB), cpu_max="48000 100000",
               pids_max="200")
        cgroup(f, "/openclaw.slice/openclaw-private.slice/docker-ccc.scope", procs="103\n",
               memory_max=str(2 * GIB), cpu_max="48000 100000", pids_max="128")
        cgroup(f, "/openclaw.slice/openclaw-private.slice/docker-ddd.scope", procs="104\n",
               memory_max=str(1536 * MIB), cpu_max="48000 100000", pids_max="128")
        self.containers = [
            container("stillon-db-1", "aaa", 101, memory=GIB, nano_cpus=500_000_000, pids=128,
                      labels=compose_labels("stillon", "db", "/srv/stillon", "/srv/stillon/compose.yaml.json",
                                            "/srv/stillon/.env")),
            container("goblinshift-api-1", "bbb", 102,
                      labels=compose_labels("goblinshift", "api", "/srv/goblin", "/srv/goblin/compose.json")),
            container("openclaw-private-2026", "ccc", 103, memory=2 * GIB, nano_cpus=480_000_000, pids=128,
                      parent="openclaw-private.slice"),
            container("openclaw-private-before-ops-2026", "ddd", 104, memory=1536 * MIB,
                      nano_cpus=480_000_000, pids=128, parent="openclaw-private.slice"),
            container("edge-proxy-1", "fff", 105, memory=256 * MIB,
                      labels={"coolify.applicationUuid": "x", "com.docker.compose.project": "edge"}),
            container("goblinshift-migrate-1", "eee", 0, running=False, memory=512 * MIB,
                      labels=compose_labels("goblinshift", "migrate", "/srv/goblin", "/srv/goblin/compose.json")),
        ]
        self.rendered = {
            "stillon": {"services": {"db": {"deploy": {"resources": {"limits": {"memory": str(GIB), "cpus": "0.5",
                                                                                  "pids": 128}}},
                                            "environment": {"POSTGRES_PASSWORD": CANARY}}}},
            "goblinshift": {"services": {"api": {"environment": {"X": CANARY}},
                                         "migrate": {"mem_limit": str(512 * MIB)}}},
        }

    # Interface Host
    def read(self, path: str) -> str | None:
        if path in self.unreadable:
            raise c1.UnreadableError(path)
        if path == "/proc/stat":
            return self.stat_samples[0] if len(self.stat_samples) == 1 else self.stat_samples.pop(0)
        if path == "/proc/diskstats":
            return self.disk_samples[0] if len(self.disk_samples) == 1 else self.disk_samples.pop(0)
        if path == "/proc/pressure/cpu":
            return self.psi_cpu
        return self.files.get(path)

    def subdirs(self, path: str) -> list[str]:
        prefix = path.rstrip("/") + "/"
        names = {key[len(prefix):].split("/", 1)[0] for key in self.files
                 if key.startswith(prefix) and "/" in key[len(prefix):]}
        return sorted(names)

    def exists(self, path: str) -> bool:
        return path not in self.missing and (path in self.files or path.endswith(".env"))

    def statvfs(self, path: str) -> tuple[int, int] | None:
        return (100 * GIB, 70 * GIB) if path in ("/", "/var/lib/docker") else None

    def run(self, argv: list[str]) -> bytes:
        c1.require_read_only(argv)
        self.commands.append(argv)
        if argv[:2] == ["docker", "info"]:
            return json.dumps({"CgroupDriver": "systemd", "CgroupVersion": "2", "DockerRootDir": "/var/lib/docker",
                               "ServerVersion": "29.9.1"}).encode()
        if argv == ["docker", "compose", "version", "--short"]:
            return b"5.6.0\n"
        if argv[:3] == ["docker", "ps", "--all"]:
            return "".join(item["Id"] + "\n" for item in self.containers).encode()
        if argv[:2] == ["docker", "inspect"]:
            wanted = argv[2:]
            return json.dumps([copy.deepcopy(i) for i in self.containers if i["Id"] in wanted]).encode()
        if argv[:2] == ["docker", "compose"]:
            project = argv[argv.index("--project-name") + 1]
            if project not in self.rendered:
                raise c1.CommandError("compose config failed: exit 1")
            return json.dumps(self.rendered[project]).encode()
        if argv[:2] == ["systemctl", "list-timers"]:
            return json.dumps([{"unit": "duora-backup.timer", "activates": "duora-backup.service",
                                "last": 1790000000000000, "next": 1790003600000000},
                               {"unit": "stillon-vigia.timer", "activates": "stillon-vigia.service"}]).encode()
        if argv == ["systemd-detect-virt"]:
            return b"kvm\n"
        raise AssertionError("comando inesperado: " + repr(argv))

    def monotonic(self) -> float:
        return self.clock

    def sleep(self, seconds: float) -> None:
        self.clock += seconds


def collect(host: SyntheticHost | None = None, **kwargs) -> dict:
    options = {"sample_seconds": 10.0, "guard": c1.Guard(max_cpu_psi=50.0, max_busy_percent=85.0, wait_seconds=0)}
    options.update(kwargs)
    return c1.collect(host or SyntheticHost(), **options)


def by_name(document: dict, name: str) -> dict:
    return next(item for item in document["containers"] if item["name"] == name)


class LimitParsingTests(unittest.TestCase):
    def test_cpu_max_quota_becomes_vcpu_and_max_is_unbounded(self) -> None:
        self.assertEqual(c1.parse_cpu_max("150000 100000"), 1.5)
        self.assertEqual(c1.parse_cpu_max("max 100000"), c1.INF)

    def test_compose_sizes_use_docker_binary_units(self) -> None:
        cases = {"512M": 512 * MIB, "1g": GIB, "1.5g": int(1.5 * GIB), "2GiB": 2 * GIB, 1024: 1024,
                 "1073741824": GIB, "100k": 102400, "64mb": 64 * MIB}
        for raw, expected in cases.items():
            with self.subTest(raw=raw):
                self.assertEqual(c1.parse_bytes(raw), expected)

    def test_unparseable_size_is_rejected_not_guessed(self) -> None:
        for raw in ("${MEM:-512M}", "", "-1g", "lots", True):
            with self.subTest(raw=raw):
                self.assertIsNone(c1.parse_bytes(raw))

    def test_docker_shares_map_to_cgroup_weight_like_current_runc(self) -> None:
        # Pares lidos na VPS atual (Docker 29.9.1, driver systemd): HostConfig.CpuShares -> cpu.weight.
        cases = {None: 100, 0: 100, 2: 1, 64: 12, 512: 59, 768: 80, 1024: 100, 2048: 174, 262144: 10000}
        for shares, weight in cases.items():
            with self.subTest(shares=shares):
                self.assertEqual(c1.shares_to_weight(shares), weight)


class EffectiveLimitTests(unittest.TestCase):
    def test_finite_ancestor_binds_unbounded_leaf(self) -> None:
        host = SyntheticHost()
        cgroup(host.files, "/openclaw.slice/openclaw-private.slice/docker-ccc.scope", procs="103\n")
        item = by_name(collect(host), "openclaw-private-2026")
        memory = item["cgroup"]["effective"]["memory"]
        self.assertEqual(memory["status"], c1.FINITE)
        self.assertEqual(memory["bytes"], 2 * GIB)
        self.assertEqual(memory["binding"], "/openclaw.slice/openclaw-private.slice")
        self.assertEqual(item["cgroup"]["local"]["memory"]["status"], c1.UNBOUNDED)

    def test_all_ancestors_max_is_measured_unbounded(self) -> None:
        item = by_name(collect(), "goblinshift-api-1")
        for resource in ("memory", "cpu", "pids", "io"):
            with self.subTest(resource=resource):
                self.assertEqual(item["cgroup"]["effective"][resource]["status"], c1.UNBOUNDED)
        self.assertEqual(item["cgroup"]["effective"]["memory"]["reason"], "max")

    def test_unreadable_limit_without_finite_ancestor_is_unknown_not_unbounded(self) -> None:
        host = SyntheticHost()
        host.unreadable.add(ROOT + "/system.slice/docker-bbb.scope/memory.max")
        memory = by_name(collect(host), "goblinshift-api-1")["cgroup"]["effective"]["memory"]
        self.assertEqual(memory["status"], c1.UNKNOWN)
        self.assertIn("/system.slice/docker-bbb.scope", memory["unknown"])

    def test_disabled_controller_is_unbounded_with_its_own_reason(self) -> None:
        host = SyntheticHost()
        base = ROOT + "/system.slice/docker-bbb.scope"
        host.files[base + "/cgroup.controllers"] = "cpuset cpu memory pids"
        del host.files[base + "/io.max"]
        io = by_name(collect(host), "goblinshift-api-1")["cgroup"]["local"]["io"]
        self.assertEqual((io["status"], io["reason"]), (c1.UNBOUNDED, "controller-disabled"))

    def test_missing_file_with_enabled_controller_is_unknown(self) -> None:
        host = SyntheticHost()
        del host.files[ROOT + "/system.slice/docker-bbb.scope/pids.max"]
        pids = by_name(collect(host), "goblinshift-api-1")["cgroup"]["local"]["pids"]
        self.assertEqual((pids["status"], pids["reason"]), (c1.UNKNOWN, "absent"))

    def test_finite_io_max_entry_is_reported_per_device(self) -> None:
        io = by_name(collect(), "edge-proxy-1")["cgroup"]["effective"]["io"]
        self.assertEqual(io["status"], c1.FINITE)
        self.assertEqual(io["devices"], {"8:0": {"wbps": 10485760}})

    def test_every_ancestor_is_recorded_leaf_first(self) -> None:
        chain = by_name(collect(), "openclaw-private-2026")["cgroup"]["ancestors"]
        self.assertEqual([node["path"] for node in chain],
                         ["/openclaw.slice/openclaw-private.slice/docker-ccc.scope",
                          "/openclaw.slice/openclaw-private.slice", "/openclaw.slice", "/"])
        self.assertEqual(set(chain[0]["limits"]),
                         {"cpu.max", "cpu.weight", "memory.max", "memory.high", "memory.swap.max", "pids.max",
                          "io.max", "io.weight"})


class LimitChainTests(unittest.TestCase):
    def test_declared_rendered_hostconfig_and_cgroup_agree(self) -> None:
        chain = by_name(collect(), "stillon-db-1")["chain"]
        self.assertEqual(chain["memory"]["values"],
                         {"declared": GIB, "rendered": GIB, "host_config": GIB, "cgroup": GIB})
        self.assertEqual(chain["memory"]["state"], "consistent")
        self.assertEqual(chain["cpu"]["values"]["cgroup"], 0.5)
        self.assertEqual(chain["pids"]["state"], "consistent")
        self.assertEqual(chain["cpu_weight"]["state"], "consistent")

    def test_hostconfig_drift_from_cgroup_is_flagged_at_that_stage(self) -> None:
        host = SyntheticHost()
        host.files[ROOT + "/system.slice/docker-aaa.scope/memory.max"] = str(2 * GIB)
        memory = by_name(collect(host), "stillon-db-1")["chain"]["memory"]
        self.assertEqual(memory["state"], "drift:host_config->cgroup")

    def test_limit_set_only_by_the_cgroup_is_not_called_drift(self) -> None:
        host = SyntheticHost()
        host.files[ROOT + "/system.slice/docker-bbb.scope/pids.max"] = "9483"
        pids = by_name(collect(host), "goblinshift-api-1")["chain"]["pids"]
        self.assertEqual(pids["state"], "cgroup-only")
        self.assertEqual(pids["values"]["cgroup"], 9483)

    def test_absent_declaration_is_declared_unbounded_not_unknown(self) -> None:
        memory = by_name(collect(), "goblinshift-api-1")["chain"]["memory"]
        self.assertEqual(memory["values"], {"declared": None, "rendered": None, "host_config": None, "cgroup": None})
        self.assertEqual(memory["state"], "consistent")

    def test_failed_render_is_unknown_and_does_not_leak_stderr(self) -> None:
        host = SyntheticHost()
        del host.rendered["stillon"]
        memory = by_name(collect(host), "stillon-db-1")["chain"]["memory"]
        self.assertEqual(memory["state"], "unknown:rendered")

    def test_missing_env_file_skips_render_and_records_the_reason(self) -> None:
        host = SyntheticHost()
        host.missing.add("/srv/stillon/.env")
        document = collect(host)
        self.assertEqual(by_name(document, "stillon-db-1")["chain"]["memory"]["state"], "unknown:rendered")
        render = next(r for r in document["compose_renders"] if r["project"] == "stillon")
        self.assertEqual(render["status"], "missing-file:/srv/stillon/.env")
        self.assertFalse(any("stillon" in argv for argv in host.commands if argv[:2] == ["docker", "compose"]))

    def test_missing_declared_file_is_unknown_with_reason(self) -> None:
        host = SyntheticHost()
        del host.files["/srv/goblin/compose.json"]
        document = collect(host)
        self.assertEqual(by_name(document, "goblinshift-api-1")["chain"]["memory"]["state"], "unknown:declared")
        render = next(r for r in document["compose_renders"] if r["project"] == "goblinshift")
        self.assertEqual(render["declared"], "missing-file:/srv/goblin/compose.json")

    def test_container_outside_compose_has_no_declaration(self) -> None:
        memory = by_name(collect(), "openclaw-private-2026")["chain"]["memory"]
        self.assertEqual(memory["state"], "not-compose")
        self.assertEqual(memory["values"]["host_config"], 2 * GIB)

    def test_one_render_per_compose_project_group(self) -> None:
        host = SyntheticHost()
        collect(host)
        renders = [argv for argv in host.commands if argv[:2] == ["docker", "compose"] and "config" in argv]
        self.assertEqual(len(renders), 2)
        stillon = next(argv for argv in renders if "stillon" in argv)
        self.assertEqual(stillon[-3:], ["config", "--format", "json"])
        self.assertIn("/srv/stillon/.env", stillon)


class BudgetTests(unittest.TestCase):
    def test_nested_limits_are_counted_once_by_the_tightest_ancestor(self) -> None:
        openclaw = collect()["budget"]["owners"]["openclaw"]
        self.assertEqual(openclaw["memory"]["status"], c1.FINITE)
        self.assertEqual(openclaw["memory"]["bytes"], 2 * GIB)
        self.assertEqual(openclaw["cpu"]["vcpu"], 0.48)
        self.assertEqual(openclaw["pids"]["count"], 200)

    def test_owner_with_unbounded_member_is_unbounded_with_finite_part(self) -> None:
        host_owner = collect()["budget"]["owners"]["host"]
        self.assertEqual(host_owner["memory"]["status"], c1.UNBOUNDED)
        self.assertEqual(host_owner["memory"]["unbounded"], ["/system.slice/docker.service"])
        self.assertEqual(host_owner["memory"]["finite_part_bytes"], 0)

    def test_total_is_unbounded_when_any_populated_leaf_is_unbounded(self) -> None:
        total = collect()["budget"]["total"]["memory"]
        self.assertEqual(total["status"], c1.UNBOUNDED)
        self.assertEqual(total["finite_part_bytes"], GIB + 256 * MIB + 512 * MIB + 2 * GIB)
        self.assertEqual(sorted(total["unbounded"]),
                         ["/system.slice/docker-bbb.scope", "/system.slice/docker.service"])

    def test_total_becomes_finite_once_every_leaf_is_bounded(self) -> None:
        host = SyntheticHost()
        host.files[ROOT + "/system.slice/docker-bbb.scope/memory.max"] = str(GIB)
        host.files[ROOT + "/system.slice/memory.max"] = str(3 * GIB)
        total = collect(host)["budget"]["total"]["memory"]
        self.assertEqual((total["status"], total["bytes"]), (c1.FINITE, 3 * GIB + 2 * GIB))

    def test_unpopulated_cgroups_and_stopped_containers_are_not_budgeted(self) -> None:
        document = collect()
        stopped = by_name(document, "goblinshift-migrate-1")
        self.assertEqual(stopped["cgroup"], None)
        self.assertEqual(stopped["chain"]["memory"]["values"]["host_config"], 512 * MIB)
        self.assertIn("goblinshift-migrate-1", document["budget"]["stopped_containers"])
        self.assertNotIn("/system.slice/idle.service", json.dumps(document["budget"]))

    def test_owners_cover_apps_platform_host_and_unassigned(self) -> None:
        owners = collect()["budget"]["owners"]
        self.assertEqual(set(owners), {"goblinshift", "stillon", "openclaw", "host", "unassigned"} | {"duora"})
        self.assertEqual(owners["duora"]["members"], ["/system.slice/duora-backup.service"])
        self.assertEqual(owners["unassigned"]["members"], ["/system.slice/docker-fff.scope"])

    def test_usage_is_aggregated_without_double_counting(self) -> None:
        openclaw = collect()["budget"]["owners"]["openclaw"]
        self.assertEqual(openclaw["memory_current_bytes"], 10 * MIB)


class ClassificationTests(unittest.TestCase):
    def test_container_rules(self) -> None:
        cases = [({"name": "openclaw-x"}, "openclaw"), ({"project": "duora"}, "duora"),
                 ({"name": "deployment-observer-abc"}, "duora"), ({"coolify_project": "kairyaquest"}, "kairyaquest"),
                 ({"working_dir": "/data/coolify/source", "project": "source"}, "coolify"),
                 ({"name": "coolify-sentinel"}, "coolify"), ({"project": "stillon"}, "stillon"),
                 ({"project": "goblinshift"}, "goblinshift"), ({"project": "edge"}, "unassigned")]
        for facts, owner in cases:
            with self.subTest(facts=facts):
                self.assertEqual(c1.classify_container(facts), owner)

    def test_unit_rules_and_backup_tag(self) -> None:
        self.assertEqual(c1.classify_unit("/openclaw.slice/openclaw-private.slice"), "openclaw")
        self.assertEqual(c1.classify_unit("/system.slice/system-duora\\x2dbackup.slice/x.service"), "duora")
        self.assertEqual(c1.classify_unit("/system.slice/ssh.service"), "host")
        self.assertTrue(c1.is_backup("duora-backup-1"))
        self.assertFalse(c1.is_backup("stillon-vigia.service"))


class HostFactsTests(unittest.TestCase):
    def test_steal_iowait_and_busy_come_from_two_proc_stat_samples(self) -> None:
        cpu = collect()["host"]["cpu_sample"]
        # Delta: user 100, system 100, idle 600, iowait 20, steal 180 = 1000 ticks.
        self.assertEqual(cpu["steal_percent"], 18.0)
        self.assertEqual(cpu["iowait_percent"], 2.0)
        self.assertEqual(cpu["busy_percent"], 20.0)
        self.assertEqual(cpu["non_idle_percent"], 38.0)

    def test_disk_iops_are_passive_and_capability_stays_unknown(self) -> None:
        disks = collect()["host"]["disks"]
        self.assertEqual([d["name"] for d in disks["devices"]], ["sda"])
        sda = disks["devices"][0]
        self.assertEqual((sda["observed_read_iops"], sda["observed_write_iops"]), (5.0, 20.0))
        self.assertEqual(disks["iops_capability"]["status"], c1.UNKNOWN)
        self.assertEqual(disks["filesystems"]["/"], {"total_bytes": 100 * GIB, "available_bytes": 70 * GIB})

    def test_identity_hashes_machine_id(self) -> None:
        identity = collect()["host"]["identity"]
        self.assertEqual(identity["kernel"], "6.8.0-87-generic")
        self.assertEqual(identity["virtualization"], "kvm")
        self.assertEqual(identity["memory"]["mem_total_bytes"], 8131464 * 1024)
        self.assertEqual(identity["cgroup"]["root_controllers"], ALL.split())
        self.assertNotIn("0123456789abcdef0123456789abcdef", json.dumps(identity))
        self.assertEqual(len(identity["machine_id_sha256"]), 64)

    def test_backups_inventory_lists_backup_timers_containers_and_units(self) -> None:
        backups = collect()["backups"]
        self.assertEqual([t["unit"] for t in backups["timers"]], ["duora-backup.timer"])
        self.assertIn("/system.slice/duora-backup.service", backups["cgroups"])
        self.assertEqual(backups["coolify_scheduled_backups"]["status"], c1.UNKNOWN)


class SafetyTests(unittest.TestCase):
    def test_no_secret_reaches_the_document(self) -> None:
        text = json.dumps(collect())
        self.assertNotIn(CANARY, text)
        self.assertNotIn("POSTGRES_PASSWORD", text)

    def test_mutating_or_unknown_commands_are_refused(self) -> None:
        refused = [["docker", "rm", "x"], ["docker", "compose", "up", "-d"], ["docker", "compose", "-f", "a", "down"],
                   ["docker", "update", "--memory", "1g", "x"], ["systemctl", "restart", "docker"],
                   ["docker", "compose", "config", "--format", "json", "&&", "rm"], ["sh", "-c", "true"],
                   ["docker", "exec", "x", "cat", "/etc/shadow"], ["docker", "ps", "--all", "-q", "--filter", "x"]]
        for argv in refused:
            with self.subTest(argv=argv), self.assertRaises(c1.CommandError):
                c1.require_read_only(argv)

    def test_every_issued_command_is_read_only(self) -> None:
        host = SyntheticHost()
        collect(host)
        self.assertTrue(host.commands)
        for argv in host.commands:
            c1.require_read_only(argv)

    def test_guard_aborts_before_any_command_when_host_is_saturated(self) -> None:
        host = SyntheticHost()
        host.psi_cpu = "some avg10=70.00 avg60=60.00 avg300=50.00 total=1\n"
        host.stat_samples = ["cpu  0 0 0 100 0 0 0 0 0 0\n", "cpu  95 0 0 100 0 0 0 0 0 0\n"]
        with self.assertRaises(c1.GuardTripped):
            collect(host)
        self.assertEqual(host.commands, [])

    def test_guard_tolerates_high_psi_without_high_cpu(self) -> None:
        host = SyntheticHost()
        host.psi_cpu = "some avg10=70.00 avg60=60.00 avg300=50.00 total=1\n"
        document = collect(host)
        self.assertEqual(document["guard"]["checks"][0]["cpu_psi_avg10"], 70.0)


class LocalHostTests(unittest.TestCase):
    def test_subdirs_follow_symlinks_like_sys_block(self) -> None:
        import os
        import tempfile
        with tempfile.TemporaryDirectory() as root:
            os.mkdir(os.path.join(root, "real"))
            os.symlink(os.path.join(root, "real"), os.path.join(root, "sda"))
            open(os.path.join(root, "file"), "w").close()
            self.assertEqual(c1.LocalHost().subdirs(root), ["real", "sda"])


class DocumentTests(unittest.TestCase):
    def test_hash_covers_the_whole_document_and_detects_tampering(self) -> None:
        document = collect()
        self.assertEqual(document["schema"], c1.SCHEMA)
        self.assertTrue(c1.verify(document))
        tampered = copy.deepcopy(document)
        tampered["host"]["identity"]["kernel"] = "other"
        self.assertFalse(c1.verify(tampered))

    def test_summary_names_hash_owners_and_findings(self) -> None:
        document = collect()
        summary = c1.summarize(document)
        self.assertIn(document["content_sha256"], summary)
        self.assertIn("openclaw", summary)
        self.assertIn("UNBOUNDED", summary)
        self.assertIn("goblinshift-api-1", summary)
        self.assertNotIn(CANARY, summary)

    def test_document_is_deterministic_for_the_same_host_state(self) -> None:
        first, second = collect(), collect()
        first.pop("collected_at")
        second.pop("collected_at")
        first.pop("content_sha256")
        second.pop("content_sha256")
        self.assertEqual(first, second)


if __name__ == "__main__":
    unittest.main()
