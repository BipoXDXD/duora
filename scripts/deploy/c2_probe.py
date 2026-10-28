"""Sampler C2 somente leitura da VPS (issue #86, docs/operations/capacity-c2.md).

Emite uma linha JSON a cada intervalo com contadores **brutos e cumulativos** do host (CPU com steal/iowait, PSI,
MemAvailable, swap, OOM, discos, uso de filesystem), de cada cgroup de container (CPU/throttling, memória, OOM, PSI,
I/O) e, só do projeto sombra, o arquivamento de WAL e as filas (ops.jobs, outbox). As taxas e os guardas são
calculados no Mac pelo c2_harness, que é testado; aqui só se lê.

Não altera o host: dois comandos exatos (docker ps e um SELECT no postgres do projeto sombra) passam pela
allowlist; o projeto de produção `duora` é recusado. Uso (stdin, nada gravado no host):
    ssh host 'nice -n 19 python3 -I - sample --project duora-c2 --interval 5 --duration 14400' < c2_probe.py
"""
from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys
import time
from typing import Any, Protocol, TextIO

SCHEMA = "duora.c2-sample/v1"
CGROUP_ROOT = "/sys/fs/cgroup"
PROJECT_PATTERN = re.compile(r"^duora-c2(-[a-z0-9]{1,16})?$")
CONTAINER_ID = re.compile(r"^[0-9a-f]{64}$")
IGNORED_DEVICES = re.compile(r"^(loop|ram|zram|sr|fd|dm-)\d*")
DEVICE_PARTITION = re.compile(r"^(sd[a-z]+|vd[a-z]+|xvd[a-z]+)\d+$|^nvme\d+n\d+p\d+$")
FILESYSTEMS = ("/", "/var/lib/docker")
CPU_FIELDS = ("user", "nice", "system", "idle", "iowait", "irq", "softirq", "steal")
COMMAND_TIMEOUT_SECONDS = 10
DISCOVERY_SECONDS = 60.0
KIB_PER_MIB = 1024
MIB = 1024 * 1024
PS_COMMAND = ("docker", "ps", "--no-trunc", "--format",
              '{{.ID}}\t{{.Names}}\t{{.Label "com.docker.compose.project"}}')
# Uma linha: arquivamento de WAL (cluster), jobs vencidos e atraso, outbox pendente. NULL vira \N.
# archive_pending: há segmento completo ainda não arquivado. Banco ocioso não troca de segmento
# (archive_timeout só age com WAL novo), então a idade do último arquivamento sozinha não é atraso.
# Nomes de WAL da mesma timeline têm largura fixa: a ordem textual é a numérica.
STATS_SQL = (
    "select a.archived_count, a.failed_count, extract(epoch from now()-a.last_archived_time), "
    "(pg_walfile_name(pg_current_wal_lsn() - (select setting::numeric from pg_settings "
    "where name='wal_segment_size')) > coalesce(a.last_archived_wal,''))::int, "
    "extract(epoch from now()-a.last_failed_time), "
    "(select count(*) from ops.jobs where status='Pending' and available_at<=now()), "
    "(select coalesce(extract(epoch from now()-min(available_at)),0) from ops.jobs "
    "where status='Pending' and available_at<=now()), "
    "(select coalesce(max(extract(epoch from completed_at-due_at)),0) from ops.jobs "
    "where completed_at>now()-interval '1 minute'), "
    "(select count(*) from ops.outbox_deliveries where status='Pending') from pg_stat_archiver a")
STATS_FIELDS = ("archived_count", "failed_count", "last_archived_age_s", "archive_pending", "last_failed_age_s",
                "jobs_due",
                "jobs_oldest_due_age_s", "jobs_max_lateness_s", "outbox_pending")
INTEGER_STATS = frozenset(("archived_count", "failed_count", "archive_pending", "jobs_due", "outbox_pending"))


class CommandRefused(RuntimeError):
    """Comando fora da allowlist ou projeto que não é o sombra."""


class Host(Protocol):
    def read(self, path: str) -> str | None: ...

    def exists(self, path: str) -> bool: ...

    def statvfs(self, path: str) -> tuple[int, int] | None: ...

    def run(self, argv: list[str]) -> str: ...

    def time(self) -> float: ...

    def sleep(self, seconds: float) -> None: ...


def validate_project(project: str) -> str:
    """Só o projeto sombra (duora-c2[-sufixo]); `duora`, de produção, nunca."""
    if not PROJECT_PATTERN.fullmatch(project):
        raise CommandRefused(f"projeto {project!r} não é o sombra duora-c2")
    return project


def stats_command(project: str) -> list[str]:
    return ["docker", "exec", f"{validate_project(project)}-postgres-1", "psql", "-X", "-A", "-t", "-F", "|",
            "-P", "null=\\N", "-U", "postgres", "-d", "duora", "-c", STATS_SQL]


def require_allowed(argv: list[str], *, project: str) -> None:
    if tuple(argv) == PS_COMMAND:
        return
    if argv == stats_command(project):
        return
    raise CommandRefused("comando fora da allowlist somente leitura: " + " ".join(argv[:3]))


class LocalHost:
    def __init__(self, project: str) -> None:
        self._project = validate_project(project)

    def read(self, path: str) -> str | None:
        try:
            with open(path, encoding="utf-8", errors="replace") as file:
                return file.read()
        except OSError:
            return None

    def exists(self, path: str) -> bool:
        return os.path.exists(path)

    def statvfs(self, path: str) -> tuple[int, int] | None:
        try:
            info = os.statvfs(path)
        except OSError:
            return None
        return info.f_blocks * info.f_frsize, info.f_bavail * info.f_frsize

    def run(self, argv: list[str]) -> str:
        require_allowed(argv, project=self._project)
        result = subprocess.run(argv, capture_output=True, text=True, timeout=COMMAND_TIMEOUT_SECONDS, check=False)
        if result.returncode != 0:
            # stderr fica de fora: pode ecoar parte do comando ou do ambiente.
            raise RuntimeError(f"{argv[0]} {argv[1]} terminou com código {result.returncode}")
        return result.stdout

    def time(self) -> float:
        return time.time()

    def sleep(self, seconds: float) -> None:
        time.sleep(seconds)


class Sampler:
    def __init__(self, host: Host, *, project: str, extra_cgroups: tuple[str, ...] = ()) -> None:
        self._host = host
        self._project = validate_project(project)
        self._extra = extra_cgroups
        self._containers: list[tuple[str, str, str]] = []
        self._discovered_at: float | None = None

    def sample(self) -> dict[str, Any]:
        errors: dict[str, str] = {}
        now = self._host.time()
        self._refresh_containers(now, errors)
        return {"schema": SCHEMA, "t": now, "host": self._host_counters(), "cgroups": self._cgroups(),
                "database": self._database(errors), "errors": errors}

    def _refresh_containers(self, now: float, errors: dict[str, str]) -> None:
        if self._discovered_at is not None and now - self._discovered_at < DISCOVERY_SECONDS:
            return
        try:
            listing = self._host.run(list(PS_COMMAND))
        except (RuntimeError, OSError, subprocess.TimeoutExpired) as error:
            errors["containers"] = type(error).__name__
            return
        containers = []
        for line in listing.splitlines():
            parts = line.split("\t")
            if len(parts) == 3 and CONTAINER_ID.fullmatch(parts[0]):
                containers.append((parts[0], parts[1], parts[2]))
        self._containers = containers
        self._discovered_at = now

    def _host_counters(self) -> dict[str, Any]:
        meminfo = _key_values(self._host.read("/proc/meminfo"), suffix=" kB")
        return {
            "cpu": _cpu(self._host.read("/proc/stat")),
            "mem_total_mib": _mib_from_kib(meminfo.get("MemTotal")),
            "mem_available_mib": _mib_from_kib(meminfo.get("MemAvailable")),
            "vmstat": _vmstat(self._host.read("/proc/vmstat")),
            "psi": {kind: _psi(self._host.read(f"/proc/pressure/{kind}")) for kind in ("cpu", "memory", "io")},
            "disks": _disks(self._host.read("/proc/diskstats")),
            "filesystems": self._filesystems(),
        }

    def _filesystems(self) -> dict[str, float]:
        usage = {}
        for path in FILESYSTEMS:
            info = self._host.statvfs(path)
            if info is not None and info[0] > 0:
                total, available = info
                usage[path] = round(100.0 * (total - available) / total, 2)
        return usage

    def _cgroups(self) -> dict[str, dict[str, Any]]:
        result: dict[str, dict[str, Any]] = {}
        for identifier, name, project in self._containers:
            path = f"{CGROUP_ROOT}/system.slice/docker-{identifier}.scope"
            if self._host.exists(path):
                owner = "c2" if project == self._project else "other"
                result[name] = {"owner": owner, "project": project, **self._cgroup(path)}
        for relative in self._extra:
            path = f"{CGROUP_ROOT}/{relative}"
            if self._host.exists(path):
                result[relative] = {"owner": "other", "project": "", **self._cgroup(path)}
        return result

    def _cgroup(self, path: str) -> dict[str, Any]:
        stat = _key_values(self._host.read(f"{path}/cpu.stat"))
        events = _key_values(self._host.read(f"{path}/memory.events"))
        rbytes, wbytes = _io_bytes(self._host.read(f"{path}/io.stat"))
        return {
            "usage_usec": _int(stat.get("usage_usec")),
            "nr_periods": _int(stat.get("nr_periods")),
            "nr_throttled": _int(stat.get("nr_throttled")),
            "throttled_usec": _int(stat.get("throttled_usec")),
            "memory_current_mib": _mib_from_bytes(self._host.read(f"{path}/memory.current")),
            "memory_peak_mib": _mib_from_bytes(self._host.read(f"{path}/memory.peak")),
            "oom_kill": _int(events.get("oom_kill")),
            "cpu_psi_some_avg10": _some(self._host.read(f"{path}/cpu.pressure")),
            "memory_psi_some_avg10": _some(self._host.read(f"{path}/memory.pressure")),
            "io_psi_some_avg10": _some(self._host.read(f"{path}/io.pressure")),
            "io_rbytes": rbytes,
            "io_wbytes": wbytes,
        }

    def _database(self, errors: dict[str, str]) -> dict[str, Any] | None:
        try:
            output = self._host.run(stats_command(self._project))
            return parse_stats(output)
        except (RuntimeError, OSError, ValueError, subprocess.TimeoutExpired) as error:
            errors["database"] = type(error).__name__
            return None


def parse_stats(output: str) -> dict[str, Any]:
    values = output.strip().split("|")
    if len(values) != len(STATS_FIELDS):
        raise ValueError("linha de estatísticas fora do formato")
    parsed: dict[str, Any] = {}
    for field, raw in zip(STATS_FIELDS, values, strict=True):
        if raw == "\\N" or raw == "":
            parsed[field] = None
        elif field in INTEGER_STATS:
            parsed[field] = int(raw)
        else:
            parsed[field] = round(float(raw), 3)
    return parsed


def run_loop(sampler: Sampler, host: Host, *, interval: float, duration: float, out: TextIO) -> None:
    """Uma linha por intervalo até `duration`; o prazo protege o host se o Mac sumir sem fechar o SSH."""
    started = host.time()
    while host.time() - started <= duration:
        tick = host.time()
        out.write(json.dumps(sampler.sample(), separators=(",", ":"), sort_keys=True) + "\n")
        out.flush()
        host.sleep(max(0.0, interval - (host.time() - tick)))


# ---------------------------------------------------------------- parsing de /proc e cgroup


def _cpu(text: str | None) -> dict[str, int] | None:
    if not text:
        return None
    fields = text.splitlines()[0].split()
    if fields[0] != "cpu" or len(fields) < 1 + len(CPU_FIELDS):
        return None
    return {name: int(value) for name, value in zip(CPU_FIELDS, fields[1:1 + len(CPU_FIELDS)], strict=True)}


def _key_values(text: str | None, *, suffix: str = "") -> dict[str, str]:
    values: dict[str, str] = {}
    for line in (text or "").splitlines():
        key, _, value = line.partition(":") if ":" in line else line.partition(" ")
        value = value.strip()
        if suffix and value.endswith(suffix):
            value = value[: -len(suffix)].strip()
        values[key.strip()] = value
    return values


def _vmstat(text: str | None) -> dict[str, int | None]:
    values = _key_values(text)
    return {name: _int(values.get(name)) for name in ("pswpin", "pswpout", "oom_kill")}


def _psi(text: str | None) -> dict[str, float] | None:
    if not text:
        return None
    result = {}
    for line in text.splitlines():
        kind, _, rest = line.partition(" ")
        fields = dict(part.split("=", 1) for part in rest.split() if "=" in part)
        if kind in ("some", "full") and "avg10" in fields:
            result[f"{kind}_avg10"] = float(fields["avg10"])
    return result or None


def _some(text: str | None) -> float | None:
    psi = _psi(text)
    return None if psi is None else psi.get("some_avg10")


def _disks(text: str | None) -> dict[str, dict[str, int]]:
    disks = {}
    for line in (text or "").splitlines():
        fields = line.split()
        if len(fields) < 14:
            continue
        name = fields[2]
        if IGNORED_DEVICES.match(name) or DEVICE_PARTITION.match(name):
            continue
        disks[name] = {"reads": int(fields[3]), "read_sectors": int(fields[5]), "writes": int(fields[7]),
                       "write_sectors": int(fields[9]), "io_ms": int(fields[12]), "weighted_ms": int(fields[13])}
    return disks


def _io_bytes(text: str | None) -> tuple[int | None, int | None]:
    if text is None:
        return None, None
    rbytes = wbytes = 0
    for line in text.splitlines():
        for part in line.split()[1:]:
            key, _, value = part.partition("=")
            if key == "rbytes":
                rbytes += int(value)
            elif key == "wbytes":
                wbytes += int(value)
    return rbytes, wbytes


def _int(value: str | None) -> int | None:
    try:
        return None if value is None else int(value)
    except ValueError:
        return None


def _mib_from_kib(value: str | None) -> float | None:
    number = _int(value)
    return None if number is None else round(number / KIB_PER_MIB, 2)


def _mib_from_bytes(text: str | None) -> float | None:
    number = _int(None if text is None else text.strip())
    return None if number is None else round(number / MIB, 2)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="sampler C2 somente leitura (stdin via SSH)")
    commands = parser.add_subparsers(dest="command", required=True)
    sample = commands.add_parser("sample")
    sample.add_argument("--project", required=True)
    sample.add_argument("--interval", type=float, default=5.0)
    sample.add_argument("--duration", type=float, required=True, help="segundos; o sampler para sozinho depois")
    sample.add_argument("--extra-cgroup", action="append", default=[],
                        help="cgroup relativo a /sys/fs/cgroup (ex.: duora-media.slice)")
    args = parser.parse_args(argv)
    try:
        host = LocalHost(args.project)
    except CommandRefused as error:
        print(f"c2-probe: {error}", file=sys.stderr)
        return 2
    sampler = Sampler(host, project=args.project, extra_cgroups=tuple(args.extra_cgroup))
    try:
        run_loop(sampler, host, interval=args.interval, duration=args.duration, out=sys.stdout)
    except (KeyboardInterrupt, BrokenPipeError):
        return 0
    return 0


if __name__ == "__main__":
    sys.exit(main())
