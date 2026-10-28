"""Harness C2 (issue #86): protocolo, guardas, orquestração com dublês e relatório; sem SSH, Docker ou VPS."""
from __future__ import annotations

import io
import json
import queue
import tempfile
import unittest
from contextlib import redirect_stdout
from dataclasses import replace
from pathlib import Path
from typing import Any

import c2_harness as harness
import c2_load as load

LIMITS = harness.Limits()


def raw_sample(t: float, *, idle: int = 800, user: int = 100, steal: int = 25, mem: float = 3000.0, swap: int = 0,
               host_oom: int = 0, other_psi: float = 0.0, usage: int = 0, periods: int = 0, throttled: int = 0,
               archive_age: float | None = 3.0, archive_pending: int | None = 1, failed: int = 0, fs: float = 50.0, jobs_due: int = 0) -> dict:
    return {
        "schema": "duora.c2-sample/v1", "t": t,
        "host": {"cpu": {"user": user, "nice": 0, "system": 50, "idle": idle, "iowait": 20, "irq": 0, "softirq": 5,
                         "steal": steal},
                 "mem_total_mib": 7940.88, "mem_available_mib": mem,
                 "vmstat": {"pswpin": swap, "pswpout": 0, "oom_kill": host_oom},
                 "psi": {"cpu": {"some_avg10": 10.0}, "memory": {"some_avg10": 0.0, "full_avg10": 0.0},
                         "io": {"some_avg10": 1.0, "full_avg10": 0.5}},
                 "disks": {"sda": {"reads": 10, "read_sectors": 2048 * int(t), "writes": 20,
                                   "write_sectors": 4096 * int(t), "io_ms": 100 * int(t), "weighted_ms": 0}},
                 "filesystems": {"/": fs}},
        "cgroups": {
            "duora-c2-web-1": {"owner": "c2", "usage_usec": usage, "nr_periods": periods, "nr_throttled": throttled,
                               "throttled_usec": 0, "memory_current_mib": 400.0, "memory_peak_mib": 500.0,
                               "oom_kill": 0, "cpu_psi_some_avg10": 5.0, "memory_psi_some_avg10": 0.0,
                               "io_psi_some_avg10": 0.0, "io_rbytes": 0, "io_wbytes": 0},
            "stillon-web-1": {"owner": "other", "usage_usec": 0, "nr_periods": 0, "nr_throttled": 0,
                              "throttled_usec": 0, "memory_current_mib": 100.0, "memory_peak_mib": 120.0,
                              "oom_kill": 0, "cpu_psi_some_avg10": other_psi, "memory_psi_some_avg10": 0.0,
                              "io_psi_some_avg10": 0.0, "io_rbytes": 0, "io_wbytes": 0},
        },
        "database": {"archived_count": 10, "failed_count": failed, "last_archived_age_s": archive_age,
                     "archive_pending": archive_pending, "last_failed_age_s": None, "jobs_due": jobs_due, "jobs_oldest_due_age_s": 0.0,
                     "jobs_max_lateness_s": 0.2, "outbox_pending": 0},
        "errors": {},
    }


def derived_series(count: int, **overrides: Any) -> list[dict]:
    raws = [raw_sample(1000.0 + 5 * index, **overrides) for index in range(count + 1)]
    return [harness.derive(a, b) for a, b in zip(raws, raws[1:], strict=False)]


def request(t: float, status: int, kind: str = "read", ms: float = 50.0, phase: str = "peak") -> load.RequestRecord:
    return load.RequestRecord(t, "me", kind, status, ms, phase)


class ProtocolTests(unittest.TestCase):
    def test_phases_follow_capacity_protocol_without_truncation(self) -> None:
        phases = harness.C2Protocol().phases()
        names = [phase.name for phase in phases]
        self.assertEqual(names, ["baseline", "warmup", "ramp-10", "ramp-25", "ramp-50", "ramp-100", "peak", "entry",
                                 "reconnect-1", "reconnect-2", "reconnect-3", "recovery", "full", "settle", "diff",
                                 "recovery-final"])
        by_name = {phase.name: phase for phase in phases}
        self.assertEqual((by_name["warmup"].seconds, by_name["peak"].seconds, by_name["peak"].clients), (300, 1800, 100))
        self.assertEqual([by_name[f"ramp-{n}"].seconds for n in (10, 25, 50, 100)], [300] * 4)
        self.assertEqual(by_name["recovery"].seconds, 900)
        self.assertEqual((by_name["full"].backup, by_name["diff"].backup), ("full", "diff"))
        self.assertLess(names.index("full"), names.index("settle"))
        self.assertLess(names.index("settle"), names.index("diff"))
        self.assertEqual(by_name["full"].seconds, 1800)
        self.assertEqual(harness.nominal_seconds(phases), 9720)

    def test_deadline_shorter_than_duration_is_rejected(self) -> None:
        with self.assertRaises(ValueError):
            harness.Phase("x", "steady", 1, 10, 9)


class WindowProfileTests(unittest.TestCase):
    """Perfil `janela` (decisão de 28/10/2026): a janela inteira na VPS cabe em 3 h, com #174 junto."""

    def setUp(self) -> None:
        self.protocol = harness.PROFILES["janela"]
        self.phases = self.protocol.phases()
        self.by_name = {phase.name: phase for phase in self.phases}

    def test_full_profile_is_the_original_protocol(self) -> None:
        self.assertEqual(harness.PROFILES["completo"], harness.C2Protocol())
        self.assertEqual(harness.PROFILES["completo"].repetitions, 3)
        self.assertIsNone(harness.PROFILES["completo"].window_seconds)

    def test_phases_run_one_ramp_one_peak_and_three_contention_cycles(self) -> None:
        names = [phase.name for phase in self.phases]
        cycle = ["entry-c{n}", "reconnect-c{n}-1", "reconnect-c{n}-2", "reconnect-c{n}-3", "full-c{n}", "settle-c{n}",
                 "diff-c{n}"]
        expected = ["baseline", "warmup", "ramp-10", "ramp-25", "ramp-50", "ramp-100", "peak"]
        for n in (1, 2, 3):
            expected += [name.format(n=n) for name in cycle]
        self.assertEqual(names, expected + ["recovery-final"])

    def test_durations_and_nominal_fit_the_window(self) -> None:
        self.assertEqual([self.by_name[f"ramp-{n}"].seconds for n in (10, 25, 50, 100)], [120] * 4)
        self.assertEqual((self.by_name["warmup"].seconds, self.by_name["peak"].seconds), (120, 600))
        self.assertEqual(self.by_name["recovery-final"].seconds, 300)
        self.assertEqual(harness.nominal_seconds(self.phases), 4095)
        self.assertEqual(self.protocol.window_seconds, 4800)

    def test_full_and_diff_stay_in_distinct_phases_under_peak_load(self) -> None:
        for n in (1, 2, 3):
            full, settle, diff = (self.by_name[f"{kind}-c{n}"] for kind in ("full", "settle", "diff"))
            self.assertEqual((full.backup, diff.backup, settle.backup), ("full", "diff", None))
            self.assertEqual((full.clients, settle.clients, diff.clients), (100, 100, 100))
            self.assertEqual((full.seconds, full.deadline_seconds, diff.seconds, settle.seconds), (240, 600, 240, 120))

    def test_peak_load_is_sustained_longer_than_the_protocol_thirty_minutes(self) -> None:
        self.assertEqual(harness.longest_sustained_seconds(self.phases, 100), 3195)
        self.assertEqual(harness.longest_sustained_seconds(harness.C2Protocol().phases(), 100), 2400)

    def test_one_run_holds_the_three_repetitions(self) -> None:
        self.assertEqual(self.protocol.repetitions, 1)
        self.assertEqual(sum(1 for phase in self.phases if phase.backup == "full"), 3)
        self.assertEqual(sum(1 for phase in self.phases if phase.kind == "entry"), 3)

    def test_every_reduction_is_declared(self) -> None:
        text = " ".join(self.protocol.deviations)
        for topic in ("3 ciclos", "aquecimento", "rampa", "30 min", "recuperação", "FULL", "restic", "WINDOW"):
            self.assertIn(topic, text)
        self.assertEqual(harness.PROFILES["completo"].deviations, ())

    def test_plan_prints_the_profile(self) -> None:
        out = io.StringIO()
        with redirect_stdout(out):
            self.assertEqual(harness.main(["plan", "--profile", "janela"]), harness.EXIT_OK)
        text = out.getvalue()
        self.assertIn("perfil janela", text)
        self.assertIn("nominal 68 min", text)
        self.assertIn("limite da janela 80 min", text)
        self.assertIn("100 clientes contínuos 53 min", text)


class DeriveTests(unittest.TestCase):
    def test_cpu_disk_and_cgroup_rates(self) -> None:
        before = raw_sample(1000.0, idle=800, user=100, steal=25, usage=0, periods=100, throttled=0)
        after = raw_sample(1005.0, idle=860, user=120, steal=45, usage=2_500_000, periods=200, throttled=30)
        derived = harness.derive(before, after)
        # delta: user 20, idle 60, steal 20 -> total 100
        self.assertEqual((derived["busy_pct"], derived["steal_pct"], derived["iowait_pct"]), (20.0, 20.0, 0.0))
        web = derived["cgroups"]["duora-c2-web-1"]
        self.assertEqual(web["cpu_pct"], 50.0)  # 2,5 s de CPU em 5 s = meio vCPU
        self.assertEqual(web["throttled_ratio"], 0.3)
        disk = derived["disks"]["sda"]
        self.assertEqual(disk["write_mib_s"], 2.0)  # 4096 setores/s * 512 B = 2 MiB/s
        self.assertEqual(disk["util_pct"], 10.0)

    def test_missing_counter_is_none_not_zero(self) -> None:
        before = raw_sample(1000.0)
        after = raw_sample(1005.0)
        after["host"]["vmstat"]["pswpin"] = None
        self.assertIsNone(harness.derive(before, after)["swap_pages"])

    def test_out_of_order_samples_are_rejected(self) -> None:
        with self.assertRaises(ValueError):
            harness.derive(raw_sample(1005.0), raw_sample(1000.0))


class GuardTests(unittest.TestCase):
    def state(self, samples: list[dict]) -> harness.GuardState:
        state = harness.GuardState(samples=samples, last_sample_at=samples[-1]["t"] if samples else 0.0)
        return state

    def codes(self, state: harness.GuardState, requests: list[load.RequestRecord] | None = None,
              now: float | None = None) -> list[str]:
        moment = now if now is not None else (state.last_sample_at or 0.0)
        return [trip.code for trip in harness.evaluate_guards(state, requests or [], moment, LIMITS)]

    def test_healthy_run_has_no_trip(self) -> None:
        self.assertEqual(self.codes(self.state(derived_series(20))), [])

    def test_memory_must_stay_low_for_sixty_seconds(self) -> None:
        self.assertEqual(self.codes(self.state(derived_series(11, mem=500.0))), [])  # 55 s
        self.assertEqual(self.codes(self.state(derived_series(12, mem=500.0))), ["MEM_LOW"])  # 60 s
        recovered = derived_series(12, mem=500.0)
        recovered[-1]["mem_available_mib"] = 512.0  # on point: 512 não está abaixo de 512
        self.assertEqual(self.codes(self.state(recovered)), [])

    def test_oom_trips_immediately(self) -> None:
        samples = derived_series(3)
        samples[-1]["oom_kills"] = 1
        self.assertEqual(self.codes(self.state(samples)), ["OOM"])

    def test_disk_trips_at_85_percent(self) -> None:
        self.assertEqual(self.codes(self.state(derived_series(2, fs=84.9))), [])
        self.assertEqual(self.codes(self.state(derived_series(2, fs=85.0))), ["DISK"])

    def test_archive_lag_trips_at_240_seconds_before_rpo(self) -> None:
        self.assertEqual(self.codes(self.state(derived_series(2, archive_age=239.0))), [])
        self.assertEqual(self.codes(self.state(derived_series(2, archive_age=240.0))), ["ARCHIVE_LAG"])

    def test_idle_database_without_completed_segment_is_not_archive_lag(self) -> None:
        series = derived_series(2, archive_age=600.0, archive_pending=0)
        self.assertEqual(self.codes(self.state(series)), [])

    def test_unknown_pending_state_keeps_the_age_guard(self) -> None:
        series = derived_series(2, archive_age=240.0, archive_pending=None)
        self.assertEqual(self.codes(self.state(series)), ["ARCHIVE_LAG"])

    def test_new_archive_failure_trips(self) -> None:
        raws = [raw_sample(1000.0, failed=0), raw_sample(1005.0, failed=1)]
        state = self.state([harness.derive(raws[0], raws[1])])
        self.assertEqual(self.codes(state), ["ARCHIVE_FAILED"])

    def test_continuous_swap_trips_after_sixty_seconds(self) -> None:
        raws = [raw_sample(1000.0 + 5 * i, swap=i) for i in range(14)]
        samples = [harness.derive(a, b) for a, b in zip(raws, raws[1:], strict=False)]
        self.assertEqual(self.codes(self.state(samples[:11])), [])
        self.assertEqual(self.codes(self.state(samples)), ["SWAP"])

    def test_other_apps_cpu_pressure_trips_only_when_sustained(self) -> None:
        self.assertEqual(self.codes(self.state(derived_series(12, other_psi=40.0))), [])  # on point não dispara
        self.assertEqual(self.codes(self.state(derived_series(12, other_psi=41.0))), ["OTHER_APPS_PSI"])
        spike = derived_series(12, other_psi=41.0)
        spike[5]["others_cpu_psi_max"] = 2.0
        self.assertEqual(self.codes(self.state(spike)), [])

    def test_stale_probe_means_running_blind(self) -> None:
        state = self.state(derived_series(2))
        self.assertEqual(self.codes(state, now=(state.last_sample_at or 0) + 31), ["PROBE_STALE"])
        self.assertEqual(self.codes(harness.GuardState(), now=0.0), ["PROBE_STALE"])

    def test_backup_failure_trips(self) -> None:
        state = self.state(derived_series(2))
        state.backup_failures = ["full comando 1 terminou com código 1"]
        self.assertEqual(self.codes(state), ["BACKUP_ERROR"])

    def test_failure_rate_needs_minimum_volume_and_five_percent(self) -> None:
        state = self.state(derived_series(2))
        now = state.last_sample_at or 0
        few = [request(now - 1, 500)] * 19
        self.assertEqual(self.codes(state, few, now), [])
        mixed = [request(now - 1, 200)] * 95 + [request(now - 1, 500)] * 3 + [request(now - 1, 0)] * 2
        self.assertEqual(self.codes(state, mixed, now), ["FAILURE_RATE"])  # 5/100: on point dispara
        self.assertEqual(self.codes(state, mixed[:-1] + [request(now - 1, 404)], now), [])  # 4/100; 4xx não é falha
        old = [request(now - 120, 500)] * 50 + [request(now - 1, 200)] * 30
        self.assertEqual(self.codes(state, old, now), [])

    def test_other_app_http_failures_and_latency(self) -> None:
        state = self.state(derived_series(2))
        state.health["stillon"] = [harness.HealthResult(1, "stillon", True, 80, 200),
                                   harness.HealthResult(2, "stillon", False, 5000, 0)]
        self.assertEqual(self.codes(state), [])
        state.health["stillon"].append(harness.HealthResult(3, "stillon", False, 5000, 502))
        self.assertEqual(self.codes(state), ["OTHER_APPS_HTTP"])
        state.health["stillon"] = [harness.HealthResult(i, "stillon", True, 600, 200) for i in range(8)]
        state.health_baseline_p95["stillon"] = 100.0  # teto = max(300, 600) -> 600 não passa
        self.assertEqual(self.codes(state), [])
        state.health["stillon"][-1] = harness.HealthResult(9, "stillon", True, 601, 200)
        self.assertEqual(self.codes(state), ["OTHER_APPS_HTTP"])

    def test_percentile_is_nearest_rank(self) -> None:
        self.assertEqual(harness.percentile([1, 2, 3, 4, 5, 6, 7, 8, 9, 10], 95), 10.0)
        self.assertEqual(harness.percentile([1, 2, 3, 4, 5, 6, 7, 8, 9, 10], 50), 5.0)
        self.assertEqual(harness.percentile([7.0], 99), 7.0)
        self.assertIsNone(harness.percentile([], 95))


class FakeClock:
    def __init__(self) -> None:
        self.t = 1000.0
        self.on_tick: list = []

    def now(self) -> float:
        return self.t

    def sleep(self, seconds: float) -> None:
        self.t += seconds
        for hook in self.on_tick:
            hook(self.t)


class FakeDriver:
    def __init__(self) -> None:
        self.calls: list[tuple] = []

    def set_clients(self, count: int) -> None:
        self.calls.append(("clients", count))

    def schedule_entries(self, within_seconds: float) -> None:
        self.calls.append(("entries", within_seconds))

    def schedule_reconnect(self, fraction: float, within_seconds: float) -> None:
        self.calls.append(("reconnect", fraction, within_seconds))

    def stop_all(self, join_seconds: float = 15.0) -> None:
        self.calls.append(("stop",))

    def last_write_ack(self) -> float | None:
        return None


class FakeBackups:
    def __init__(self, clock: FakeClock, takes: float, fail: bool = False) -> None:
        self.clock = clock
        self.takes = takes
        self.fail = fail
        self.started: list[tuple[str, tuple[str, ...]]] = []
        self.started_at = 0.0
        self.stopped = False

    def start(self, phase: str, commands) -> None:
        self.started.append((phase, tuple(commands)))
        self.started_at = self.clock.now()

    def done(self) -> bool:
        return bool(self.started) and self.clock.now() - self.started_at >= self.takes

    def failures(self) -> list[str]:
        return ["full comando 1 terminou com código 1"] if self.fail and self.done() else []

    def stop(self) -> None:
        self.stopped = True


SHORT = harness.C2Protocol(baseline_seconds=10, warmup_seconds=10, ramp=(5, 10), step_seconds=10, peak_clients=10,
                           peak_seconds=10, entry_seconds=10, reconnect_rounds=1, reconnect_seconds=10,
                           recovery_clients=5, recovery_seconds=10, backup_min_seconds=20,
                           backup_deadline_seconds=60, settle_seconds=10, slack_seconds=5)


class OrchestratorTests(unittest.TestCase):
    def build(self, takes: float = 5.0, fail: bool = False, sample_fn=None):
        clock = FakeClock()
        samples: queue.Queue = queue.Queue()
        health: queue.Queue = queue.Queue()
        sample_fn = sample_fn or (lambda t: raw_sample(t))
        clock.on_tick.append(lambda t: samples.put(sample_fn(t)) if int(t) % 5 == 0 else None)
        driver = FakeDriver()
        backups = FakeBackups(clock, takes, fail)
        events_out = io.StringIO()
        events = harness.EventLog(events_out, harness.Clock(clock.now, clock.sleep))
        recorder = load.Recorder(None, clock=clock.now)
        orchestrator = harness.Orchestrator(SHORT.phases(), SHORT, LIMITS, driver, recorder, samples, health, backups,
                                            {"full": ("full duora-c2",), "diff": ("diff duora-c2", "restic duora-c2")},
                                            events, harness.Clock(clock.now, clock.sleep))
        return orchestrator, driver, backups, events, clock

    def test_completes_all_phases_in_order_and_stops_load(self) -> None:
        orchestrator, driver, backups, events, _ = self.build(takes=5.0)
        self.assertEqual(orchestrator.run(), harness.EXIT_OK)
        ends = [e["phase"] for e in events.entries if e["event"] == "phase_end"]
        self.assertEqual(ends, [p.name for p in SHORT.phases()])
        self.assertEqual([phase for phase, _ in backups.started], ["full", "diff"])
        self.assertEqual(backups.started[1][1], ("diff duora-c2", "restic duora-c2"))
        self.assertIn(("entries", 60), driver.calls)
        self.assertIn(("reconnect", 0.5, 10), driver.calls)
        self.assertEqual(driver.calls[0], ("clients", 0))
        self.assertEqual(driver.calls[-1], ("stop",))
        self.assertTrue(backups.stopped)

    def test_backup_phase_waits_for_real_completion_beyond_minimum(self) -> None:
        orchestrator, _, _, events, _ = self.build(takes=35.0)
        self.assertEqual(orchestrator.run(), harness.EXIT_OK)
        full = next(e for e in events.entries if e["event"] == "phase_end" and e["phase"] == "full")
        self.assertGreaterEqual(full["elapsed"], 35.0)

    def test_backup_past_deadline_aborts(self) -> None:
        orchestrator, driver, _, events, _ = self.build(takes=1000.0)
        self.assertEqual(orchestrator.run(), harness.EXIT_ABORTED)
        self.assertEqual([t.code for t in orchestrator.trips], ["DEADLINE"])
        self.assertEqual(events.entries[-1]["event"], "abort")
        self.assertEqual(driver.calls[-1], ("stop",))

    def test_backup_error_aborts(self) -> None:
        orchestrator, _, _, _, _ = self.build(takes=5.0, fail=True)
        self.assertEqual(orchestrator.run(), harness.EXIT_ABORTED)
        self.assertIn("BACKUP_ERROR", [t.code for t in orchestrator.trips])

    def test_guard_trip_aborts_and_stops_load_immediately(self) -> None:
        orchestrator, driver, _, events, _ = self.build(
            sample_fn=lambda t: raw_sample(t, host_oom=1 if t >= 1040 else 0))
        self.assertEqual(orchestrator.run(), harness.EXIT_ABORTED)
        self.assertEqual([t.code for t in orchestrator.trips], ["OOM"])
        trip = next(e for e in events.entries if e["event"] == "guard_trip")
        self.assertEqual(trip["code"], "OOM")
        self.assertEqual(driver.calls[-1], ("stop",))

    def test_run_past_the_window_aborts_even_if_each_phase_is_on_time(self) -> None:
        orchestrator, driver, _, events, clock = self.build(takes=15.0)
        orchestrator._protocol = replace(SHORT, window_seconds=50)
        started = clock.now()
        self.assertEqual(orchestrator.run(), harness.EXIT_ABORTED)
        self.assertEqual([t.code for t in orchestrator.trips], ["WINDOW"])
        self.assertLessEqual(clock.now() - started, 51.0)
        self.assertEqual(events.entries[-1]["event"], "abort")
        self.assertEqual(driver.calls[-1], ("stop",))

    def test_health_baseline_is_frozen_after_baseline_phase(self) -> None:
        orchestrator, _, _, events, _ = self.build()
        for index in range(3):
            orchestrator._health.put(harness.HealthResult(1000 + index, "stillon", True, 100 + index, 200))
        orchestrator.run()
        baseline = next(e for e in events.entries if e["event"] == "health_baseline")
        self.assertEqual(baseline["p95_ms"], {"stillon": 102.0})


class ConfigTests(unittest.TestCase):
    def write(self, directory: Path, **overrides: Any) -> Path:
        config = {
            "schema": harness.CONFIG_SCHEMA, "project": "duora-c2",
            "target": {"connect_host": "127.0.0.1", "connect_port": 18443, "server_name": "c2.duora.test",
                       "ca_file": "c2-root.crt"},
            "accounts_file": "accounts.json", "ssh": ["ssh", "-o", "BatchMode=yes", "c2vps"],
            "probe_script": "c2_probe.py", "extra_cgroups": ["duora-media.slice"],
            "backup_commands": {"full": ["sudo duora_backup.py full --compose-project duora-c2"],
                                "diff": ["sudo duora_backup.py diff --compose-project duora-c2"]},
            "external_health": [{"name": "stillon", "url": "https://stillon.example/health"}],
            "limits": {"failure_abort_ratio": 0.1},
        }
        config.update(overrides)
        path = directory / "c2.json"
        path.write_text(json.dumps(config))
        return path

    def test_valid_config_resolves_paths_and_limit_overrides(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            config = harness.load_config(self.write(Path(directory)))
            self.assertEqual(config.target.ca_file, str(Path(directory) / "c2-root.crt"))
            self.assertEqual(config.limits.failure_abort_ratio, 0.1)
            self.assertEqual(config.limits.mem_available_min_mib, 512.0)
            command = harness.probe_remote_command(config, 600)
            self.assertEqual(command, "nice -n 19 python3 -I - sample --project duora-c2 --interval 5 --duration 600 "
                                      "--extra-cgroup duora-media.slice")

    def test_unsafe_configs_are_refused(self) -> None:
        cases = {
            "produção": {"project": "duora"},
            "carga direta no domínio": {"target": {"connect_host": "meetduora.com", "connect_port": 443,
                                                   "server_name": "meetduora.com", "ca_file": "x"}},
            "sem FULL": {"backup_commands": {"full": [], "diff": ["diff duora-c2"]}},
            "backup de outro projeto": {"backup_commands": {"full": ["full --compose-project duora"],
                                                            "diff": ["diff duora-c2"]}},
            "sem sonda dos outros apps": {"external_health": []},
            "sonda sem https": {"external_health": [{"name": "x", "url": "http://x"}]},
            "sonda do exemplo": {"external_health": [{"name": "x", "url": "https://REPLACE-stillon/health"}]},
            "ssh trocado": {"ssh": ["bash", "-c"]},
            "schema errado": {"schema": "outro"},
        }
        for name, override in cases.items():
            with self.subTest(name), tempfile.TemporaryDirectory() as directory:
                with self.assertRaises((harness.ConfigError, ValueError)):
                    harness.load_config(self.write(Path(directory), **override))

    def test_example_config_is_valid_once_markers_are_filled(self) -> None:
        example = Path(harness.__file__).parent / "c2" / "c2.example.json"
        with tempfile.TemporaryDirectory() as directory:
            with self.assertRaises(harness.ConfigError):
                harness.load_config(example)
            filled = Path(directory) / "c2" / "c2.json"
            filled.parent.mkdir()
            filled.write_text(example.read_text().replace("REPLACE-", ""))
            config = harness.load_config(filled)
            self.assertEqual(config.project, "duora-c2")
            self.assertEqual(len(config.backup_commands["diff"]), 2)
            self.assertTrue(all("duora-c2" in c for c in config.abort_commands))

    def test_abort_command_for_another_project_is_refused(self) -> None:
        with tempfile.TemporaryDirectory() as directory, self.assertRaises(harness.ConfigError):
            harness.load_config(self.write(Path(directory), abort_commands=["docker compose -p duora stop"]))

    def test_unknown_limit_is_refused(self) -> None:
        with tempfile.TemporaryDirectory() as directory, self.assertRaises(TypeError):
            harness.load_config(self.write(Path(directory), limits={"mem_min": 1}))

    def test_runnable_requires_enough_synthetic_accounts_and_valid_repetition(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            base = Path(directory)
            (base / "c2_probe.py").write_text("# sampler")
            accounts = [{"email": f"p{i}@c2.invalid", "password": "x"} for i in range(99)]
            (base / "accounts.json").write_text(json.dumps({"accounts": accounts}))
            config = harness.load_config(self.write(base))
            with self.assertRaises(harness.ConfigError):
                harness.check_runnable(config, 1, harness.C2Protocol())
            accounts.append({"email": "p99@c2.invalid", "password": "x"})
            (base / "accounts.json").write_text(json.dumps({"accounts": accounts}))
            self.assertEqual(len(harness.check_runnable(config, 3, harness.C2Protocol())), 100)
            for repetition in (0, 4):
                with self.subTest(repetition=repetition), self.assertRaises(harness.ConfigError):
                    harness.check_runnable(config, repetition, harness.C2Protocol())


class WindowRunnableTests(unittest.TestCase):
    def test_window_profile_accepts_only_repetition_one(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            base = Path(directory)
            (base / "c2_probe.py").write_text("# sampler")
            accounts = [{"email": f"p{i}@c2.invalid", "password": "x"} for i in range(100)]
            (base / "accounts.json").write_text(json.dumps({"accounts": accounts}))
            config = harness.load_config(ConfigTests().write(base))
            self.assertEqual(len(harness.check_runnable(config, 1, harness.PROFILES["janela"])), 100)
            with self.assertRaises(harness.ConfigError):
                harness.check_runnable(config, 2, harness.PROFILES["janela"])


class ReportTests(unittest.TestCase):
    def evidence(self, directory: Path, *, exit_code: int, read_ms: float, statuses: list[int],
                 jobs_after: int = 0) -> Path:
        manifest = {"schema": harness.MANIFEST_SCHEMA, "repetition": 1, "commit": "abc", "exit_code": exit_code,
                    "limits": {}, "pending_flows": ["mensagens", "SignalR"]}
        (directory / "manifest.json").write_text(json.dumps(manifest))
        events = [{"t": 1000.0, "event": "phase_start", "phase": "baseline"},
                  {"t": 1030.0, "event": "phase_end", "phase": "baseline"},
                  {"t": 1030.0, "event": "phase_start", "phase": "peak"},
                  {"t": 1060.0, "event": "phase_end", "phase": "peak"},
                  {"t": 1060.0, "event": "phase_start", "phase": "recovery"},
                  {"t": 1150.0, "event": "phase_end", "phase": "recovery"},
                  {"t": 1100.0, "event": "backup_end", "phase": "full", "index": 1, "exit_code": 0, "seconds": 40.0}]
        (directory / "events.jsonl").write_text("\n".join(json.dumps(e) for e in events) + "\n")
        requests = [load.RequestRecord(1040.0 + i * 0.1, "me", "read", status, read_ms, "peak")
                    for i, status in enumerate(statuses)]
        requests += [load.RequestRecord(1040.0, "csrf", "login", 204, 12.0, "peak"),
                     load.RequestRecord(1041.0, "time-zone-put", "command", 200, 100.0, "peak")]
        (directory / "requests.jsonl").write_text("\n".join(r.to_json() for r in requests) + "\n")
        samples = [raw_sample(1000.0 + 5 * i, jobs_due=jobs_after if 1060.0 + 5 * i > 1150 - 60 else 0)
                   for i in range(31)]
        (directory / "samples.jsonl").write_text("\n".join(json.dumps(s) for s in samples) + "\n")
        return directory

    def test_passing_run_is_only_a_candidate(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            report = harness.build_report(self.evidence(Path(directory), exit_code=0, read_ms=120.0,
                                                        statuses=[200] * 100))
            self.assertEqual(report["verdict"], "PASS_CANDIDATE")
            peak = next(p for p in report["phases"] if p["phase"] == "peak")
            self.assertEqual(peak["requests"]["kinds"]["read"]["p95_ms"], 120.0)
            self.assertEqual(peak["requests"]["rtt_floor_ms"], 12.0)
            self.assertEqual(peak["gates"], {"read_p95": True, "command_p95": True, "reconcile_p95": None,
                                             "failure_ratio": True, "no_oom": True})
            recovery = next(p for p in report["phases"] if p["phase"] == "recovery")
            self.assertTrue(recovery["gates"]["queues_back_to_baseline"])
            self.assertEqual(len(report["content_sha256"]), 64)
            markdown = harness.render_markdown(report)
            self.assertIn("PASS_CANDIDATE", markdown)
            self.assertIn("mensagens, SignalR", markdown)

    def test_report_carries_profile_and_declared_reductions(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            evidence = self.evidence(Path(directory), exit_code=0, read_ms=120.0, statuses=[200] * 100)
            manifest = json.loads((evidence / "manifest.json").read_text())
            manifest.update({"profile": "janela", "deviations": ["3 ciclos no lugar de 3 repetições"]})
            (evidence / "manifest.json").write_text(json.dumps(manifest))
            report = harness.build_report(evidence)
            self.assertEqual((report["profile"], report["deviations"]),
                             ("janela", ["3 ciclos no lugar de 3 repetições"]))
            markdown = harness.render_markdown(report)
            self.assertIn("perfil `janela`", markdown)
            self.assertIn("- 3 ciclos no lugar de 3 repetições", markdown)

    def test_report_of_old_manifest_defaults_to_full_profile(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            report = harness.build_report(self.evidence(Path(directory), exit_code=0, read_ms=120.0,
                                                        statuses=[200] * 100))
            self.assertEqual((report["profile"], report["deviations"]), ("completo", []))

    def test_slow_reads_and_failures_fail_the_gates(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            report = harness.build_report(self.evidence(Path(directory), exit_code=0, read_ms=251.0,
                                                        statuses=[200] * 98 + [500, 0]))
            peak = next(p for p in report["phases"] if p["phase"] == "peak")
            self.assertFalse(peak["gates"]["read_p95"])
            self.assertFalse(peak["gates"]["failure_ratio"])  # 2/102 >= 1%
            self.assertEqual(report["verdict"], "FAIL")

    def test_queue_not_drained_after_recovery_fails(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            report = harness.build_report(self.evidence(Path(directory), exit_code=0, read_ms=100.0,
                                                        statuses=[200] * 100, jobs_after=5))
            recovery = next(p for p in report["phases"] if p["phase"] == "recovery")
            self.assertFalse(recovery["gates"]["queues_back_to_baseline"])
            self.assertEqual(report["verdict"], "FAIL")

    def test_aborted_run_is_never_a_candidate(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            report = harness.build_report(self.evidence(Path(directory), exit_code=3, read_ms=10.0,
                                                        statuses=[200] * 100))
            self.assertEqual(report["verdict"], "ABORTED")


class RestoreObjectiveTests(unittest.TestCase):
    def test_rpo_and_rto_are_computed_against_targets(self) -> None:
        result = harness.restore_objectives(last_ack_at=1000.0, restored_max_at=700.0, outage_at=1010.0,
                                            ready_at=8210.0)
        self.assertEqual(result, {"rpo_seconds": 300.0, "rto_seconds": 7200.0, "rpo_ok": True, "rto_ok": True})
        late = harness.restore_objectives(last_ack_at=1000.0, restored_max_at=699.0, outage_at=1010.0,
                                          ready_at=8211.0)
        self.assertEqual((late["rpo_ok"], late["rto_ok"]), (False, False))

    def test_inconsistent_instants_are_refused(self) -> None:
        with self.assertRaises(ValueError):
            harness.restore_objectives(last_ack_at=1000.0, restored_max_at=1001.0, outage_at=1010.0, ready_at=1100.0)


class LimitsTests(unittest.TestCase):
    def test_defaults_match_capacity_protocol(self) -> None:
        self.assertEqual((LIMITS.mem_available_min_mib, LIMITS.disk_max_percent, LIMITS.archive_lag_max_seconds),
                         (512.0, 85.0, 240.0))
        self.assertEqual((LIMITS.read_p95_ms, LIMITS.command_p95_ms, LIMITS.reconcile_p95_ms,
                          LIMITS.failure_max_ratio), (250.0, 300.0, 1000.0, 0.01))
        self.assertEqual(replace(LIMITS, disk_max_percent=80.0).disk_max_percent, 80.0)


if __name__ == "__main__":
    unittest.main()


class InstantBackups:
    def __init__(self) -> None:
        self.started: list[str] = []

    def start(self, phase: str, commands) -> None:
        self.started.append(phase)

    def done(self) -> bool:
        return True

    def failures(self) -> list[str]:
        return []

    def stop(self) -> None:
        return None


class EndToEndAgainstFakeServer(unittest.TestCase):
    """Orquestrador real + LoadDriver real + servidor falso no loopback, com fases de segundos."""

    def test_short_run_records_every_kind_and_report_builds(self) -> None:
        import threading
        from http.server import ThreadingHTTPServer

        import test_c2_load

        fake = test_c2_load.FakeDuora()
        server = ThreadingHTTPServer(("127.0.0.1", 0), fake.handler())
        threading.Thread(target=server.serve_forever, daemon=True).start()
        protocol = harness.C2Protocol(entry_window_seconds=1, reconnect_fraction=1.0, reconnect_window_seconds=1)
        phases = [harness.Phase("baseline", "baseline", 0, 1, 10), harness.Phase("peak", "steady", 2, 3, 10),
                  harness.Phase("entry", "entry", 2, 3, 10), harness.Phase("reconnect-1", "reconnect", 2, 3, 10),
                  harness.Phase("full", "backup", 2, 1, 10, backup="full")]
        target = load.Target("127.0.0.1", server.server_address[1], "c2.duora.test", None, tls=False)
        accounts = [load.Account("p1@c2.invalid", "frase sintética")] * 2
        with tempfile.TemporaryDirectory() as directory:
            evidence = Path(directory)
            requests_out = (evidence / "requests.jsonl").open("w")
            events_out = (evidence / "events.jsonl").open("w")
            samples_out = (evidence / "samples.jsonl").open("w")
            clock = harness.Clock()
            events = harness.EventLog(events_out, clock)
            recorder = load.Recorder(requests_out)
            samples: queue.Queue = queue.Queue()
            for index in range(3):
                samples.put(raw_sample(clock.now() - 10 + 5 * index))
            driver = load.LoadDriver(target, accounts, recorder, seed=1)
            orchestrator = harness.Orchestrator(phases, protocol, LIMITS, driver, recorder, samples, queue.Queue(),
                                                InstantBackups(), {"full": ("x duora-c2",), "diff": ()}, events,
                                                clock, samples_out)
            try:
                code = orchestrator.run()
            finally:
                server.shutdown()
                server.server_close()
            for handle in (requests_out, events_out, samples_out):
                handle.close()
            (evidence / "manifest.json").write_text(json.dumps({"repetition": 1, "commit": "x", "exit_code": code,
                                                                "limits": {}, "pending_flows": ["mensagens"]}))
            report = harness.build_report(evidence)
            kinds = {json.loads(line)["kind"] for line in (evidence / "requests.jsonl").read_text().splitlines()}
        self.assertEqual(code, harness.EXIT_OK)
        self.assertTrue({"login", "logout", "read", "command", "reconcile"} <= kinds)
        self.assertEqual([p["phase"] for p in report["phases"]], ["baseline", "peak", "entry", "reconnect-1", "full"])
        self.assertIn(report["verdict"], ("PASS_CANDIDATE", "FAIL"))
