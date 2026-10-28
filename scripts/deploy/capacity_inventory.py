"""Coletor C1 somente leitura da VPS atual (issue #86, docs/operations/capacity.md).

Inventaria identidade/boot/kernel/cgroup v2/discos/steal, todos os containers e cgroups populados, e a cadeia
de limites declarado (Compose) -> renderizado (`docker compose config`) -> HostConfig -> arquivos do cgroup em
todos os ancestrais. Soma o orçamento por dono sem dupla contagem e separa UNBOUNDED medido de UNKNOWN.

Não altera o host: todo comando passa por `require_read_only` e só campos de uma allowlist chegam à saída
(nada de Env, Cmd, Mounts nem labels arbitrários). Não mede capacidade de IOPS nem roda carga (C2).

Uso na VPS (stdin, sem arquivo remoto):
    ssh host 'nice -n 19 ionice -c 3 python3 -I - collect --source-sha256 <sha>' < capacity_inventory.py > c1.json
    python3 capacity_inventory.py summarize c1.json
"""
from __future__ import annotations

import argparse
import hashlib
import importlib
import json
import math
import os
import re
import subprocess
import sys
from dataclasses import dataclass, field
from datetime import UTC, datetime
from pathlib import Path
from typing import Any, Protocol

SCHEMA = "duora.c1-inventory/v1"
CGROUP_ROOT = "/sys/fs/cgroup"
INF = math.inf
FINITE, UNBOUNDED, UNKNOWN = "FINITE", "UNBOUNDED", "UNKNOWN"
UNREADABLE = "UNREADABLE"
NOT_COMPOSE = "N/A"
LIMIT_FILES = ("cpu.max", "cpu.weight", "memory.max", "memory.high", "memory.swap.max", "pids.max", "io.max",
               "io.weight")
USAGE_FILES = ("memory.current", "memory.peak", "memory.swap.current", "pids.current", "cpu.stat",
               "memory.events")
NODE_FILES = ("cgroup.controllers", "cgroup.events", "cgroup.procs", *LIMIT_FILES, *USAGE_FILES)
# Recurso -> (arquivo de limite, controlador, chave de saída).
RESOURCES = {"memory": ("memory.max", "memory", "bytes"), "cpu": ("cpu.max", "cpu", "vcpu"),
             "pids": ("pids.max", "pids", "count"), "io": ("io.max", "io", "devices")}
SUMMED = ("memory", "cpu", "pids")
STAGES = ("declared", "rendered", "host_config", "cgroup")
EXPECTED_OWNERS = ("duora", "goblinshift", "stillon", "kairyaquest", "coolify", "openclaw", "host")
MAX_CGROUP_NODES = 4000
COMMAND_TIMEOUT_SECONDS = 30
DEFAULT_CPU_PERIOD_USEC = 100_000
PAGE_BYTES = 4096
MIN_CPU_SHARES, MAX_CPU_SHARES, MAX_CPU_WEIGHT = 2, 262144, 10000
IGNORED_BLOCK_DEVICES = re.compile(r"^(loop|ram|zram|sr|fd)\d*")
BACKUP_PATTERN = re.compile(r"backup|restic|wal-?g|pgbackrest|pg_?dump", re.IGNORECASE)
ALLOWED_LABELS = {
    "com.docker.compose.project": "project", "com.docker.compose.service": "service",
    "com.docker.compose.project.working_dir": "working_dir",
    "com.docker.compose.project.config_files": "config_files",
    "com.docker.compose.project.environment_file": "environment_files",
    "coolify.projectName": "coolify_project", "coolify.resourceName": "coolify_resource",
}
# Ordem importa: a primeira regra que casa define o dono. Dados, não código, para revisão fácil.
CONTAINER_RULES: tuple[tuple[str, str, str], ...] = (
    ("openclaw", "name", r"^openclaw"),
    ("duora", "project", r"^duora$"),
    ("duora", "coolify_project", r"^duora$"),
    ("duora", "name", r"^deployment-observer-"),
    ("goblinshift", "project", r"^goblinshift$"),
    ("stillon", "project", r"^stillon$"),
    ("kairyaquest", "coolify_project", r"^kairyaquest$"),
    ("coolify", "working_dir", r"^/data/coolify/(source|proxy)$"),
    ("coolify", "name", r"^coolify"),
)
UNIT_RULES: tuple[tuple[str, str], ...] = (
    ("openclaw", r"openclaw"), ("duora", r"duora"), ("goblinshift", r"goblinshift"), ("stillon", r"stillon"),
    ("kairyaquest", r"kairyaquest"), ("coolify", r"coolify"),
)
COMPOSE_VALUE_FLAGS = ("--project-name", "--project-directory", "--env-file", "--file")
EXACT_COMMANDS = (
    ("docker", "ps", "--all", "--quiet", "--no-trunc"),
    ("docker", "info", "--format", "{{json .}}"),
    ("docker", "compose", "version", "--short"),
    ("systemctl", "list-timers", "--all", "--no-pager", "--output=json"),
    ("systemd-detect-virt",),
)
CONTAINER_ID = re.compile(r"^[0-9a-f]{3,64}$")


class CommandError(RuntimeError):
    """Comando recusado pela allowlist ou que terminou com erro (sem stderr, que pode conter segredo)."""


class UnreadableError(OSError):
    """Arquivo existe, mas não pôde ser lido (permissão ou erro de I/O)."""


class GuardTripped(RuntimeError):
    """Host saturado: a coleta para antes de pressionar o bot OpenClaw (PSI e CPU altos)."""


class Host(Protocol):
    def read(self, path: str) -> str | None: ...

    def subdirs(self, path: str) -> list[str]: ...

    def exists(self, path: str) -> bool: ...

    def statvfs(self, path: str) -> tuple[int, int] | None: ...

    def run(self, argv: list[str]) -> bytes: ...

    def monotonic(self) -> float: ...

    def sleep(self, seconds: float) -> None: ...


def require_read_only(argv: list[str]) -> None:
    """Recusa tudo que não seja leitura conhecida; a forma exata do comando é o contrato."""
    if tuple(argv) in EXACT_COMMANDS:
        return
    if argv[:2] == ["docker", "inspect"] and len(argv) > 2 and all(CONTAINER_ID.match(i) for i in argv[2:]):
        return
    if argv[:2] == ["docker", "compose"] and _is_compose_config(argv[2:]):
        return
    raise CommandError("comando fora da allowlist somente leitura: " + " ".join(argv[:3]))


def _is_compose_config(rest: list[str]) -> bool:
    index = 0
    while index < len(rest) and rest[index] in COMPOSE_VALUE_FLAGS:
        if index + 1 >= len(rest) or rest[index + 1].startswith("-") or not rest[index + 1]:
            return False
        index += 2
    return rest[index:] == ["config", "--format", "json"]


class LocalHost:
    """Leituras reais do host; comandos só pela allowlist, com timeout, sem shell."""

    def read(self, path: str) -> str | None:
        try:
            return Path(path).read_text(encoding="utf-8", errors="replace")
        except FileNotFoundError:
            return None
        except OSError as error:
            raise UnreadableError(path) from error

    def subdirs(self, path: str) -> list[str]:
        try:
            # Segue symlinks: /sys/block/* são links; cgroupfs não tem links, então não há ciclo.
            return sorted(entry.name for entry in os.scandir(path) if entry.is_dir())
        except OSError:
            return []

    def exists(self, path: str) -> bool:
        return os.path.exists(path)

    def statvfs(self, path: str) -> tuple[int, int] | None:
        try:
            info = os.statvfs(path)
        except OSError:
            return None
        return info.f_blocks * info.f_frsize, info.f_bavail * info.f_frsize

    def run(self, argv: list[str]) -> bytes:
        require_read_only(argv)
        try:
            result = subprocess.run(argv, capture_output=True, timeout=COMMAND_TIMEOUT_SECONDS, check=False)
        except (OSError, subprocess.TimeoutExpired) as error:
            raise CommandError(f"{argv[0]} {argv[1] if len(argv) > 1 else ''} falhou: {type(error).__name__}") \
                from error
        if result.returncode != 0:
            raise CommandError(f"{' '.join(argv[:2])} terminou com código {result.returncode}")
        return result.stdout

    def monotonic(self) -> float:
        import time
        return time.monotonic()

    def sleep(self, seconds: float) -> None:
        import time
        time.sleep(seconds)


# ---------------------------------------------------------------- coleta


@dataclass(frozen=True)
class Guard:
    """Para a coleta se PSI de CPU e CPU ocupada passarem dos tetos ao mesmo tempo (margem abaixo do OpenClaw)."""

    max_cpu_psi: float = 45.0
    max_busy_percent: float = 75.0
    wait_seconds: float = 180.0
    poll_seconds: float = 5.0

    def check(self, host: Host, phase: str, checks: list[dict[str, Any]]) -> None:
        started = host.monotonic()
        while True:
            psi = read_psi(host, "cpu").get("some", {}).get("avg10", 0.0)
            record: dict[str, Any] = {"phase": phase, "cpu_psi_avg10": psi, "non_idle_percent": None}
            if psi <= self.max_cpu_psi:
                checks.append(record)
                return
            first = read_cpu_ticks(host)
            host.sleep(1.0)
            sample = cpu_percentages(first, read_cpu_ticks(host), 1.0)
            record["non_idle_percent"] = sample["non_idle_percent"]
            if (sample["non_idle_percent"] or 0.0) <= self.max_busy_percent:
                checks.append(record)
                return
            if host.monotonic() - started >= self.wait_seconds:
                raise GuardTripped(f"fase {phase}: PSI CPU avg10={psi} e CPU {sample['non_idle_percent']}%")
            host.sleep(self.poll_seconds)


def collect(host: Host, *, sample_seconds: float, guard: Guard, render_pause_seconds: float = 0.5,
            source_sha256: str | None = None) -> dict[str, Any]:
    checks: list[dict[str, Any]] = []
    guard.check(host, "start", checks)
    started = host.monotonic()
    pressure_start = {name: read_psi(host, name) for name in ("cpu", "memory", "io")}
    ticks_start, disks_start = read_cpu_ticks(host), read_diskstats(host)
    tree_start = walk_cgroups(host)
    docker = docker_facts(host)
    containers = inspect_containers(host)
    renders = render_compose(host, containers, guard, checks, render_pause_seconds)
    guard.check(host, "final-sample", checks)
    host.sleep(max(0.0, sample_seconds - (host.monotonic() - started)))
    window = host.monotonic() - started
    ticks_end, disks_end = read_cpu_ticks(host), read_diskstats(host)
    tree = walk_cgroups(host)
    document = build_document(host, tree, tree_start, containers, renders, docker, window)
    document["host"]["pressure"] = {"start": pressure_start,
                                    "end": {name: read_psi(host, name) for name in ("cpu", "memory", "io")}}
    document["host"]["cpu_sample"] = cpu_percentages(ticks_start, ticks_end, window)
    document["host"]["disks"] = disk_facts(host, disks_start, disks_end, window, docker.get("root_dir"))
    document["guard"] = {"max_cpu_psi": guard.max_cpu_psi, "max_busy_percent": guard.max_busy_percent,
                         "checks": checks}
    document["parameters"] = {"sample_seconds": sample_seconds, "collector_source_sha256": source_sha256}
    document["collected_at"] = datetime.now(UTC).replace(microsecond=0).isoformat()
    document["content_sha256"] = content_hash(document)
    return document


@dataclass
class Node:
    path: str
    files: dict[str, str | None]
    children: list[str] = field(default_factory=list)

    @property
    def controllers(self) -> set[str]:
        return set((self.files.get("cgroup.controllers") or "").split())

    @property
    def populated(self) -> bool:
        if self.path == "/":
            return True
        return "populated 1" in (self.files.get("cgroup.events") or "")

    @property
    def has_procs(self) -> bool:
        procs = self.files.get("cgroup.procs")
        return bool(procs and procs != UNREADABLE and procs.strip())


def cgroup_dir(path: str) -> str:
    return CGROUP_ROOT if path == "/" else CGROUP_ROOT + path


def walk_cgroups(host: Host) -> dict[str, Node]:
    nodes: dict[str, Node] = {}
    pending = ["/"]
    while pending:
        path = pending.pop()
        directory = cgroup_dir(path)
        nodes[path] = Node(path, {name: _read_tolerant(host, f"{directory}/{name}") for name in NODE_FILES})
        for child in host.subdirs(directory):
            child_path = ("" if path == "/" else path) + "/" + child
            nodes[path].children.append(child_path)
            pending.append(child_path)
        if len(nodes) > MAX_CGROUP_NODES:
            raise RuntimeError(f"árvore de cgroups acima de {MAX_CGROUP_NODES} nós; coleta interrompida")
    return nodes


def _read_tolerant(host: Host, path: str) -> str | None:
    try:
        return host.read(path)
    except UnreadableError:
        return UNREADABLE


def docker_facts(host: Host) -> dict[str, Any]:
    info = json.loads(host.run(["docker", "info", "--format", "{{json .}}"]))
    compose = host.run(["docker", "compose", "version", "--short"]).decode().strip()
    return {"server_version": info.get("ServerVersion"), "cgroup_driver": info.get("CgroupDriver"),
            "cgroup_version": info.get("CgroupVersion"), "root_dir": info.get("DockerRootDir"),
            "storage_driver": info.get("Driver"), "compose_version": compose}


def inspect_containers(host: Host) -> list[dict[str, Any]]:
    ids = host.run(["docker", "ps", "--all", "--quiet", "--no-trunc"]).decode().split()
    if not ids:
        return []
    return json.loads(host.run(["docker", "inspect", *ids]))


@dataclass(frozen=True)
class ComposeGroup:
    project: str
    working_dir: str
    config_files: tuple[str, ...]
    environment_files: tuple[str, ...]

    def argv(self) -> list[str]:
        argv = ["docker", "compose", "--project-name", self.project, "--project-directory", self.working_dir]
        for env in self.environment_files:
            argv += ["--env-file", env]
        for file in self.config_files:
            argv += ["--file", file]
        return argv + ["config", "--format", "json"]


def compose_group(labels: dict[str, str]) -> ComposeGroup | None:
    project, workdir, files = (labels.get(k) for k in ("com.docker.compose.project",
                                                        "com.docker.compose.project.working_dir",
                                                        "com.docker.compose.project.config_files"))
    if not (project and workdir and files):
        return None
    env = labels.get("com.docker.compose.project.environment_file")
    return ComposeGroup(project, workdir, tuple(files.split(",")), tuple(env.split(",")) if env else ())


@dataclass(frozen=True)
class Render:
    document: dict[str, Any] | None
    status: str


def render_compose(host: Host, containers: list[dict[str, Any]], guard: Guard, checks: list[dict[str, Any]],
                   pause_seconds: float) -> dict[ComposeGroup, Render]:
    """Renderiza cada grupo Compose uma vez; o JSON (com segredos interpolados) não sai desta memória."""
    groups = {g for g in (compose_group(_labels(c)) for c in containers) if g is not None}
    renders: dict[ComposeGroup, Render] = {}
    for group in sorted(groups, key=lambda g: (g.project, g.config_files, g.environment_files)):
        missing = [path for path in (*group.config_files, *group.environment_files) if not host.exists(path)]
        if missing:
            # Arquivo de env efêmero (staging do deploy) ou compose em /run já removido: o render exato é impossível.
            renders[group] = Render(None, "missing-file:" + missing[0])
            continue
        guard.check(host, "render:" + group.project, checks)
        try:
            renders[group] = Render(json.loads(host.run(group.argv())), "ok")
        except CommandError:
            renders[group] = Render(None, "command-failed")
        except ValueError:
            renders[group] = Render(None, "invalid-json")
        host.sleep(pause_seconds)
    return renders


# ---------------------------------------------------------------- limites


@dataclass(frozen=True)
class Limit:
    status: str
    value: float
    reason: str
    devices: dict[str, dict[str, int]] | None = None


def parse_cpu_max(raw: str) -> float | None:
    parts = raw.split()
    if len(parts) != 2:
        return None
    quota, period = parts
    if not period.isdigit() or int(period) == 0:
        return None
    if quota == "max":
        return INF
    return int(quota) / int(period) if quota.isdigit() else None


def parse_io_max(raw: str) -> dict[str, dict[str, int]] | None:
    devices: dict[str, dict[str, int]] = {}
    for line in raw.splitlines():
        parts = line.split()
        if not parts:
            continue
        finite = {}
        for item in parts[1:]:
            key, _, value = item.partition("=")
            if value != "max":
                if not value.isdigit():
                    return None
                finite[key] = int(value)
        if finite:
            devices[parts[0]] = finite
    return devices


def local_limit(node: Node, resource: str) -> Limit:
    if node.path == "/":
        return Limit(UNBOUNDED, INF, "root")
    filename, controller, _ = RESOURCES[resource]
    raw = node.files.get(filename)
    if raw == UNREADABLE:
        return Limit(UNKNOWN, math.nan, "unreadable")
    if raw is None:
        if controller not in node.controllers:
            return Limit(UNBOUNDED, INF, "controller-disabled")
        return Limit(UNKNOWN, math.nan, "absent")
    text = raw.strip()
    if resource == "io":
        devices = parse_io_max(text)
        if devices is None:
            return Limit(UNKNOWN, math.nan, "unparseable")
        return Limit(FINITE, math.nan, "io.max", devices) if devices else Limit(UNBOUNDED, INF, "max")
    value = parse_cpu_max(text) if resource == "cpu" else (INF if text == "max" else _int_or_none(text))
    if value is None:
        return Limit(UNKNOWN, math.nan, "unparseable")
    return Limit(UNBOUNDED, INF, "max") if value == INF else Limit(FINITE, float(value), filename)


def _int_or_none(text: str) -> int | None:
    return int(text) if text.isdigit() else None


def ancestors(path: str) -> list[str]:
    chain = [path]
    while chain[-1] != "/":
        parent = chain[-1].rsplit("/", 1)[0]
        chain.append(parent or "/")
    return chain


def effective_limit(nodes: dict[str, Node], path: str, resource: str) -> dict[str, Any]:
    """O teto efetivo é o menor finito entre o nó e todos os ancestrais; UNKNOWN só sem nenhum finito."""
    limits = [(p, local_limit(nodes[p], resource)) for p in ancestors(path) if p in nodes]
    unknown = [p for p, limit in limits if limit.status == UNKNOWN]
    finite = [(p, limit) for p, limit in limits if limit.status == FINITE]
    if resource == "io" and finite:
        merged: dict[str, dict[str, int]] = {}
        for _, limit in finite:
            for device, values in (limit.devices or {}).items():
                for key, value in values.items():
                    merged.setdefault(device, {})[key] = min(value, merged.get(device, {}).get(key, value))
        return {"status": FINITE, "devices": merged, "binding": [p for p, _ in finite], "unknown": unknown}
    if finite:
        binding, limit = min(finite, key=lambda item: item[1].value)
        return {"status": FINITE, **encode(resource, limit.value), "binding": binding, "unknown": unknown}
    if unknown:
        return {"status": UNKNOWN, "unknown": unknown}
    return {"status": UNBOUNDED, "reason": limits[0][1].reason}


def encode(resource: str, value: float) -> dict[str, Any]:
    key = RESOURCES[resource][2]
    if resource == "cpu":
        return {key: round(value, 6)}
    return {key: int(value)}


def describe_limit(limit: Limit, resource: str) -> dict[str, Any]:
    described: dict[str, Any] = {"status": limit.status, "reason": limit.reason}
    if limit.status == FINITE:
        described.update({"devices": limit.devices} if resource == "io" else encode(resource, limit.value))
    return described


# ---------------------------------------------------------------- orçamento sem dupla contagem


@dataclass
class Budget:
    value: float = 0.0
    finite_part: float = 0.0
    unbounded: list[str] = field(default_factory=list)
    unknown: list[str] = field(default_factory=list)


def subtree_budget(nodes: dict[str, Node], path: str, resource: str, memo: dict[str, Budget]) -> Budget:
    """Orçamento = min(teto do nó, soma dos filhos); limite aninhado conta uma vez, pelo ancestral mais justo."""
    if path in memo:
        return memo[path]
    node = nodes[path]
    result = Budget()
    if node.populated:
        for child in node.children:
            part = subtree_budget(nodes, child, resource, memo)
            result.value += part.value
            result.finite_part += part.finite_part
            result.unbounded += part.unbounded
            result.unknown += part.unknown
        if node.has_procs and path != "/":
            result.value = INF
            result.unbounded.append(path)
        limit = local_limit(node, resource)
        if limit.status == UNKNOWN:
            result.unknown.append(path)
        elif limit.status == FINITE:
            result.value = min(limit.value, result.value)
            result.finite_part = result.value
            result.unbounded = []
    memo[path] = result
    return result


def describe_budget(resource: str, parts: list[Budget]) -> dict[str, Any]:
    value = sum(p.value for p in parts)
    finite = sum(p.finite_part for p in parts)
    unbounded = sorted(path for p in parts for path in p.unbounded)
    unknown = sorted(path for p in parts for path in p.unknown)
    key = RESOURCES[resource][2]
    if value == INF:
        return {"status": UNBOUNDED, "unbounded": unbounded, f"finite_part_{key}": encode(resource, finite)[key],
                "unknown": unknown}
    return {"status": FINITE, **encode(resource, value), "unknown": unknown}


def classify_container(facts: dict[str, str]) -> str:
    for owner, key, pattern in CONTAINER_RULES:
        if re.search(pattern, facts.get(key, "")):
            return owner
    return "unassigned"


def classify_unit(path: str) -> str:
    for owner, pattern in UNIT_RULES:
        if re.search(pattern, path):
            return owner
    return "host"


def is_backup(name: str) -> bool:
    return bool(BACKUP_PATTERN.search(name))


def node_owners(nodes: dict[str, Node], leaf_owner: dict[str, str]) -> dict[str, str | None]:
    """Dono de um nó = dono comum de todas as folhas populadas abaixo dele; None quando misto."""
    owners: dict[str, str | None] = {}

    def visit(path: str) -> set[str]:
        node = nodes[path]
        found: set[str] = set()
        if not node.populated:
            return found
        for child in node.children:
            found |= visit(child)
        if node.has_procs and path != "/":
            found.add(leaf_owner.get(path) or classify_unit(path))
        owners[path] = next(iter(found)) if len(found) == 1 else None
        return found

    visit("/")
    return owners


def maximal_members(nodes: dict[str, Node], owners: dict[str, str | None]) -> dict[str, list[str]]:
    members: dict[str, list[str]] = {}
    pending = ["/"]
    while pending:
        path = pending.pop()
        if path not in owners:
            continue
        owner = owners[path]
        if owner is not None and path != "/":
            members.setdefault(owner, []).append(path)
        else:
            pending.extend(nodes[path].children)
    return {owner: sorted(paths) for owner, paths in sorted(members.items())}


def usage_number(node: Node, filename: str) -> int:
    raw = node.files.get(filename)
    return int(raw.strip()) if raw and raw.strip().isdigit() else 0


def stat_field(node: Node | None, filename: str, key: str) -> int:
    if node is None:
        return 0
    for line in (node.files.get(filename) or "").splitlines():
        name, _, value = line.partition(" ")
        if name == key and value.strip().isdigit():
            return int(value)
    return 0


def aggregate(nodes: dict[str, Node], before: dict[str, Node], paths: list[str], window: float,
              memos: dict[str, dict[str, Budget]]) -> dict[str, Any]:
    result: dict[str, Any] = {"members": paths}
    for resource in SUMMED:
        result[resource] = describe_budget(resource, [subtree_budget(nodes, p, resource, memos[resource])
                                                      for p in paths])
    result["memory_current_bytes"] = sum(usage_number(nodes[p], "memory.current") for p in paths)
    used = sum(stat_field(nodes[p], "cpu.stat", "usage_usec") - stat_field(before.get(p), "cpu.stat", "usage_usec")
               for p in paths if p in before)
    result["cpu_used_vcpu"] = round(used / 1_000_000 / window, 3) if window > 0 else None
    return result


# ---------------------------------------------------------------- cadeia declarado -> cgroup


def parse_bytes(raw: Any) -> int | None:
    if isinstance(raw, bool):
        return None
    if isinstance(raw, int):
        return raw if raw >= 0 else None
    match = re.fullmatch(r"(\d+(?:\.\d+)?)\s*([bkmgt]?)(i?b)?", str(raw).strip(), re.IGNORECASE)
    if match is None:
        return None
    return int(float(match[1]) * 1024 ** "bkmgt".index((match[2] or "b").lower()))


def shares_to_weight(shares: int | None) -> int:
    """cpu.shares (Docker) -> cpu.weight (cgroup v2) pela fórmula quadrática do runc atual (1024 -> 100).

    A linear antiga (1024 -> 39) não bate com o host: os pares 64/512/768/2048 -> 12/59/80/174 foram lidos na VPS.
    """
    if not shares:
        return 100
    if shares <= MIN_CPU_SHARES:
        return 1
    if shares >= MAX_CPU_SHARES:
        return MAX_CPU_WEIGHT
    exponent = math.log2(shares)
    return math.ceil(10 ** ((exponent * exponent + 125 * exponent) / 612.0 - 7.0 / 34.0))


def service_resources(service: dict[str, Any]) -> dict[str, Any]:
    limits = ((service.get("deploy") or {}).get("resources") or {}).get("limits") or {}
    memory_raw = limits.get("memory", service.get("mem_limit"))
    cpu_raw = limits.get("cpus", service.get("cpus"))
    pids_raw = limits.get("pids", service.get("pids_limit"))
    shares_raw = service.get("cpu_shares")
    return {
        "memory": None if memory_raw is None else (parse_bytes(memory_raw) if parse_bytes(memory_raw) else UNKNOWN),
        "cpu": None if cpu_raw is None else _float_or_unknown(cpu_raw),
        "pids": None if pids_raw is None else _pids(pids_raw),
        "cpu_weight": shares_to_weight(None) if shares_raw is None else _weight(shares_raw),
        "io": bool(service.get("blkio_config")),
    }


def _float_or_unknown(raw: Any) -> float | str:
    try:
        value = float(raw)
    except (TypeError, ValueError):
        return UNKNOWN
    return value if value > 0 else UNKNOWN


def _pids(raw: Any) -> int | str | None:
    try:
        value = int(raw)
    except (TypeError, ValueError):
        return UNKNOWN
    return value if value > 0 else None


def _weight(raw: Any) -> int | str:
    try:
        return shares_to_weight(int(raw))
    except (TypeError, ValueError):
        return UNKNOWN


def host_config_resources(config: dict[str, Any]) -> dict[str, Any]:
    nano, quota = config.get("NanoCpus") or 0, config.get("CpuQuota") or 0
    cpu = nano / 1e9 if nano > 0 else (quota / (config.get("CpuPeriod") or DEFAULT_CPU_PERIOD_USEC)
                                       if quota > 0 else None)
    pids = config.get("PidsLimit")
    throttles = [k for k in ("BlkioDeviceReadBps", "BlkioDeviceWriteBps", "BlkioDeviceReadIOps",
                             "BlkioDeviceWriteIOps") if config.get(k)]
    return {"memory": config.get("Memory") or None, "cpu": cpu, "pids": pids if pids and pids > 0 else None,
            "cpu_weight": shares_to_weight(config.get("CpuShares") or None),
            "io": bool(throttles or config.get("BlkioWeight"))}


def cgroup_resources(node: Node | None) -> dict[str, Any]:
    if node is None:
        return {key: UNKNOWN for key in ("memory", "cpu", "pids", "cpu_weight", "io")}
    resources: dict[str, Any] = {}
    for resource in ("memory", "cpu", "pids", "io"):
        limit = local_limit(node, resource)
        if resource == "io":
            resources["io"] = UNKNOWN if limit.status == UNKNOWN else limit.status == FINITE
        else:
            resources[resource] = {FINITE: limit.value, UNBOUNDED: None}.get(limit.status, UNKNOWN)
    weight = (node.files.get("cpu.weight") or "").strip()
    resources["cpu_weight"] = int(weight) if weight.isdigit() else UNKNOWN
    if isinstance(resources["memory"], float):
        resources["memory"] = int(resources["memory"])
    if isinstance(resources["pids"], float):
        resources["pids"] = int(resources["pids"])
    return resources


def same(resource: str, left: Any, right: Any) -> bool:
    if left is None or right is None:
        return left is right
    if resource == "memory":
        return abs(left - right) < PAGE_BYTES
    if resource == "cpu":
        return math.isclose(left, right, abs_tol=1e-6)
    return left == right


def chain_state(resource: str, values: dict[str, Any], stages: tuple[str, ...], consistent: str) -> str:
    known = [(stage, values[stage]) for stage in stages if values[stage] != UNKNOWN]
    for (left_stage, left), (right_stage, right) in zip(known, known[1:]):
        if left is None and right is not None and right_stage == "cgroup":
            return "cgroup-only"  # Teto só no cgroup (ex.: DefaultTasksMax do systemd), não pedido pelo Docker.
        if not same(resource, left, right):
            return f"drift:{left_stage}->{right_stage}"
    unknown = [stage for stage in stages if values[stage] == UNKNOWN]
    return f"unknown:{unknown[0]}" if unknown else consistent


def limit_chain(declared: dict[str, Any] | str, rendered: dict[str, Any] | str, host_config: dict[str, Any],
                cgroup: dict[str, Any]) -> dict[str, Any]:
    chain: dict[str, Any] = {}
    for resource in ("memory", "cpu", "pids", "cpu_weight", "io"):
        values = {"declared": declared if isinstance(declared, str) else declared[resource],
                  "rendered": rendered if isinstance(rendered, str) else rendered[resource],
                  "host_config": host_config[resource], "cgroup": cgroup[resource]}
        if declared == NOT_COMPOSE:
            state = chain_state(resource, values, ("host_config", "cgroup"), "not-compose")
        else:
            state = chain_state(resource, values, STAGES, "consistent")
        if resource == "io" and state == "consistent" and any(v is True for v in values.values()):
            state = "manual-review"
        chain[resource] = {"values": values, "state": state}
    return chain


def declared_services(host: Host, group: ComposeGroup) -> tuple[dict[str, Any] | None, str]:
    services: dict[str, Any] = {}
    for file in group.config_files:
        try:
            text = host.read(file)
        except UnreadableError:
            return None, "unreadable:" + file
        if text is None:
            return None, "missing-file:" + file
        document = _parse_compose(file, text)
        if not isinstance(document, dict):
            return None, "unparseable:" + file
        for name, service in (document.get("services") or {}).items():
            merged = services.setdefault(name, {})
            for key in ("mem_limit", "cpus", "pids_limit", "cpu_shares", "blkio_config"):
                if key in service:
                    merged[key] = service[key]
            limits = ((service.get("deploy") or {}).get("resources") or {}).get("limits")
            if limits:
                merged.setdefault("deploy", {"resources": {"limits": {}}})["resources"]["limits"].update(limits)
    return services, "ok"


def _parse_compose(file: str, text: str) -> Any:
    try:
        if file.endswith(".json"):
            return json.loads(text)
        return importlib.import_module("yaml").safe_load(text)
    except Exception:  # noqa: BLE001 - qualquer falha de parse vira UNKNOWN, sem ecoar conteúdo (pode ter segredo).
        return None


# ---------------------------------------------------------------- documento


def _labels(item: dict[str, Any]) -> dict[str, str]:
    return (item.get("Config") or {}).get("Labels") or {}


def container_cgroup(host: Host, item: dict[str, Any]) -> str | None:
    state = item.get("State") or {}
    pid = state.get("Pid") or 0
    if not state.get("Running") or pid <= 0:
        return None
    for line in (_read_tolerant(host, f"/proc/{pid}/cgroup") or "").splitlines():
        if line.startswith("0::"):
            return line[3:].strip() or "/"
    return None


def build_document(host: Host, tree: dict[str, Node], before: dict[str, Node], containers: list[dict[str, Any]],
                   renders: dict[ComposeGroup, Render], docker: dict[str, Any],
                   window: float) -> dict[str, Any]:
    leaf_owner: dict[str, str] = {}
    records = []
    declared_cache: dict[ComposeGroup, tuple[dict[str, Any] | None, str]] = {}
    for item in containers:
        record, path = container_record(host, tree, item, renders, declared_cache)
        records.append(record)
        if path is not None:
            leaf_owner[path] = record["owner"]
    records.sort(key=lambda r: r["name"])
    owners = node_owners(tree, leaf_owner)
    members = maximal_members(tree, owners)
    memos: dict[str, dict[str, Budget]] = {resource: {} for resource in SUMMED}
    container_by_path = {r["cgroup"]["path"]: r["name"] for r in records if r["cgroup"]}
    cgroups = [cgroup_record(tree, path, owners, container_by_path) for path in sorted(tree)
               if tree[path].populated and path != "/"]
    budget = {
        "total": {resource: describe_budget(resource, [subtree_budget(tree, "/", resource, memos[resource])])
                  for resource in SUMMED},
        "owners": {owner: aggregate(tree, before, paths, window, memos) for owner, paths in members.items()},
        "stopped_containers": sorted(r["name"] for r in records if not r["running"]),
        "expected_owners_missing": sorted(set(EXPECTED_OWNERS) - set(members)),
        "physical": {"mem_total_bytes": meminfo_bytes(host, "MemTotal"), "cpus": cpu_count(host)},
    }
    backup_paths = sorted(c["path"] for c in cgroups if c["has_procs"] and c["backup"])
    return {
        "schema": SCHEMA,
        "host": {"identity": identity(host, docker)},
        "containers": records,
        "cgroups": cgroups,
        "budget": budget,
        "compose_renders": [{"project": g.project, "working_dir": g.working_dir, "config_files": list(g.config_files),
                             "environment_files": list(g.environment_files), "status": r.status,
                             "declared": declared_cache[g][1] if g in declared_cache else None}
                            for g, r in sorted(renders.items(), key=lambda i: (i[0].project, i[0].config_files,
                                                                               i[0].environment_files))],
        "backups": backups(host, records, backup_paths, tree, before, window, memos),
        "findings": findings(records, cgroups),
    }


def container_record(host: Host, tree: dict[str, Node], item: dict[str, Any],
                     renders: dict[ComposeGroup, Render],
                     declared_cache: dict[ComposeGroup, tuple[dict[str, Any] | None, str]]) -> tuple[dict[str, Any], str | None]:
    labels = _labels(item)
    name = (item.get("Name") or "").lstrip("/")
    facts = {short: labels[key] for key, short in ALLOWED_LABELS.items() if key in labels}
    state = item.get("State") or {}
    config = item.get("HostConfig") or {}
    path = container_cgroup(host, item)
    node = tree.get(path) if path else None
    group = compose_group(labels)
    service = labels.get("com.docker.compose.service", "")
    if group is None:
        declared: dict[str, Any] | str = NOT_COMPOSE if "com.docker.compose.project" not in labels else UNKNOWN
        rendered: dict[str, Any] | str = declared
    else:
        if group not in declared_cache:
            declared_cache[group] = declared_services(host, group)
        raw_services = declared_cache[group][0]
        declared = UNKNOWN if raw_services is None else service_resources(raw_services.get(service, {}))
        render = renders[group].document if group in renders else None
        rendered = UNKNOWN if render is None else service_resources((render.get("services") or {}).get(service, {}))
    record: dict[str, Any] = {
        "name": name, "id": (item.get("Id") or "")[:12],
        "owner": classify_container({**facts, "name": name}), "backup": is_backup(name),
        "running": bool(state.get("Running")), "status": state.get("Status"),
        "oom_killed": bool(state.get("OOMKilled")), "restart_count": state.get("RestartCount", 0),
        "image": (item.get("Config") or {}).get("Image"), "labels": facts,
        "host_config": {key: config.get(key) for key in ("NanoCpus", "CpuQuota", "CpuPeriod", "CpuShares", "Memory",
                                                         "MemoryReservation", "MemorySwap", "PidsLimit",
                                                         "BlkioWeight", "CgroupParent")},
        "chain": limit_chain(declared, rendered, host_config_resources(config), cgroup_resources(node)),
        "cgroup": None if node is None or path is None else cgroup_detail(tree, path),
    }
    return record, path if node is not None else None


def cgroup_detail(tree: dict[str, Node], path: str) -> dict[str, Any]:
    node = tree[path]
    return {
        "path": path,
        "local": {resource: describe_limit(local_limit(node, resource), resource) for resource in RESOURCES},
        "effective": {resource: effective_limit(tree, path, resource) for resource in RESOURCES},
        "ancestors": [{"path": p, "limits": {f: tree[p].files.get(f) for f in LIMIT_FILES}}
                      for p in ancestors(path) if p in tree],
        "usage": usage(node),
    }


def usage(node: Node) -> dict[str, Any]:
    return {"memory_current_bytes": usage_number(node, "memory.current"),
            "memory_peak_bytes": usage_number(node, "memory.peak"),
            "swap_current_bytes": usage_number(node, "memory.swap.current"),
            "pids_current": usage_number(node, "pids.current"),
            "nr_periods": stat_field(node, "cpu.stat", "nr_periods"),
            "nr_throttled": stat_field(node, "cpu.stat", "nr_throttled"),
            "throttled_usec": stat_field(node, "cpu.stat", "throttled_usec"),
            "oom_kill": stat_field(node, "memory.events", "oom_kill"),
            "memory_max_events": stat_field(node, "memory.events", "max")}


def cgroup_record(tree: dict[str, Node], path: str, owners: dict[str, str | None],
                  container_by_path: dict[str, str]) -> dict[str, Any]:
    node = tree[path]
    record: dict[str, Any] = {"path": path, "owner": owners.get(path), "has_procs": node.has_procs,
                              "container": container_by_path.get(path),
                              "backup": is_backup(container_by_path.get(path) or path),
                              "limits": {f: node.files.get(f) for f in LIMIT_FILES}, "usage": usage(node)}
    if node.has_procs:
        record["effective"] = {resource: effective_limit(tree, path, resource) for resource in RESOURCES}
    return record


def backups(host: Host, records: list[dict[str, Any]], paths: list[str], tree: dict[str, Node],
            before: dict[str, Node], window: float, memos: dict[str, dict[str, Budget]]) -> dict[str, Any]:
    try:
        timers = json.loads(host.run(["systemctl", "list-timers", "--all", "--no-pager", "--output=json"]))
    except (CommandError, ValueError):
        timers = None
    return {
        "timers": None if timers is None else sorted(
            ({k: t.get(k) for k in ("unit", "activates", "last", "next")} for t in timers
             if is_backup(f"{t.get('unit', '')} {t.get('activates', '')}")), key=lambda t: t["unit"] or ""),
        "containers": sorted(r["name"] for r in records if r["backup"]),
        "cgroups": paths,
        "budget": aggregate(tree, before, paths, window, memos) if paths else None,
        "coolify_scheduled_backups": {"status": UNKNOWN,
                                      "reason": "exige consulta ao banco do Coolify; fora do C1 somente leitura"},
    }


def findings(records: list[dict[str, Any]], cgroups: list[dict[str, Any]]) -> dict[str, Any]:
    leaves = [(c.get("container") or c["path"], c["owner"], c["effective"]) for c in cgroups if "effective" in c]
    result: dict[str, Any] = {}
    for status in (UNBOUNDED, UNKNOWN):
        result[status.lower()] = {resource: sorted(f"{name} ({owner})" for name, owner, eff in leaves
                                                   if eff[resource]["status"] == status)
                                  for resource in RESOURCES}
    result["drift"] = sorted(f"{r['name']}: {res} {item['state']}" for r in records
                             for res, item in r["chain"].items() if item["state"].startswith("drift"))
    result["oom_kill"] = sorted(c["path"] for c in cgroups if c["usage"]["oom_kill"] > 0)
    result["throttled"] = sorted(f"{c.get('container') or c['path']}: {c['usage']['nr_throttled']}/"
                                 f"{c['usage']['nr_periods']}" for c in cgroups
                                 if c["has_procs"] and c["usage"]["nr_throttled"] > 0)
    return result


# ---------------------------------------------------------------- fatos do host


def read_psi(host: Host, resource: str) -> dict[str, dict[str, float]]:
    result: dict[str, dict[str, float]] = {}
    for line in (_read_tolerant(host, f"/proc/pressure/{resource}") or "").splitlines():
        kind, *pairs = line.split()
        values = {}
        for pair in pairs:
            key, _, value = pair.partition("=")
            if key.startswith("avg"):
                values[key] = float(value)
        result[kind] = values
    return result


def read_cpu_ticks(host: Host) -> list[int]:
    for line in (host.read("/proc/stat") or "").splitlines():
        if line.startswith("cpu "):
            return [int(v) for v in line.split()[1:9]]
    return [0] * 8


def cpu_percentages(first: list[int], second: list[int], window: float) -> dict[str, Any]:
    delta = [b - a for a, b in zip(first, second)]
    total = sum(delta)
    if total <= 0:
        return {"window_seconds": round(window, 3), "busy_percent": None, "steal_percent": None,
                "iowait_percent": None, "non_idle_percent": None}
    idle, iowait, steal = delta[3], delta[4], delta[7]

    def percent(value: int) -> float:
        return round(100 * value / total, 2)

    return {"window_seconds": round(window, 3), "busy_percent": percent(total - idle - iowait - steal),
            "steal_percent": percent(steal), "iowait_percent": percent(iowait),
            "non_idle_percent": percent(total - idle - iowait)}


def read_diskstats(host: Host) -> dict[str, list[int]]:
    stats = {}
    for line in (host.read("/proc/diskstats") or "").splitlines():
        parts = line.split()
        if len(parts) >= 14:
            stats[parts[2]] = [int(v) for v in parts[3:14]]
    return stats


def disk_facts(host: Host, first: dict[str, list[int]], second: dict[str, list[int]], window: float,
               docker_root: str | None) -> dict[str, Any]:
    devices = []
    for name in host.subdirs("/sys/block"):
        if IGNORED_BLOCK_DEVICES.match(name):
            continue
        base = f"/sys/block/{name}"
        scheduler = re.search(r"\[(\S+)\]", _read_tolerant(host, base + "/queue/scheduler") or "")
        device: dict[str, Any] = {"name": name,
                                  "size_bytes": int((_read_tolerant(host, base + "/size") or "0").strip() or 0) * 512,
                                  "rotational": (_read_tolerant(host, base + "/queue/rotational") or "").strip(),
                                  "scheduler": scheduler[1] if scheduler else None}
        if name in first and name in second and window > 0:
            delta = [b - a for a, b in zip(first[name], second[name])]
            device.update({"observed_read_iops": round(delta[0] / window, 2),
                           "observed_write_iops": round(delta[4] / window, 2),
                           "observed_util_percent": round(100 * delta[9] / (window * 1000), 2)})
        devices.append(device)
    filesystems = {}
    for mount in sorted({"/", docker_root or "/var/lib/docker"}):
        sizes = host.statvfs(mount)
        filesystems[mount] = None if sizes is None else {"total_bytes": sizes[0], "available_bytes": sizes[1]}
    return {"devices": devices, "filesystems": filesystems,
            "iops_capability": {"status": UNKNOWN, "reason": "benchmark não executado: C1 é somente leitura e o "
                                                             "host compartilhado não admite fio/carga"}}


def meminfo_bytes(host: Host, key: str) -> int | None:
    for line in (host.read("/proc/meminfo") or "").splitlines():
        name, _, rest = line.partition(":")
        if name == key:
            return int(rest.split()[0]) * 1024
    return None


def cpu_count(host: Host) -> int:
    return sum(1 for line in (host.read("/proc/cpuinfo") or "").splitlines() if line.startswith("processor"))


def identity(host: Host, docker: dict[str, Any]) -> dict[str, Any]:
    def text(path: str) -> str | None:
        value = _read_tolerant(host, path)
        return value.strip() if value is not None else None

    machine_id = text("/etc/machine-id")
    os_release = dict(re.findall(r'^(\w+)="?([^"\n]*)"?$', host.read("/etc/os-release") or "", re.MULTILINE))
    btime = re.search(r"^btime (\d+)$", host.read("/proc/stat") or "", re.MULTILINE)
    models = sorted({line.split(":", 1)[1].strip() for line in (host.read("/proc/cpuinfo") or "").splitlines()
                     if line.startswith("model name")})
    try:
        virt = host.run(["systemd-detect-virt"]).decode().strip()
    except CommandError:
        virt = UNKNOWN
    return {
        "hostname": text("/proc/sys/kernel/hostname"),
        "machine_id_sha256": hashlib.sha256(machine_id.encode()).hexdigest() if machine_id else None,
        "boot_id": text("/proc/sys/kernel/random/boot_id"),
        "boot_time_epoch": int(btime[1]) if btime else None,
        "uptime_seconds": float((text("/proc/uptime") or "0").split()[0]),
        "kernel": text("/proc/sys/kernel/osrelease"), "kernel_version": text("/proc/sys/kernel/version"),
        "os": os_release.get("PRETTY_NAME"), "virtualization": virt,
        "cpu": {"count": cpu_count(host), "models": models},
        "memory": {"mem_total_bytes": meminfo_bytes(host, "MemTotal"),
                   "mem_available_bytes": meminfo_bytes(host, "MemAvailable"),
                   "swap_total_bytes": meminfo_bytes(host, "SwapTotal")},
        "pid_max": int(text("/proc/sys/kernel/pid_max") or 0),
        "cgroup": {"v2": host.read(CGROUP_ROOT + "/cgroup.controllers") is not None,
                   "root_controllers": (text(CGROUP_ROOT + "/cgroup.controllers") or "").split(),
                   "root_subtree_control": (text(CGROUP_ROOT + "/cgroup.subtree_control") or "").split()},
        "docker": docker,
    }


# ---------------------------------------------------------------- hash e resumo


def content_hash(document: dict[str, Any]) -> str:
    body = {key: value for key, value in document.items() if key != "content_sha256"}
    canonical = json.dumps(body, sort_keys=True, separators=(",", ":"), ensure_ascii=False, allow_nan=False)
    return hashlib.sha256(canonical.encode()).hexdigest()


def verify(document: dict[str, Any]) -> bool:
    return document.get("content_sha256") == content_hash(document)


def _mib(value: Any) -> str:
    return "?" if value is None else f"{value / 1024**2:,.0f} MiB"


def _budget_text(resource: str, item: dict[str, Any]) -> str:
    key = RESOURCES[resource][2]
    if item["status"] == FINITE:
        value = item[key]
        text = _mib(value) if resource == "memory" else f"{value}"
    else:
        finite = item[f"finite_part_{key}"]
        text = (f"UNBOUNDED (parte finita {_mib(finite) if resource == 'memory' else finite}; "
                f"{len(item['unbounded'])} sem teto)")
    return text + (f" +{len(item['unknown'])} UNKNOWN" if item["unknown"] else "")


def summarize(document: dict[str, Any]) -> str:
    host = document["host"]
    ident, cpu, budget = host["identity"], host.get("cpu_sample", {}), document["budget"]
    lines = [
        f"# Inventário C1 (somente leitura) — {document.get('collected_at')}",
        f"content_sha256: {document['content_sha256']} (verificado: {verify(document)})",
        f"Host {ident['hostname']} | {ident['os']} | kernel {ident['kernel']} | {ident['virtualization']} | "
        f"{ident['cpu']['count']} vCPU {', '.join(ident['cpu']['models'])} | boot_id {ident['boot_id']}",
        f"MemTotal {_mib(ident['memory']['mem_total_bytes'])}, MemAvailable "
        f"{_mib(ident['memory']['mem_available_bytes'])}, swap {_mib(ident['memory']['swap_total_bytes'])}",
        f"Docker {ident['docker']['server_version']} ({ident['docker']['cgroup_driver']}, cgroup "
        f"v{ident['docker']['cgroup_version']}), Compose {ident['docker']['compose_version']}",
        f"CPU na janela {cpu.get('window_seconds')} s: ocupada {cpu.get('busy_percent')}%, steal "
        f"{cpu.get('steal_percent')}%, iowait {cpu.get('iowait_percent')}%; PSI CPU avg10 início/fim "
        f"{host['pressure']['start']['cpu'].get('some', {}).get('avg10')}/"
        f"{host['pressure']['end']['cpu'].get('some', {}).get('avg10')}",
        "Discos: " + "; ".join(f"{d['name']} {d['size_bytes'] / 1024**3:.0f} GiB r/w IOPS observados "
                                f"{d.get('observed_read_iops')}/{d.get('observed_write_iops')}"
                                for d in host["disks"]["devices"])
        + f" | capacidade IOPS: {host['disks']['iops_capability']['status']}",
        "",
        "## Orçamento sem dupla contagem (tetos efetivos, não consumo)",
        f"Total: memória {_budget_text('memory', budget['total']['memory'])}; CPU "
        f"{_budget_text('cpu', budget['total']['cpu'])} vCPU; PIDs {_budget_text('pids', budget['total']['pids'])}",
        "| Dono | Memória | CPU (vCPU) | PIDs | memory.current | CPU usada |",
        "|---|---|---|---|---:|---:|",
    ]
    for owner, item in budget["owners"].items():
        lines.append(f"| {owner} | {_budget_text('memory', item['memory'])} | {_budget_text('cpu', item['cpu'])} | "
                     f"{_budget_text('pids', item['pids'])} | {_mib(item['memory_current_bytes'])} | "
                     f"{item['cpu_used_vcpu']} |")
    if budget["expected_owners_missing"]:
        lines.append("Donos esperados sem cgroup populado: " + ", ".join(budget["expected_owners_missing"]))
    if budget["stopped_containers"]:
        lines.append("Containers parados (fora do orçamento): " + ", ".join(budget["stopped_containers"]))
    lines += ["", "## Achados"]
    found = document["findings"]
    for status in ("unbounded", "unknown"):
        for resource, names in found[status].items():
            if names:
                lines.append(f"- {status.upper()} {resource} ({len(names)}): {', '.join(names)}")
    for key in ("drift", "oom_kill", "throttled"):
        if found[key]:
            lines.append(f"- {key}: {'; '.join(found[key])}")
    pending = [f"{r['project']} (render {r['status']}, declarado {r['declared']})" for r in document["compose_renders"]
               if r["status"] != "ok" or r["declared"] != "ok"]
    if pending:
        lines.append("- Compose sem cadeia completa: " + "; ".join(pending))
    backup = document["backups"]
    lines += ["", "## Backups",
              f"Timers: {', '.join(t['unit'] for t in backup['timers'] or []) or 'nenhum'}; containers: "
              f"{', '.join(backup['containers']) or 'nenhum'}; cgroups: {', '.join(backup['cgroups']) or 'nenhum'}; "
              f"backups agendados do Coolify: {backup['coolify_scheduled_backups']['status']}"]
    return "\n".join(lines) + "\n"


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0] if __doc__ else None)
    commands = parser.add_subparsers(dest="command", required=True)
    run = commands.add_parser("collect", help="coleta na VPS e imprime o JSON no stdout")
    run.add_argument("--sample-seconds", type=float, default=10.0)
    run.add_argument("--max-cpu-psi", type=float, default=Guard.max_cpu_psi)
    run.add_argument("--max-busy-percent", type=float, default=Guard.max_busy_percent)
    run.add_argument("--wait-seconds", type=float, default=Guard.wait_seconds)
    run.add_argument("--source-sha256", default=None)
    show = commands.add_parser("summarize", help="verifica o hash e imprime o resumo")
    show.add_argument("file", type=Path)
    args = parser.parse_args(argv)
    if args.command == "summarize":
        document = json.loads(args.file.read_text(encoding="utf-8"))
        sys.stdout.write(summarize(document))
        return 0 if verify(document) else 4
    guard = Guard(args.max_cpu_psi, args.max_busy_percent, args.wait_seconds)
    try:
        document = collect(LocalHost(), sample_seconds=args.sample_seconds, guard=guard,
                           source_sha256=args.source_sha256)
    except GuardTripped as error:
        sys.stderr.write(f"coleta abortada pelo guarda: {error}\n")
        return 3
    json.dump(document, sys.stdout, indent=2, sort_keys=True, ensure_ascii=False, allow_nan=False)
    sys.stdout.write("\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
