"""Harness C2 da issue #86: protocolo, guardas de aborto, orquestração e relatório (docs/operations/capacity-c2.md).

Roda no Mac. Gera a carga sintética (c2_load) contra o projeto sombra `duora-c2` pelo túnel SSH, lê o sampler
somente leitura da VPS (c2_probe, por stdin), dispara as fases de backup FULL e DIFF na VPS e para tudo na hora em
que um guarda dispara, inclusive quando os outros apps da VPS degradam. Toda decisão sai em evidência JSONL.

    python3 scripts/deploy/c2_harness.py plan
    python3 scripts/deploy/c2_harness.py run --config c2.json --repetition 1 --evidence evidence/
    python3 scripts/deploy/c2_harness.py report evidence/c2-<utc>-rep1

Códigos de saída: 0 concluído, 2 configuração inválida, 3 abortado por guarda, 4 interrompido pelo operador.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import math
import queue
import shlex
import ssl
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.request
from collections.abc import Callable, Iterable, Sequence
from dataclasses import asdict, dataclass, field, replace
from datetime import UTC, datetime
from pathlib import Path
from typing import Any, Protocol, TextIO

import c2_load
import c2_probe

CONFIG_SCHEMA = "duora.c2-config/v1"
MANIFEST_SCHEMA = "duora.c2-run/v1"
REPORT_SCHEMA = "duora.c2-report/v1"
REPETITIONS = 3
SAMPLE_SECONDS = 5.0
HEALTH_SECONDS = 15.0
HEALTH_TIMEOUT_SECONDS = 5.0
PROBE_BUFFER_SECONDS = 2 * 3600
EXIT_OK, EXIT_CONFIG, EXIT_ABORTED, EXIT_INTERRUPTED = 0, 2, 3, 4
SECTOR_BYTES = 512
MIB = 1024 * 1024
PERCENTILES = (50, 95, 99)
FAILURE_CLASSES = frozenset(("server", "transport"))


# ---------------------------------------------------------------- protocolo


@dataclass(frozen=True)
class Phase:
    """Uma fase da repetição. `seconds` é o mínimo; fase de backup só termina com o backup concluído."""

    name: str
    kind: str  # baseline | steady | entry | reconnect | backup
    clients: int
    seconds: int
    deadline_seconds: int
    backup: str | None = None

    def __post_init__(self) -> None:
        if self.deadline_seconds < self.seconds:
            raise ValueError(f"fase {self.name}: prazo menor que a duração")


@dataclass(frozen=True)
class C2Protocol:
    """docs/operations/capacity.md: 5 min de aquecimento, degraus de 5 min, pico de 30 min, entrada/reconexão,
    recuperação de 15 min e contenção com FULL e DIFF em fases distintas, cada uma com no mínimo 30 min."""

    baseline_seconds: int = 120
    warmup_clients: int = 10
    warmup_seconds: int = 300
    ramp: tuple[int, ...] = (10, 25, 50, 100)
    step_seconds: int = 300
    peak_clients: int = 100
    peak_seconds: int = 1800
    entry_window_seconds: int = 60
    entry_seconds: int = 120
    reconnect_rounds: int = 3
    reconnect_fraction: float = 0.5
    reconnect_window_seconds: int = 10
    reconnect_seconds: int = 60
    recovery_clients: int = 10
    recovery_seconds: int = 900
    backup_min_seconds: int = 1800
    backup_deadline_seconds: int = 5400
    settle_seconds: int = 600
    slack_seconds: int = 120

    def phases(self) -> list[Phase]:
        def fixed(name: str, kind: str, clients: int, seconds: int) -> Phase:
            return Phase(name, kind, clients, seconds, seconds + self.slack_seconds)

        phases = [fixed("baseline", "baseline", 0, self.baseline_seconds),
                  fixed("warmup", "steady", self.warmup_clients, self.warmup_seconds)]
        phases += [fixed(f"ramp-{clients}", "steady", clients, self.step_seconds) for clients in self.ramp]
        phases.append(fixed("peak", "steady", self.peak_clients, self.peak_seconds))
        phases.append(fixed("entry", "entry", self.peak_clients, self.entry_seconds))
        phases += [fixed(f"reconnect-{index}", "reconnect", self.peak_clients, self.reconnect_seconds)
                   for index in range(1, self.reconnect_rounds + 1)]
        phases.append(fixed("recovery", "steady", self.recovery_clients, self.recovery_seconds))
        phases.append(Phase("full", "backup", self.peak_clients, self.backup_min_seconds,
                            self.backup_deadline_seconds, backup="full"))
        phases.append(fixed("settle", "steady", self.recovery_clients, self.settle_seconds))
        phases.append(Phase("diff", "backup", self.peak_clients, self.backup_min_seconds,
                            self.backup_deadline_seconds, backup="diff"))
        phases.append(fixed("recovery-final", "steady", self.recovery_clients, self.recovery_seconds))
        return phases


def nominal_seconds(phases: Sequence[Phase]) -> int:
    return sum(phase.seconds for phase in phases)


def deadline_seconds(phases: Sequence[Phase]) -> int:
    return sum(phase.deadline_seconds for phase in phases)


# ---------------------------------------------------------------- limites e guardas


@dataclass(frozen=True)
class Limits:
    """Gatilhos de aborto (capacity.md "Interromper a escalada") e critérios propostos de aprovação."""

    mem_available_min_mib: float = 512.0
    mem_low_seconds: float = 60.0
    swap_seconds: float = 60.0
    disk_max_percent: float = 85.0
    archive_lag_max_seconds: float = 240.0
    failure_abort_ratio: float = 0.05
    failure_window_seconds: float = 60.0
    failure_min_requests: int = 20
    # Outros apps: pressão de CPU no cgroup deles (tempo esperando CPU) e sondas HTTP externas.
    other_cpu_psi_max: float = 40.0
    other_psi_seconds: float = 60.0
    health_consecutive_failures: int = 2
    health_latency_factor: float = 3.0
    health_latency_floor_ms: float = 500.0
    health_latency_window: int = 8
    probe_stale_seconds: float = 30.0
    # Aprovação (spec 19.1): não abortam; entram no veredito do relatório.
    read_p95_ms: float = 250.0
    command_p95_ms: float = 300.0
    reconcile_p95_ms: float = 1000.0
    failure_max_ratio: float = 0.01
    # Investigação (não é falha isolada): CPU > 85% por 5 min ou throttling em > 10% dos períodos.
    investigate_busy_percent: float = 85.0
    investigate_busy_seconds: float = 300.0
    investigate_throttle_ratio: float = 0.10


@dataclass(frozen=True)
class Trip:
    code: str
    detail: str


def derive(previous: dict[str, Any], current: dict[str, Any]) -> dict[str, Any]:
    """Taxas e deltas entre duas amostras brutas do sampler. Contador ausente vira None, nunca zero."""
    dt = float(current["t"]) - float(previous["t"])
    if dt <= 0:
        raise ValueError("amostras fora de ordem")
    host_now, host_before = current["host"], previous["host"]
    derived: dict[str, Any] = {"t": current["t"], "dt": dt, **_cpu_percentages(host_before.get("cpu"),
                                                                                    host_now.get("cpu"))}
    derived["mem_available_mib"] = host_now.get("mem_available_mib")
    vm_now, vm_before = host_now.get("vmstat") or {}, host_before.get("vmstat") or {}
    derived["swap_pages"] = _delta_sum(vm_before, vm_now, ("pswpin", "pswpout"))
    host_oom = _delta_sum(vm_before, vm_now, ("oom_kill",))
    psi = host_now.get("psi") or {}
    derived["psi"] = {kind: (psi.get(kind) or {}) for kind in ("cpu", "memory", "io")}
    filesystems = host_now.get("filesystems") or {}
    derived["fs_max_percent"] = max(filesystems.values()) if filesystems else None
    derived["disks"] = _disk_rates(host_before.get("disks") or {}, host_now.get("disks") or {}, dt)
    cgroups, cgroup_oom = _cgroup_rates(previous.get("cgroups") or {}, current.get("cgroups") or {}, dt)
    derived["cgroups"] = cgroups
    derived["oom_kills"] = (host_oom or 0) + cgroup_oom
    others = [values["cpu_psi"] for values in cgroups.values() if values["owner"] == "other"
              and values["cpu_psi"] is not None]
    derived["others_cpu_psi_max"] = max(others) if others else None
    derived["database"] = _database_rates(previous.get("database"), current.get("database"))
    return derived


def _cpu_percentages(before: dict[str, int] | None, now: dict[str, int] | None) -> dict[str, float | None]:
    if not before or not now:
        return {"busy_pct": None, "steal_pct": None, "iowait_pct": None}
    delta = {key: now[key] - before[key] for key in now}
    total = sum(delta.values())
    if total <= 0:
        return {"busy_pct": None, "steal_pct": None, "iowait_pct": None}
    busy = delta["user"] + delta["nice"] + delta["system"] + delta["irq"] + delta["softirq"]
    return {"busy_pct": round(100.0 * busy / total, 2), "steal_pct": round(100.0 * delta["steal"] / total, 2),
            "iowait_pct": round(100.0 * delta["iowait"] / total, 2)}


def _delta_sum(before: dict[str, Any], now: dict[str, Any], keys: Iterable[str]) -> int | None:
    total = 0
    for key in keys:
        if now.get(key) is None or before.get(key) is None:
            return None
        total += max(0, int(now[key]) - int(before[key]))
    return total


def _disk_rates(before: dict[str, dict[str, int]], now: dict[str, dict[str, int]], dt: float) -> dict[str, Any]:
    rates = {}
    for name, values in now.items():
        old = before.get(name)
        if old is None:
            continue
        ios = (values["reads"] - old["reads"]) + (values["writes"] - old["writes"])
        rates[name] = {
            "read_mib_s": round((values["read_sectors"] - old["read_sectors"]) * SECTOR_BYTES / MIB / dt, 3),
            "write_mib_s": round((values["write_sectors"] - old["write_sectors"]) * SECTOR_BYTES / MIB / dt, 3),
            "iops": round(ios / dt, 2),
            "util_pct": round(min(100.0, 100.0 * (values["io_ms"] - old["io_ms"]) / (dt * 1000)), 2),
            "await_ms": round((values["weighted_ms"] - old["weighted_ms"]) / ios, 2) if ios > 0 else 0.0,
        }
    return rates


def _cgroup_rates(before: dict[str, Any], now: dict[str, Any], dt: float) -> tuple[dict[str, Any], int]:
    rates: dict[str, Any] = {}
    oom_total = 0
    for name, values in now.items():
        old = before.get(name) or {}
        usage = _counter_delta(old, values, "usage_usec")
        periods = _counter_delta(old, values, "nr_periods")
        throttled = _counter_delta(old, values, "nr_throttled")
        oom = _counter_delta(old, values, "oom_kill") or 0
        oom_total += oom
        io_bytes = [_counter_delta(old, values, key) for key in ("io_rbytes", "io_wbytes")]
        rates[name] = {
            "owner": values.get("owner"),
            "cpu_pct": None if usage is None else round(100.0 * usage / (dt * 1_000_000), 2),
            "throttled_ratio": round(throttled / periods, 4) if periods and throttled is not None else 0.0,
            "memory_mib": values.get("memory_current_mib"),
            "memory_peak_mib": values.get("memory_peak_mib"),
            "oom_kills": oom,
            "cpu_psi": values.get("cpu_psi_some_avg10"),
            "memory_psi": values.get("memory_psi_some_avg10"),
            "io_psi": values.get("io_psi_some_avg10"),
            "io_mib_s": None if None in io_bytes else round(sum(b for b in io_bytes if b) / MIB / dt, 3),
        }
    return rates, oom_total


def _counter_delta(before: dict[str, Any], now: dict[str, Any], key: str) -> int | None:
    if now.get(key) is None or before.get(key) is None:
        return None
    return max(0, int(now[key]) - int(before[key]))


def _database_rates(before: dict[str, Any] | None, now: dict[str, Any] | None) -> dict[str, Any] | None:
    if now is None:
        return None
    failed = None
    if before is not None and before.get("failed_count") is not None and now.get("failed_count") is not None:
        failed = max(0, now["failed_count"] - before["failed_count"])
    return {"archive_lag_s": now.get("last_archived_age_s"), "archive_failed": failed,
            "jobs_due": now.get("jobs_due"), "jobs_oldest_due_age_s": now.get("jobs_oldest_due_age_s"),
            "jobs_max_lateness_s": now.get("jobs_max_lateness_s"), "outbox_pending": now.get("outbox_pending")}


@dataclass
class HealthResult:
    t: float
    name: str
    ok: bool
    ms: float
    status: int


@dataclass
class GuardState:
    samples: list[dict[str, Any]] = field(default_factory=list)
    last_sample_at: float | None = None
    health: dict[str, list[HealthResult]] = field(default_factory=dict)
    health_baseline_p95: dict[str, float] = field(default_factory=dict)
    backup_failures: list[str] = field(default_factory=list)


def evaluate_guards(state: GuardState, requests: Sequence[c2_load.RequestRecord], now: float,
                    limits: Limits) -> list[Trip]:
    """Todos os guardas que dispararam agora. Qualquer um aborta a repetição."""
    trips: list[Trip] = []
    if state.last_sample_at is None or now - state.last_sample_at > limits.probe_stale_seconds:
        trips.append(Trip("PROBE_STALE", "sem amostra da VPS: execução às cegas"))
    samples = state.samples
    if samples:
        latest = samples[-1]
        trips += _sustained(samples, limits.mem_low_seconds, "MEM_LOW",
                            lambda s: s.get("mem_available_mib") is not None
                            and s["mem_available_mib"] < limits.mem_available_min_mib,
                            f"MemAvailable < {limits.mem_available_min_mib} MiB")
        trips += _sustained(samples, limits.swap_seconds, "SWAP", lambda s: (s.get("swap_pages") or 0) > 0,
                            "swap contínuo")
        trips += _sustained(samples, limits.other_psi_seconds, "OTHER_APPS_PSI",
                            lambda s: s.get("others_cpu_psi_max") is not None
                            and s["others_cpu_psi_max"] > limits.other_cpu_psi_max,
                            f"PSI de CPU de outro app > {limits.other_cpu_psi_max}")
        if latest.get("oom_kills"):
            trips.append(Trip("OOM", f"{latest['oom_kills']} OOM kill(s) no intervalo"))
        if latest.get("fs_max_percent") is not None and latest["fs_max_percent"] >= limits.disk_max_percent:
            trips.append(Trip("DISK", f"disco em {latest['fs_max_percent']}%"))
        database = latest.get("database")
        if database is not None:
            lag = database.get("archive_lag_s")
            if lag is not None and lag >= limits.archive_lag_max_seconds:
                trips.append(Trip("ARCHIVE_LAG", f"último WAL arquivado há {lag:.0f} s"))
            if database.get("archive_failed"):
                trips.append(Trip("ARCHIVE_FAILED", f"{database['archive_failed']} falha(s) de archive"))
    trips += [Trip("BACKUP_ERROR", detail) for detail in state.backup_failures]
    trips += _failure_rate_trip(requests, now, limits)
    trips += _health_trips(state, limits)
    return trips


def _sustained(samples: Sequence[dict[str, Any]], seconds: float, code: str,
               condition: Callable[[dict[str, Any]], bool], detail: str) -> list[Trip]:
    """Dispara quando a condição vale em todas as amostras que cobrem os últimos `seconds`."""
    covered = 0.0
    for sample in reversed(samples):
        if not condition(sample):
            return []
        covered += float(sample.get("dt") or 0)
        if covered >= seconds:
            return [Trip(code, f"{detail} por {seconds:.0f} s")]
    return []


def _failure_rate_trip(requests: Sequence[c2_load.RequestRecord], now: float, limits: Limits) -> list[Trip]:
    window = [r for r in requests if r.t >= now - limits.failure_window_seconds]
    if len(window) < limits.failure_min_requests:
        return []
    failures = sum(1 for r in window if c2_load.classify(r.status) in FAILURE_CLASSES)
    ratio = failures / len(window)
    if ratio >= limits.failure_abort_ratio:
        return [Trip("FAILURE_RATE", f"{ratio:.1%} de 5xx/timeout em {len(window)} requisições")]
    return []


def _health_trips(state: GuardState, limits: Limits) -> list[Trip]:
    trips = []
    for name, history in state.health.items():
        tail = history[-limits.health_consecutive_failures:]
        if len(tail) == limits.health_consecutive_failures and not any(result.ok for result in tail):
            trips.append(Trip("OTHER_APPS_HTTP", f"{name}: {len(tail)} sondas seguidas falharam"))
            continue
        baseline = state.health_baseline_p95.get(name)
        recent = [result.ms for result in history[-limits.health_latency_window:] if result.ok]
        if baseline is None or len(recent) < limits.health_latency_window:
            continue
        ceiling = max(baseline * limits.health_latency_factor, baseline + limits.health_latency_floor_ms)
        observed = percentile(recent, 95)
        if observed is not None and observed > ceiling:
            trips.append(Trip("OTHER_APPS_HTTP", f"{name}: p95 {observed:.0f} ms > teto {ceiling:.0f} ms "
                                                 f"(base {baseline:.0f} ms)"))
    return trips


def percentile(values: Sequence[float], rank: int) -> float | None:
    """Nearest-rank: o valor observado na posição ceil(rank% de n). Nada de interpolação inventada."""
    if not values:
        return None
    ordered = sorted(values)
    index = max(0, math.ceil(rank / 100 * len(ordered)) - 1)
    return float(ordered[index])


# ---------------------------------------------------------------- configuração


class ConfigError(ValueError):
    pass


@dataclass(frozen=True)
class HealthTarget:
    name: str
    url: str


@dataclass(frozen=True)
class Config:
    project: str
    target: c2_load.Target
    accounts_file: Path
    ssh: tuple[str, ...]
    probe_script: Path
    extra_cgroups: tuple[str, ...]
    backup_commands: dict[str, tuple[str, ...]]
    health: tuple[HealthTarget, ...]
    seed: int
    limits: Limits
    abort_commands: tuple[str, ...] = ()


def load_config(path: Path) -> Config:
    data = json.loads(path.read_text(encoding="utf-8"))
    if data.get("schema") != CONFIG_SCHEMA:
        raise ConfigError(f"schema esperado {CONFIG_SCHEMA}")
    try:
        project = c2_probe.validate_project(str(data["project"]))
    except c2_probe.CommandRefused as error:
        raise ConfigError(str(error)) from error
    base = path.parent
    target_data = data["target"]
    ca_file = target_data.get("ca_file")
    target = c2_load.Target(str(target_data["connect_host"]), int(target_data["connect_port"]),
                            str(target_data["server_name"]), str(base / ca_file) if ca_file else None)
    if target.connect_host not in c2_load.LOOPBACK_HOSTS:
        raise ConfigError("a carga só sai pelo túnel no loopback; nunca direto ao domínio público")
    backups = {name: tuple(str(command) for command in data["backup_commands"].get(name, []))
               for name in ("full", "diff")}
    for name, commands in backups.items():
        if not commands:
            raise ConfigError(f"backup_commands.{name} vazio: a fase {name} exige processamento real")
        for command in commands:
            if project not in command:
                raise ConfigError(f"comando de backup {name} não cita o projeto {project}: {command}")
    limits = replace(Limits(), **data.get("limits", {}))
    ssh = tuple(str(part) for part in data["ssh"])
    if not ssh or ssh[0] != "ssh":
        raise ConfigError("ssh precisa começar com o executável ssh")
    health = tuple(HealthTarget(str(item["name"]), str(item["url"])) for item in data.get("external_health", []))
    if not health:
        raise ConfigError("external_health vazio: o guarda dos outros apps exige ao menos uma sonda")
    for item in health:
        if not item.url.startswith("https://") or "REPLACE" in item.url:
            raise ConfigError(f"sonda {item.name} sem https ou ainda com o marcador REPLACE do exemplo")
    abort_commands = tuple(str(c) for c in data.get("abort_commands", []))
    for command in abort_commands:
        if project not in command:
            raise ConfigError(f"comando de aborto não cita o projeto {project}: {command}")
    return Config(project=project, target=target, accounts_file=base / str(data["accounts_file"]), ssh=ssh,
                  probe_script=base / str(data.get("probe_script", "c2_probe.py")),
                  extra_cgroups=tuple(str(c) for c in data.get("extra_cgroups", [])), backup_commands=backups,
                  health=health, seed=int(data.get("seed", 86)), limits=limits, abort_commands=abort_commands)


def probe_remote_command(config: Config, duration_seconds: int) -> str:
    extras = " ".join(f"--extra-cgroup {shlex.quote(c)}" for c in config.extra_cgroups)
    return (f"nice -n 19 python3 -I - sample --project {shlex.quote(config.project)} --interval {SAMPLE_SECONDS:g} "
            f"--duration {duration_seconds} {extras}").strip()


# ---------------------------------------------------------------- orquestração


class Driver(Protocol):
    def set_clients(self, count: int) -> None: ...

    def schedule_entries(self, within_seconds: float) -> None: ...

    def schedule_reconnect(self, fraction: float, within_seconds: float) -> None: ...

    def stop_all(self, join_seconds: float = 15.0) -> None: ...

    def last_write_ack(self) -> float | None: ...


class BackupRunner(Protocol):
    def start(self, phase: str, commands: Sequence[str]) -> None: ...

    def done(self) -> bool: ...

    def failures(self) -> list[str]: ...

    def stop(self) -> None: ...


@dataclass
class Clock:
    now: Callable[[], float] = time.time
    sleep: Callable[[float], None] = time.sleep


class EventLog:
    def __init__(self, out: TextIO, clock: Clock) -> None:
        self._out = out
        self._clock = clock
        self._lock = threading.Lock()
        self.entries: list[dict[str, Any]] = []

    def write(self, event: str, **fields: Any) -> None:
        entry = {"t": round(self._clock.now(), 3), "event": event, **fields}
        with self._lock:
            self.entries.append(entry)
            self._out.write(json.dumps(entry, ensure_ascii=False, sort_keys=True) + "\n")
            self._out.flush()


class Orchestrator:
    """Executa as fases em ordem; a cada segundo drena amostras e avalia os guardas."""

    def __init__(self, phases: Sequence[Phase], protocol: C2Protocol, limits: Limits, driver: Driver,
                 recorder: c2_load.Recorder, samples: queue.Queue[dict[str, Any]], health: queue.Queue[HealthResult],
                 backups: BackupRunner, backup_commands: dict[str, tuple[str, ...]], events: EventLog,
                 clock: Clock, sample_out: TextIO | None = None) -> None:
        self._phases = list(phases)
        self._protocol = protocol
        self._limits = limits
        self._driver = driver
        self._recorder = recorder
        self._samples = samples
        self._health = health
        self._backups = backups
        self._backup_commands = backup_commands
        self._events = events
        self._clock = clock
        self._sample_out = sample_out
        self._state = GuardState()
        self._previous_raw: dict[str, Any] | None = None
        self.trips: list[Trip] = []

    @property
    def state(self) -> GuardState:
        return self._state

    def run(self) -> int:
        self._state.last_sample_at = self._clock.now()
        try:
            for phase in self._phases:
                if not self._run_phase(phase):
                    return EXIT_ABORTED
            return EXIT_OK
        except KeyboardInterrupt:
            self._events.write("interrupted")
            return EXIT_INTERRUPTED
        finally:
            self._driver.stop_all()
            self._backups.stop()
            self._recorder.flush()

    def _run_phase(self, phase: Phase) -> bool:
        started = self._clock.now()
        self._recorder.phase = phase.name
        self._events.write("phase_start", phase=phase.name, kind=phase.kind, clients=phase.clients,
                           min_seconds=phase.seconds, deadline_seconds=phase.deadline_seconds)
        self._driver.set_clients(phase.clients)
        if phase.kind == "entry":
            self._driver.schedule_entries(self._protocol.entry_window_seconds)
        elif phase.kind == "reconnect":
            self._driver.schedule_reconnect(self._protocol.reconnect_fraction,
                                            self._protocol.reconnect_window_seconds)
        elif phase.backup is not None:
            self._backups.start(phase.name, self._backup_commands[phase.backup])
        while True:
            self._drain()
            now = self._clock.now()
            self._state.backup_failures = self._backups.failures()
            trips = evaluate_guards(self._state, self._recorder.recent(self._limits.failure_window_seconds), now,
                                    self._limits)
            if trips:
                self.trips = trips
                for trip in trips:
                    self._events.write("guard_trip", phase=phase.name, code=trip.code, detail=trip.detail)
                self._events.write("abort", phase=phase.name, elapsed=round(now - started, 1))
                return False
            elapsed = now - started
            backup_pending = phase.backup is not None and not self._backups.done()
            if elapsed >= phase.seconds and not backup_pending:
                if phase.kind == "baseline":
                    self._freeze_health_baseline()
                self._events.write("phase_end", phase=phase.name, elapsed=round(elapsed, 1),
                                   last_write_ack=self._driver.last_write_ack())
                return True
            if elapsed >= phase.deadline_seconds:
                trip = Trip("DEADLINE", f"fase {phase.name} passou do prazo de {phase.deadline_seconds} s")
                self.trips = [trip]
                self._events.write("guard_trip", phase=phase.name, code=trip.code, detail=trip.detail)
                self._events.write("abort", phase=phase.name, elapsed=round(elapsed, 1))
                return False
            self._clock.sleep(1.0)

    def _drain(self) -> None:
        while True:
            try:
                raw = self._samples.get_nowait()
            except queue.Empty:
                break
            if self._sample_out is not None:
                self._sample_out.write(json.dumps(raw, separators=(",", ":"), sort_keys=True) + "\n")
            self._state.last_sample_at = self._clock.now()
            if self._previous_raw is not None:
                try:
                    self._state.samples.append(derive(self._previous_raw, raw))
                except (ValueError, KeyError, TypeError) as error:
                    self._events.write("sample_rejected", reason=type(error).__name__)
            self._previous_raw = raw
        while True:
            try:
                result = self._health.get_nowait()
            except queue.Empty:
                break
            self._state.health.setdefault(result.name, []).append(result)

    def _freeze_health_baseline(self) -> None:
        for name, history in self._state.health.items():
            values = [result.ms for result in history if result.ok]
            p95 = percentile(values, 95)
            if p95 is not None:
                self._state.health_baseline_p95[name] = p95
        self._events.write("health_baseline", p95_ms=self._state.health_baseline_p95)


class SshBackupRunner:
    """Roda os comandos de uma fase em sequência, pela mesma conexão SSH, com log por comando."""

    def __init__(self, ssh: Sequence[str], evidence: Path, events: EventLog) -> None:
        self._ssh = list(ssh)
        self._evidence = evidence
        self._events = events
        self._thread: threading.Thread | None = None
        self._process: subprocess.Popen[bytes] | None = None
        self._failures: list[str] = []
        self._lock = threading.Lock()

    def start(self, phase: str, commands: Sequence[str]) -> None:
        self._thread = threading.Thread(target=self._run, args=(phase, list(commands)), daemon=True)
        self._thread.start()

    def done(self) -> bool:
        return self._thread is not None and not self._thread.is_alive()

    def failures(self) -> list[str]:
        with self._lock:
            return list(self._failures)

    def stop(self) -> None:
        if self._process is not None and self._process.poll() is None:
            self._process.terminate()

    def _run(self, phase: str, commands: list[str]) -> None:
        for index, command in enumerate(commands, start=1):
            log = self._evidence / f"backup-{phase}-{index}.log"
            started = time.time()
            self._events.write("backup_start", phase=phase, index=index, command=command)
            with log.open("wb") as output:
                self._process = subprocess.Popen([*self._ssh, command], stdout=output, stderr=subprocess.STDOUT,
                                                 stdin=subprocess.DEVNULL)
                code = self._process.wait()
            self._events.write("backup_end", phase=phase, index=index, exit_code=code,
                               seconds=round(time.time() - started, 1), log=log.name)
            if code != 0:
                with self._lock:
                    self._failures.append(f"{phase} comando {index} terminou com código {code}")
                return


def start_probe(config: Config, duration_seconds: int, samples: queue.Queue[dict[str, Any]],
                events: EventLog) -> subprocess.Popen[bytes]:
    script = config.probe_script.open("rb")
    process = subprocess.Popen([*config.ssh, probe_remote_command(config, duration_seconds)], stdin=script,
                               stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
    script.close()

    def pump() -> None:
        assert process.stdout is not None
        for line in process.stdout:
            try:
                samples.put(json.loads(line))
            except ValueError:
                events.write("probe_garbage")
        events.write("probe_exit", code=process.wait())

    threading.Thread(target=pump, daemon=True).start()
    return process


def start_health(targets: Sequence[HealthTarget], results: queue.Queue[HealthResult], stop: threading.Event,
                 out: TextIO) -> None:
    context = ssl.create_default_context()

    def probe_once(target: HealthTarget) -> HealthResult:
        started = time.time()
        clock = time.monotonic()
        status = 0
        try:
            request = urllib.request.Request(target.url, headers={"User-Agent": "duora-c2-health/1"})
            with urllib.request.urlopen(request, timeout=HEALTH_TIMEOUT_SECONDS, context=context) as response:
                status = response.status
        except urllib.error.HTTPError as error:
            status = error.code
        except (OSError, ValueError):
            status = 0
        ms = (time.monotonic() - clock) * 1000
        return HealthResult(started, target.name, 200 <= status < 400, round(ms, 1), status)

    def loop() -> None:
        while not stop.is_set():
            for target in targets:
                result = probe_once(target)
                results.put(result)
                out.write(json.dumps(asdict(result), sort_keys=True) + "\n")
                out.flush()
            stop.wait(HEALTH_SECONDS)

    threading.Thread(target=loop, daemon=True).start()


def git_commit() -> str:
    try:
        return subprocess.run(["git", "rev-parse", "HEAD"], capture_output=True, text=True, check=True,
                              cwd=Path(__file__).parent).stdout.strip()
    except (OSError, subprocess.CalledProcessError):
        return "UNKNOWN"


def check_runnable(config: Config, repetition: int, protocol: C2Protocol) -> list[c2_load.Account]:
    """Recusa antes de qualquer efeito: repetição fora de 1..3, contas insuficientes ou fora do domínio sintético."""
    if not 1 <= repetition <= REPETITIONS:
        raise ConfigError(f"repetição {repetition} fora de 1..{REPETITIONS}")
    accounts = c2_load.load_accounts(config.accounts_file)
    if len(accounts) < protocol.peak_clients:
        raise ConfigError(f"{len(accounts)} contas sintéticas; o pico exige {protocol.peak_clients}")
    if not config.probe_script.is_file():
        raise ConfigError(f"sampler ausente: {config.probe_script}")
    return accounts


def run_repetition(config: Config, repetition: int, evidence_root: Path) -> int:
    protocol = C2Protocol()
    phases = protocol.phases()
    accounts = check_runnable(config, repetition, protocol)
    stamp = datetime.now(UTC).strftime("%Y%m%dT%H%M%SZ")
    evidence = evidence_root / f"c2-{stamp}-rep{repetition}"
    evidence.mkdir(parents=True, exist_ok=False)
    clock = Clock()
    manifest: dict[str, Any] = {
        "schema": MANIFEST_SCHEMA, "repetition": repetition, "commit": git_commit(), "started_at": stamp,
        "project": config.project, "server_name": config.target.server_name, "seed": config.seed,
        "accounts": len(accounts), "phases": [asdict(phase) for phase in phases], "limits": asdict(config.limits),
        "backup_commands": config.backup_commands, "health_targets": [asdict(t) for t in config.health],
        "probe_script_sha256": hashlib.sha256(config.probe_script.read_bytes()).hexdigest(),
        "pending_flows": ["mensagens", "SignalR"],
    }
    with (evidence / "events.jsonl").open("w", encoding="utf-8") as events_file, \
            (evidence / "requests.jsonl").open("w", encoding="utf-8") as requests_file, \
            (evidence / "samples.jsonl").open("w", encoding="utf-8") as samples_file, \
            (evidence / "health.jsonl").open("w", encoding="utf-8") as health_file:
        events = EventLog(events_file, clock)
        recorder = c2_load.Recorder(requests_file)
        samples: queue.Queue[dict[str, Any]] = queue.Queue()
        health_results: queue.Queue[HealthResult] = queue.Queue()
        stop_health = threading.Event()
        probe = start_probe(config, deadline_seconds(phases) + PROBE_BUFFER_SECONDS, samples, events)
        start_health(config.health, health_results, stop_health, health_file)
        driver = c2_load.LoadDriver(config.target, accounts, recorder, seed=config.seed * 10 + repetition)
        backups = SshBackupRunner(config.ssh, evidence, events)
        orchestrator = Orchestrator(phases, protocol, config.limits, driver, recorder, samples, health_results,
                                    backups, config.backup_commands, events, clock, samples_file)
        try:
            code = orchestrator.run()
        finally:
            stop_health.set()
            probe.terminate()
        if code == EXIT_ABORTED:
            for command in config.abort_commands:
                result = subprocess.run([*config.ssh, command], capture_output=True, timeout=120, check=False)
                events.write("abort_command", command=command, exit_code=result.returncode)
        manifest.update({"finished_at": datetime.now(UTC).strftime("%Y%m%dT%H%M%SZ"), "exit_code": code,
                         "trips": [asdict(trip) for trip in orchestrator.trips],
                         "messages_not_simulated": driver.messages_skipped,
                         "last_write_ack": driver.last_write_ack()})
    (evidence / "manifest.json").write_text(json.dumps(manifest, indent=2, ensure_ascii=False) + "\n",
                                            encoding="utf-8")
    report = build_report(evidence)
    write_report(evidence, report)
    print(f"evidência: {evidence}\nveredito: {report['verdict']}", flush=True)
    return code


# ---------------------------------------------------------------- relatório


def read_jsonl(path: Path) -> list[dict[str, Any]]:
    if not path.exists():
        return []
    return [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines() if line.strip()]


def phase_windows(events: Sequence[dict[str, Any]]) -> list[tuple[str, float, float]]:
    windows = []
    open_phase: tuple[str, float] | None = None
    for entry in events:
        if entry["event"] == "phase_start":
            open_phase = (entry["phase"], entry["t"])
        elif entry["event"] in ("phase_end", "abort") and open_phase is not None:
            windows.append((open_phase[0], open_phase[1], entry["t"]))
            open_phase = None
    return windows


def summarize_requests(requests: Sequence[dict[str, Any]], seconds: float) -> dict[str, Any]:
    by_kind: dict[str, list[dict[str, Any]]] = {}
    for request in requests:
        by_kind.setdefault(request["kind"], []).append(request)
    classes = {name: 0 for name in ("ok", "client", "throttled", "server", "transport")}
    for request in requests:
        classes[c2_load.classify(int(request["status"]))] += 1
    total = len(requests)
    failures = classes["server"] + classes["transport"]
    kinds = {}
    for kind, entries in sorted(by_kind.items()):
        latencies = [float(entry["ms"]) for entry in entries if c2_load.classify(int(entry["status"])) == "ok"]
        kinds[kind] = {"count": len(entries), **{f"p{rank}_ms": percentile(latencies, rank) for rank in PERCENTILES}}
    floor = [float(r["ms"]) for r in requests if r["op"] == "csrf" and c2_load.classify(int(r["status"])) == "ok"]
    return {"total": total, "rate_per_s": round(total / seconds, 2) if seconds > 0 else None, "classes": classes,
            "failure_ratio": round(failures / total, 4) if total else None, "kinds": kinds,
            "rtt_floor_ms": min(floor) if floor else None}


def summarize_samples(samples: Sequence[dict[str, Any]]) -> dict[str, Any]:
    def series(key: str) -> list[float]:
        return [float(s[key]) for s in samples if s.get(key) is not None]

    def stats(values: list[float]) -> dict[str, float | None]:
        return {"avg": round(sum(values) / len(values), 2) if values else None,
                "max": max(values) if values else None}

    psi = {f"{kind}_{level}": max((float(s["psi"][kind][f"{level}_avg10"]) for s in samples
                                   if s["psi"].get(kind) and s["psi"][kind].get(f"{level}_avg10") is not None),
                                  default=None)
           for kind in ("cpu", "memory", "io") for level in ("some", "full")}
    services: dict[str, dict[str, Any]] = {}
    for sample in samples:
        for name, values in sample["cgroups"].items():
            entry = services.setdefault(name, {"owner": values["owner"], "cpu_pct": [], "throttled_ratio": [],
                                               "memory_mib": [], "cpu_psi": [], "io_mib_s": []})
            for key in ("cpu_pct", "throttled_ratio", "memory_mib", "cpu_psi", "io_mib_s"):
                if values.get(key) is not None:
                    entry[key].append(float(values[key]))
    cgroups = {name: {"owner": entry["owner"], **{key: stats(entry[key]) for key in entry if key != "owner"}}
               for name, entry in sorted(services.items())}
    disks: dict[str, dict[str, list[float]]] = {}
    for sample in samples:
        for name, values in sample["disks"].items():
            for key, value in values.items():
                disks.setdefault(name, {}).setdefault(key, []).append(float(value))
    database = [s["database"] for s in samples if s.get("database")]

    def db_max(key: str) -> float | None:
        values = [float(d[key]) for d in database if d.get(key) is not None]
        return max(values) if values else None

    return {
        "samples": len(samples), "busy_pct": stats(series("busy_pct")), "steal_pct": stats(series("steal_pct")),
        "iowait_pct": stats(series("iowait_pct")),
        "mem_available_min_mib": min(series("mem_available_mib"), default=None),
        "swap_pages": int(sum(series("swap_pages"))), "oom_kills": int(sum(series("oom_kills"))),
        "fs_max_percent": max(series("fs_max_percent"), default=None), "psi_max": psi,
        "others_cpu_psi_max": max(series("others_cpu_psi_max"), default=None),
        "disks": {name: {key: stats(values) for key, values in metrics.items()} for name, metrics in disks.items()},
        "cgroups": cgroups,
        "database": {key: db_max(key) for key in ("archive_lag_s", "jobs_due", "jobs_oldest_due_age_s",
                                                  "jobs_max_lateness_s", "outbox_pending")}
        | {"archive_failed": int(sum(float(d["archive_failed"]) for d in database if d.get("archive_failed")))},
    }


def phase_gates(requests: dict[str, Any], samples: dict[str, Any], limits: Limits) -> dict[str, bool | None]:
    kinds = requests["kinds"]

    def p95_within(kind: str, ceiling: float) -> bool | None:
        value = kinds.get(kind, {}).get("p95_ms")
        return None if value is None else value <= ceiling

    ratio = requests["failure_ratio"]
    return {"read_p95": p95_within("read", limits.read_p95_ms),
            "command_p95": p95_within("command", limits.command_p95_ms),
            "reconcile_p95": p95_within("reconcile", limits.reconcile_p95_ms),
            "failure_ratio": None if ratio is None else ratio < limits.failure_max_ratio,
            "no_oom": samples["oom_kills"] == 0}


def investigation_flags(samples: Sequence[dict[str, Any]], limits: Limits) -> list[str]:
    flags = []
    if _sustained(samples, limits.investigate_busy_seconds, "BUSY",
                  lambda s: (s.get("busy_pct") or 0) > limits.investigate_busy_percent, ""):
        flags.append(f"CPU > {limits.investigate_busy_percent}% por {limits.investigate_busy_seconds:.0f} s")
    throttled = sorted({name for s in samples for name, values in s["cgroups"].items()
                        if values["owner"] == "c2" and values["throttled_ratio"] > limits.investigate_throttle_ratio})
    if throttled:
        flags.append("throttling > 10% dos períodos em: " + ", ".join(throttled))
    return flags


def build_report(evidence: Path) -> dict[str, Any]:
    manifest = json.loads((evidence / "manifest.json").read_text(encoding="utf-8"))
    limits = replace(Limits(), **manifest["limits"])
    events = read_jsonl(evidence / "events.jsonl")
    requests = read_jsonl(evidence / "requests.jsonl")
    raw_samples = read_jsonl(evidence / "samples.jsonl")
    derived = [derive(a, b) for a, b in zip(raw_samples, raw_samples[1:], strict=False) if b["t"] > a["t"]]
    phases = []
    baseline_queue: float | None = None
    for name, start, end in phase_windows(events):
        phase_requests = [r for r in requests if r["phase"] == name]
        phase_samples = [s for s in derived if start < s["t"] <= end]
        request_summary = summarize_requests(phase_requests, end - start)
        sample_summary = summarize_samples(phase_samples)
        entry: dict[str, Any] = {"phase": name, "seconds": round(end - start, 1), "requests": request_summary,
                                 "resources": sample_summary,
                                 "gates": phase_gates(request_summary, sample_summary, limits)
                                 if phase_requests else {},
                                 "investigate": investigation_flags(phase_samples, limits)}
        queues = [max(float(s["database"].get("jobs_due") or 0), float(s["database"].get("outbox_pending") or 0))
                  for s in phase_samples if s.get("database")]
        if name == "baseline":
            baseline_queue = max(queues, default=0.0)
        if name.startswith("recovery") and queues:
            tail = queues[-max(1, int(60 / SAMPLE_SECONDS)):]
            entry["gates"]["queues_back_to_baseline"] = max(tail) <= (baseline_queue or 0.0)
        phases.append(entry)
    backups = [e for e in events if e["event"] == "backup_end"]
    trips = [e for e in events if e["event"] == "guard_trip"]
    all_gates = [value for phase in phases for value in phase["gates"].values()]
    completed = manifest.get("exit_code") == EXIT_OK
    if not completed:
        verdict = "ABORTED" if manifest.get("exit_code") == EXIT_ABORTED else "INCOMPLETE"
    elif any(value is False for value in all_gates):
        verdict = "FAIL"
    else:
        verdict = "PASS_CANDIDATE"
    report: dict[str, Any] = {
        "schema": REPORT_SCHEMA, "repetition": manifest["repetition"], "commit": manifest["commit"],
        "verdict": verdict, "trips": trips, "backups": backups, "phases": phases,
        "pending_flows": manifest.get("pending_flows", []),
        "note": "PASS_CANDIDATE não é aceite: exige as 3 repetições, restore e revisão humana (capacity.md).",
    }
    report["content_sha256"] = hashlib.sha256(json.dumps(report, sort_keys=True).encode()).hexdigest()
    return report


def render_markdown(report: dict[str, Any]) -> str:
    lines = [f"# C2 repetição {report['repetition']} — {report['verdict']}", "",
             f"Commit `{report['commit']}`; sha256 do relatório `{report['content_sha256']}`.", "",
             report["note"], "",
             "| Fase | s | req/s | leitura p95 | comando p95 | reconcil. p95 | falhas | CPU máx | steal máx | "
             "PSI CPU | MemAvail mín | outros PSI | lag WAL máx | gates |",
             "|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|"]
    for phase in report["phases"]:
        requests, resources = phase["requests"], phase["resources"]
        kinds = requests["kinds"]

        def p95(kind: str) -> str:
            value = kinds.get(kind, {}).get("p95_ms")
            return "—" if value is None else f"{value:.0f}"

        ratio = requests["failure_ratio"]
        failed = [name for name, value in phase["gates"].items() if value is False]
        lines.append(" | ".join([
            f"| {phase['phase']}", f"{phase['seconds']:.0f}", str(requests["rate_per_s"] or 0), p95("read"),
            p95("command"), p95("reconcile"), "—" if ratio is None else f"{ratio:.2%}",
            _fmt(resources["busy_pct"]["max"]), _fmt(resources["steal_pct"]["max"]),
            _fmt(resources["psi_max"]["cpu_some"]), _fmt(resources["mem_available_min_mib"]),
            _fmt(resources["others_cpu_psi_max"]), _fmt(resources["database"]["archive_lag_s"]),
            ("falhou: " + ", ".join(failed)) if failed else "ok"]) + " |")
    if report["trips"]:
        lines += ["", "## Guardas disparados", ""]
        lines += [f"- `{trip['code']}` na fase {trip['phase']}: {trip['detail']}" for trip in report["trips"]]
    if report["backups"]:
        lines += ["", "## Backups", ""]
        lines += [f"- {b['phase']} #{b['index']}: código {b['exit_code']} em {b['seconds']} s" for b in report["backups"]]
    lines += ["", "Pendentes (sem rota no produto): " + ", ".join(report["pending_flows"]) + "."]
    return "\n".join(lines) + "\n"


def _fmt(value: float | None) -> str:
    return "—" if value is None else f"{value:.1f}"


def write_report(evidence: Path, report: dict[str, Any]) -> None:
    (evidence / "report.json").write_text(json.dumps(report, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    (evidence / "report.md").write_text(render_markdown(report), encoding="utf-8")


# ---------------------------------------------------------------- RPO/RTO do restore do ensaio


def restore_objectives(*, last_ack_at: float, restored_max_at: float, outage_at: float, ready_at: float,
                       rpo_max_seconds: float = 300.0, rto_max_seconds: float = 7200.0) -> dict[str, Any]:
    """RPO = escrita confirmada mais nova antes da perda menos a mais nova que voltou; RTO = perda até pronto."""
    if not restored_max_at <= last_ack_at <= outage_at <= ready_at:
        raise ValueError("instantes fora de ordem: restaurado <= confirmado <= perda <= pronto")
    rpo = last_ack_at - restored_max_at
    rto = ready_at - outage_at
    return {"rpo_seconds": round(rpo, 1), "rto_seconds": round(rto, 1), "rpo_ok": rpo <= rpo_max_seconds,
            "rto_ok": rto <= rto_max_seconds}


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="harness C2 da issue #86")
    commands = parser.add_subparsers(dest="command", required=True)
    commands.add_parser("plan", help="mostra as fases, a duração nominal e o prazo máximo")
    run = commands.add_parser("run", help="executa uma repetição (1..3)")
    run.add_argument("--config", type=Path, required=True)
    run.add_argument("--repetition", type=int, required=True)
    run.add_argument("--evidence", type=Path, required=True)
    report = commands.add_parser("report", help="recalcula report.json/report.md de uma evidência")
    report.add_argument("evidence", type=Path)
    restore = commands.add_parser("restore-objectives", help="RPO/RTO observados do restore do ensaio")
    for name in ("last-ack-at", "restored-max-at", "outage-at", "ready-at"):
        restore.add_argument(f"--{name}", type=float, required=True, help="epoch UTC em segundos")
    args = parser.parse_args(argv)
    if args.command == "plan":
        phases = C2Protocol().phases()
        for phase in phases:
            print(f"{phase.name:16} {phase.kind:9} clientes={phase.clients:3} mín={phase.seconds:5}s "
                  f"prazo={phase.deadline_seconds:5}s{'  backup=' + phase.backup if phase.backup else ''}")
        print(f"nominal {nominal_seconds(phases) / 60:.0f} min; prazo máximo {deadline_seconds(phases) / 60:.0f} min "
              f"por repetição; {REPETITIONS} repetições")
        return EXIT_OK
    if args.command == "report":
        built = build_report(args.evidence)
        write_report(args.evidence, built)
        print(render_markdown(built))
        return EXIT_OK
    if args.command == "restore-objectives":
        try:
            result = restore_objectives(last_ack_at=args.last_ack_at, restored_max_at=args.restored_max_at,
                                        outage_at=args.outage_at, ready_at=args.ready_at)
        except ValueError as error:
            print(f"c2-harness: {error}", file=sys.stderr)
            return EXIT_CONFIG
        print(json.dumps(result, indent=2))
        return EXIT_OK if result["rpo_ok"] and result["rto_ok"] else EXIT_ABORTED
    try:
        config = load_config(args.config)
        check_runnable(config, args.repetition, C2Protocol())
    except (ConfigError, KeyError, ValueError, TypeError, OSError) as error:
        print(f"c2-harness: configuração inválida: {error}", file=sys.stderr)
        return EXIT_CONFIG
    return run_repetition(config, args.repetition, args.evidence)


if __name__ == "__main__":
    sys.exit(main())
