"""Contrato do sampler C2 da VPS (issue #86): host sintético, sem Docker, SSH ou VPS."""
from __future__ import annotations

import io
import json
import unittest

import c2_probe as probe

C2_ID = "a" * 64
OTHER_ID = "b" * 64
ROOT = "/sys/fs/cgroup"


def cgroup_files(base: str, *, usage: int = 1_000_000, throttled: int = 0, oom_kill: int = 0,
                 cpu_psi: float = 0.0) -> dict[str, str]:
    return {
        f"{base}/cpu.stat": f"usage_usec {usage}\nuser_usec 1\nsystem_usec 1\nnr_periods 100\n"
                            f"nr_throttled {throttled}\nthrottled_usec {throttled * 1000}\n",
        f"{base}/memory.current": str(300 * 1024 * 1024),
        f"{base}/memory.peak": str(400 * 1024 * 1024),
        f"{base}/memory.events": f"low 0\nhigh 0\nmax 0\noom 0\noom_kill {oom_kill}\n",
        f"{base}/cpu.pressure": f"some avg10={cpu_psi:.2f} avg60=0.00 avg300=0.00 total=10\n"
                                "full avg10=0.00 avg60=0.00 avg300=0.00 total=0\n",
        f"{base}/memory.pressure": "some avg10=1.50 avg60=0.00 avg300=0.00 total=10\n"
                                   "full avg10=0.50 avg60=0.00 avg300=0.00 total=0\n",
        f"{base}/io.pressure": "some avg10=2.00 avg60=0.00 avg300=0.00 total=10\n"
                               "full avg10=1.00 avg60=0.00 avg300=0.00 total=0\n",
        f"{base}/io.stat": "8:0 rbytes=100 wbytes=200 rios=1 wios=2 dbytes=0 dios=0\n"
                           "8:16 rbytes=1 wbytes=2 rios=1 wios=1 dbytes=0 dios=0\n",
    }


class FakeHost:
    def __init__(self) -> None:
        self.files: dict[str, str] = {
            "/proc/stat": "cpu  100 0 50 800 20 0 5 25 0 0\ncpu0 1 1 1 1 1 1 1 1 0 0\n",
            "/proc/meminfo": "MemTotal:        8131464 kB\nMemFree:  100 kB\nMemAvailable:    3758096 kB\n",
            "/proc/vmstat": "pgpgin 1\npswpin 3\npswpout 4\noom_kill 0\n",
            "/proc/pressure/cpu": "some avg10=12.50 avg60=1.00 avg300=0.00 total=1\n",
            "/proc/pressure/memory": "some avg10=0.10 avg60=0.00 avg300=0.00 total=1\n"
                                     "full avg10=0.00 avg60=0.00 avg300=0.00 total=1\n",
            "/proc/pressure/io": "some avg10=3.00 avg60=0.00 avg300=0.00 total=1\n"
                                 "full avg10=1.00 avg60=0.00 avg300=0.00 total=1\n",
            "/proc/diskstats": "   8       0 sda 10 0 80 5 20 0 160 7 0 30 40 0 0 0 0\n"
                               "   7       0 loop0 1 0 8 0 0 0 0 0 0 0 0 0 0 0 0\n",
        }
        self.files.update(cgroup_files(f"{ROOT}/system.slice/docker-{C2_ID}.scope"))
        self.files.update(cgroup_files(f"{ROOT}/system.slice/docker-{OTHER_ID}.scope", cpu_psi=7.0))
        self.files.update(cgroup_files(f"{ROOT}/duora-media.slice"))
        self.commands: list[list[str]] = []
        self.ps_output = f"{C2_ID}\tduora-c2-postgres-1\tduora-c2\n{OTHER_ID}\tstillon-web-1\tstillon\n"
        self.sql_output = "12|0|3.5|1|\\N|2|7.25|0.5|4\n"
        self.now = 1_000.0

    def read(self, path: str) -> str | None:
        return self.files.get(path)

    def exists(self, path: str) -> bool:
        return any(name == path or name.startswith(path + "/") for name in self.files)

    def statvfs(self, path: str) -> tuple[int, int] | None:
        return (100, 20) if path == "/" else None

    def run(self, argv: list[str]) -> str:
        probe.require_allowed(argv, project="duora-c2")
        self.commands.append(argv)
        if argv[:2] == ["docker", "ps"]:
            return self.ps_output
        return self.sql_output

    def time(self) -> float:
        return self.now

    def sleep(self, seconds: float) -> None:
        self.now += seconds


class AllowlistTests(unittest.TestCase):
    def test_accepts_exact_ps_and_archiver_query_of_the_shadow_project(self) -> None:
        probe.require_allowed(list(probe.PS_COMMAND), project="duora-c2")
        probe.require_allowed(probe.stats_command("duora-c2"), project="duora-c2")

    def test_rejects_mutation_and_other_projects(self) -> None:
        for argv in (["docker", "rm", "-f", "x"], ["docker", "exec", "duora-postgres-1", "sh"],
                     probe.stats_command("duora-c2")[:-1] + ["drop table x"],
                     ["docker", "compose", "-p", "duora-c2", "down"]):
            with self.subTest(argv=argv), self.assertRaises(probe.CommandRefused):
                probe.require_allowed(argv, project="duora-c2")
        with self.assertRaises(probe.CommandRefused):
            probe.require_allowed(probe.stats_command("duora-c2"), project="stillon")

    def test_production_project_name_is_refused(self) -> None:
        for project in ("duora", "", "../x", "Duora-C2"):
            with self.subTest(project=project), self.assertRaises(probe.CommandRefused):
                probe.validate_project(project)


class SampleTests(unittest.TestCase):
    def setUp(self) -> None:
        self.host = FakeHost()
        self.sampler = probe.Sampler(self.host, project="duora-c2", extra_cgroups=("duora-media.slice",))

    def test_host_counters_are_raw_and_cumulative(self) -> None:
        sample = self.sampler.sample()
        host = sample["host"]
        self.assertEqual(host["cpu"], {"user": 100, "nice": 0, "system": 50, "idle": 800, "iowait": 20, "irq": 0,
                                       "softirq": 5, "steal": 25})
        self.assertEqual(host["mem_total_mib"], 7940.88)
        self.assertEqual(host["mem_available_mib"], 3670.02)
        self.assertEqual(host["vmstat"], {"pswpin": 3, "pswpout": 4, "oom_kill": 0})
        self.assertEqual(host["psi"]["cpu"], {"some_avg10": 12.5})
        self.assertEqual(host["psi"]["io"], {"some_avg10": 3.0, "full_avg10": 1.0})
        self.assertEqual(host["disks"], {"sda": {"reads": 10, "read_sectors": 80, "writes": 20,
                                                 "write_sectors": 160, "io_ms": 30, "weighted_ms": 40}})
        self.assertEqual(host["filesystems"], {"/": 80.0})

    def test_containers_are_classified_by_compose_project_only(self) -> None:
        cgroups = self.sampler.sample()["cgroups"]
        self.assertEqual(set(cgroups), {"duora-c2-postgres-1", "stillon-web-1", "duora-media.slice"})
        self.assertEqual(cgroups["duora-c2-postgres-1"]["owner"], "c2")
        self.assertEqual(cgroups["stillon-web-1"]["owner"], "other")
        self.assertEqual(cgroups["duora-media.slice"]["owner"], "other")
        postgres = cgroups["duora-c2-postgres-1"]
        self.assertEqual(postgres["usage_usec"], 1_000_000)
        self.assertEqual(postgres["nr_periods"], 100)
        self.assertEqual(postgres["oom_kill"], 0)
        self.assertEqual(postgres["memory_current_mib"], 300.0)
        self.assertEqual(postgres["io_rbytes"], 101)
        self.assertEqual(postgres["io_wbytes"], 202)
        self.assertEqual(cgroups["stillon-web-1"]["cpu_psi_some_avg10"], 7.0)

    def test_database_stats_are_parsed_with_null_ages(self) -> None:
        stats = self.sampler.sample()["database"]
        self.assertEqual(stats, {"archived_count": 12, "failed_count": 0, "last_archived_age_s": 3.5,
                                 "archive_pending": 1, "last_failed_age_s": None, "jobs_due": 2, "jobs_oldest_due_age_s": 7.25,
                                 "jobs_max_lateness_s": 0.5, "outbox_pending": 4})

    def test_missing_files_become_null_not_zero(self) -> None:
        del self.host.files["/proc/pressure/io"]
        del self.host.files[f"{ROOT}/system.slice/docker-{OTHER_ID}.scope/cpu.pressure"]
        sample = self.sampler.sample()
        self.assertIsNone(sample["host"]["psi"]["io"])
        self.assertIsNone(sample["cgroups"]["stillon-web-1"]["cpu_psi_some_avg10"])

    def test_database_failure_is_reported_and_sampling_continues(self) -> None:
        self.host.sql_output = "garbage"
        sample = self.sampler.sample()
        self.assertIsNone(sample["database"])
        self.assertIn("database", sample["errors"])

    def test_loop_emits_one_json_line_per_interval_until_duration(self) -> None:
        out = io.StringIO()
        probe.run_loop(self.sampler, self.host, interval=5.0, duration=12.0, out=out)
        lines = [json.loads(line) for line in out.getvalue().splitlines()]
        self.assertEqual([line["t"] for line in lines], [1000.0, 1005.0, 1010.0])
        self.assertTrue(all(line["schema"] == probe.SCHEMA for line in lines))


if __name__ == "__main__":
    unittest.main()
