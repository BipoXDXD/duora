"""Cliente de carga C2 (issue #86): participantes sintéticos contra o projeto sombra, a partir do Mac.

Cada cliente é uma thread com conexão keep-alive própria, cookies de sessão e token CSRF, como o navegador. O
modelo do protocolo (docs/operations/capacity.md): leitura a cada 10 s, comando a cada 5 s e mensagem a cada 15 s.
Mensagens e SignalR ainda não existem no produto: ficam como **pendentes** no relatório, nunca como stub.

Leituras: GET /me, /events, participação no evento e sala. Comandos: inscrição e check-in no evento sintético,
aceite da sala quando o worker a forma, e troca do fuso com If-Match (escrita real, gera WAL). Cada requisição vira
um registro com instante, operação, classe, status e latência; status 0 é falha de transporte ou timeout.
"""
from __future__ import annotations

import http.client
import json
import random
import socket
import ssl
import threading
import time
import urllib.parse
from collections import deque
from dataclasses import dataclass, field
from datetime import datetime
from pathlib import Path
from typing import Any, Callable, TextIO

READ_SECONDS = 10.0
COMMAND_SECONDS = 5.0
MESSAGE_SECONDS = 15.0
REQUEST_TIMEOUT_SECONDS = 10.0
TIME_ZONES = ("America/Sao_Paulo", "UTC")
LOOPBACK_HOSTS = frozenset(("127.0.0.1", "::1", "localhost"))
WINDOW_SECONDS = 300.0
CSRF_COOKIE = "XSRF-TOKEN"
CSRF_HEADER = "X-XSRF-TOKEN"
KINDS = ("login", "logout", "read", "command", "reconcile")


@dataclass(frozen=True)
class Target:
    """Para onde conectar (túnel SSH no loopback) e qual nome apresentar no SNI/Host (o domínio do sombra)."""

    connect_host: str
    connect_port: int
    server_name: str
    ca_file: str | None
    tls: bool = True
    timeout_seconds: float = REQUEST_TIMEOUT_SECONDS

    def __post_init__(self) -> None:
        if not self.tls and self.connect_host not in LOOPBACK_HOSTS:
            raise ValueError("HTTP sem TLS só no loopback (testes locais)")
        if self.tls and not self.ca_file:
            raise ValueError("TLS exige ca_file (raiz interna do Caddy do sombra); verificação nunca é desligada")


@dataclass(frozen=True)
class Account:
    email: str
    password: str


@dataclass(frozen=True)
class RequestRecord:
    t: float
    op: str
    kind: str
    status: int
    ms: float
    phase: str
    error: str = ""

    def to_json(self) -> str:
        return json.dumps({"t": round(self.t, 3), "op": self.op, "kind": self.kind, "status": self.status,
                           "ms": round(self.ms, 2), "phase": self.phase, "error": self.error},
                          separators=(",", ":"), sort_keys=True)


def classify(status: int) -> str:
    """ok, client (4xx sem 429), throttled (429), server (5xx) ou transport (0: timeout/conexão)."""
    if status == 0:
        return "transport"
    if status == 429:
        return "throttled"
    if 500 <= status:
        return "server"
    if 400 <= status:
        return "client"
    return "ok"


class Recorder:
    """Grava cada requisição em JSONL e mantém uma janela recente para os guardas."""

    def __init__(self, out: TextIO | None, clock: Callable[[], float] = time.time) -> None:
        self._out = out
        self._clock = clock
        self._lock = threading.Lock()
        self._window: deque[RequestRecord] = deque()
        self.phase = "setup"

    def record(self, op: str, kind: str, status: int, started: float, ms: float, error: str = "") -> None:
        entry = RequestRecord(started, op, kind, status, ms, self.phase, error)
        with self._lock:
            self._window.append(entry)
            cutoff = self._clock() - WINDOW_SECONDS
            while self._window and self._window[0].t < cutoff:
                self._window.popleft()
            if self._out is not None:
                self._out.write(entry.to_json() + "\n")

    def recent(self, seconds: float) -> list[RequestRecord]:
        cutoff = self._clock() - seconds
        with self._lock:
            return [entry for entry in self._window if entry.t >= cutoff]

    def flush(self) -> None:
        with self._lock:
            if self._out is not None:
                self._out.flush()


class _SniConnection(http.client.HTTPSConnection):
    """Conecta no túnel (127.0.0.1:porta), mas apresenta o domínio do sombra no SNI e valida o certificado nele."""

    def __init__(self, target: Target, context: ssl.SSLContext) -> None:
        super().__init__(target.connect_host, target.connect_port, timeout=target.timeout_seconds, context=context)
        self._server_name = target.server_name
        self._tls_context = context

    def connect(self) -> None:
        raw = socket.create_connection((self.host, self.port), self.timeout)
        self.sock = self._tls_context.wrap_socket(raw, server_hostname=self._server_name)


def tls_context(ca_file: str) -> ssl.SSLContext:
    context = ssl.create_default_context(cafile=ca_file)
    context.check_hostname = True
    context.verify_mode = ssl.CERT_REQUIRED
    return context


def parse_set_cookies(headers: list[tuple[str, str]]) -> dict[str, str | None]:
    """name -> valor; None quando o servidor expira o cookie (logout)."""
    cookies: dict[str, str | None] = {}
    for name, value in headers:
        if name.lower() != "set-cookie":
            continue
        pair, _, attributes = value.partition(";")
        key, _, cookie_value = pair.strip().partition("=")
        expired = "max-age=0" in attributes.lower().replace(" ", "") or "1970" in attributes
        cookies[key] = None if expired or cookie_value == "" else cookie_value
    return cookies


class Session:
    """Uma conexão keep-alive com cookies; nunca segue redirect nem guarda corpo de resposta."""

    def __init__(self, target: Target, recorder: Recorder, context: ssl.SSLContext | None = None) -> None:
        self._target = target
        self._recorder = recorder
        self._context = context if context is not None or not target.tls else tls_context(str(target.ca_file))
        self._connection: http.client.HTTPConnection | None = None
        self.cookies: dict[str, str] = {}

    def close(self) -> None:
        if self._connection is not None:
            self._connection.close()
            self._connection = None

    def request(self, method: str, path: str, *, op: str, kind: str, body: Any = None,
                headers: dict[str, str] | None = None) -> tuple[int, dict[str, str], Any]:
        all_headers = {"Host": self._target.server_name, "Accept": "application/json",
                       "User-Agent": "duora-c2-load/1"}
        if self.cookies:
            all_headers["Cookie"] = "; ".join(f"{key}={value}" for key, value in sorted(self.cookies.items()))
        if method != "GET" and CSRF_COOKIE in self.cookies:
            all_headers[CSRF_HEADER] = urllib.parse.unquote(self.cookies[CSRF_COOKIE])
        payload = None
        if body is not None:
            payload = json.dumps(body).encode("utf-8")
            all_headers["Content-Type"] = "application/json"
        all_headers.update(headers or {})
        started = time.time()
        clock = time.monotonic()
        try:
            connection = self._ensure_connection()
            connection.request(method, path, body=payload, headers=all_headers)
            response = connection.getresponse()
            raw = response.read()
            status = response.status
            response_headers = response.getheaders()
        except (OSError, http.client.HTTPException, ssl.SSLError) as error:
            self.close()
            self._recorder.record(op, kind, 0, started, (time.monotonic() - clock) * 1000, type(error).__name__)
            return 0, {}, None
        self._recorder.record(op, kind, status, started, (time.monotonic() - clock) * 1000)
        for key, value in parse_set_cookies(response_headers).items():
            if value is None:
                self.cookies.pop(key, None)
            else:
                self.cookies[key] = value
        lowered = {key.lower(): value for key, value in response_headers}
        parsed = None
        if raw and lowered.get("content-type", "").startswith("application/json"):
            try:
                parsed = json.loads(raw)
            except ValueError:
                parsed = None
        return status, lowered, parsed

    def _ensure_connection(self) -> http.client.HTTPConnection:
        if self._connection is None:
            if self._target.tls:
                assert self._context is not None  # garantido pelo construtor quando tls
                self._connection = _SniConnection(self._target, self._context)
            else:
                self._connection = http.client.HTTPConnection(self._target.connect_host, self._target.connect_port,
                                                              timeout=self._target.timeout_seconds)
        return self._connection


@dataclass
class ClientState:
    event_id: str | None = None
    check_in_opens_at: float | None = None
    registered: bool = False
    checked_in: bool = False
    room_id: str | None = None
    accepted: bool = False
    time_zone_etag: str | None = None
    zone_index: int = 0
    last_write_ack: float | None = None
    logged_in: bool = False


class SyntheticParticipant:
    """Comportamento de um participante. Chamado pela thread do cliente; os métodos públicos fazem uma ação cada."""

    def __init__(self, session: Session, account: Account, clock: Callable[[], float] = time.time) -> None:
        self.session = session
        self.account = account
        self.state = ClientState()
        self._clock = clock
        self._read_cycle = 0

    def login(self) -> bool:
        self.session.request("GET", "/api/v1/auth/csrf", op="csrf", kind="login")
        status, _, _ = self.session.request("POST", "/api/v1/auth/login", op="login", kind="login",
                                            body={"email": self.account.email, "password": self.account.password})
        if status not in (200, 204):
            return False
        # O token antiforgery é ligado ao usuário: renova depois do login, como a SPA.
        self.session.request("GET", "/api/v1/auth/csrf", op="csrf", kind="login")
        self.state.logged_in = True
        return True

    def logout(self) -> None:
        self.session.request("POST", "/api/v1/auth/logout", op="logout", kind="logout")
        self.session.cookies.clear()
        self.state.logged_in = False

    def read(self) -> None:
        """Gira entre /me, /events, participação e sala, como as telas que se atualizam a cada 10 s."""
        cycle = self._read_cycle % 3
        self._read_cycle += 1
        if cycle == 0:
            self.session.request("GET", "/api/v1/me", op="me", kind="read")
        elif cycle == 1 or self.state.event_id is None:
            self._refresh_events()
        elif self.state.room_id is not None:
            self.session.request("GET", f"/api/v1/rooms/{self.state.room_id}", op="room", kind="read")
        else:
            self._refresh_participation(kind="read")

    def command(self) -> None:
        state = self.state
        now = self._clock()
        if state.event_id is not None and not state.registered:
            status, _, _ = self.session.request("PUT", f"/api/v1/events/{state.event_id}/registration",
                                                op="register", kind="command")
            state.registered = status in (200, 204, 409)
        elif (state.event_id is not None and not state.checked_in and state.check_in_opens_at is not None
              and now >= state.check_in_opens_at):
            status, _, _ = self.session.request("POST", f"/api/v1/events/{state.event_id}/check-in",
                                                op="check-in", kind="command")
            state.checked_in = status in (200, 204, 409)
        elif state.room_id is not None and not state.accepted:
            status, _, _ = self.session.request("POST", f"/api/v1/rooms/{state.room_id}/accept", op="accept",
                                                kind="command")
            state.accepted = status in (200, 204, 409)
        else:
            self._toggle_time_zone()

    def reconcile(self) -> None:
        """Reconexão: TCP/TLS novo e releitura do estado que importa (sem SignalR ainda)."""
        self.session.close()
        self.session.request("GET", "/api/v1/me", op="me", kind="reconcile")
        if self.state.event_id is not None:
            self._refresh_participation(kind="reconcile")

    def _refresh_events(self) -> None:
        status, _, events = self.session.request("GET", "/api/v1/events", op="events", kind="read")
        if status != 200 or not isinstance(events, list):
            return
        chosen = choose_event(events, self._clock())
        if chosen is not None and chosen[0] != self.state.event_id:
            self.state = ClientState(event_id=chosen[0], check_in_opens_at=chosen[1],
                                     time_zone_etag=self.state.time_zone_etag, zone_index=self.state.zone_index,
                                     last_write_ack=self.state.last_write_ack, logged_in=self.state.logged_in)

    def _refresh_participation(self, *, kind: str) -> None:
        status, _, body = self.session.request("GET", f"/api/v1/events/{self.state.event_id}/participation",
                                               op="participation", kind=kind)
        if status == 200 and isinstance(body, dict):
            room = body.get("roomId")
            if isinstance(room, str) and room != self.state.room_id:
                self.state.room_id = room
                self.state.accepted = False

    def _toggle_time_zone(self) -> None:
        state = self.state
        if state.time_zone_etag is None:
            status, headers, _ = self.session.request("GET", "/api/v1/me/time-zone", op="time-zone-get",
                                                      kind="command")
            if status != 200:
                return
            state.time_zone_etag = headers.get("etag")
        state.zone_index = (state.zone_index + 1) % len(TIME_ZONES)
        status, headers, _ = self.session.request(
            "PUT", "/api/v1/me/time-zone", op="time-zone-put", kind="command",
            body={"timeZone": TIME_ZONES[state.zone_index]}, headers={"If-Match": state.time_zone_etag or ""})
        if status == 200:
            state.time_zone_etag = headers.get("etag")
            state.last_write_ack = self._clock()
        else:
            state.time_zone_etag = None


def choose_event(events: list[Any], now: float) -> tuple[str, float] | None:
    """O evento publicado que começa primeiro e ainda não começou; devolve (id, abertura do check-in em epoch)."""
    candidates = []
    for event in events:
        if not isinstance(event, dict) or event.get("state") != "Published":
            continue
        try:
            starts = datetime.fromisoformat(str(event["startsAt"])).timestamp()
            opens = datetime.fromisoformat(str(event["checkInOpensAt"])).timestamp()
        except (KeyError, ValueError):
            continue
        if starts > now:
            candidates.append((starts, str(event["eventId"]), opens))
    if not candidates:
        return None
    _, event_id, opens = min(candidates)
    return event_id, opens


@dataclass
class _Worker:
    participant: SyntheticParticipant
    thread: threading.Thread | None = None
    stop: threading.Event = field(default_factory=threading.Event)
    pending: list[tuple[float, str]] = field(default_factory=list)
    lock: threading.Lock = field(default_factory=threading.Lock)


class LoadDriver:
    """Liga e desliga clientes conforme a fase; entradas e reconexões são agendadas por cliente, com jitter."""

    def __init__(self, target: Target, accounts: list[Account], recorder: Recorder, *, seed: int,
                 session_factory: Callable[[Target, Recorder], Session] | None = None) -> None:
        if not accounts:
            raise ValueError("nenhuma conta sintética")
        self._recorder = recorder
        self._random = random.Random(seed)
        factory = session_factory or (lambda t, r: Session(t, r))
        self._workers = [_Worker(SyntheticParticipant(factory(target, recorder), account)) for account in accounts]
        self.messages_skipped = 0

    @property
    def capacity(self) -> int:
        return len(self._workers)

    def active(self) -> int:
        return sum(1 for worker in self._workers if worker.thread is not None and worker.thread.is_alive())

    def set_clients(self, count: int) -> None:
        if count > len(self._workers):
            raise ValueError(f"{count} clientes pedidos, só {len(self._workers)} contas sintéticas")
        for index, worker in enumerate(self._workers):
            running = worker.thread is not None and worker.thread.is_alive()
            if index < count and not running:
                worker.stop = threading.Event()
                worker.thread = threading.Thread(target=self._run, args=(worker,), daemon=True,
                                                 name=f"c2-client-{index}")
                worker.thread.start()
            elif index >= count and running:
                worker.stop.set()

    def schedule_entries(self, within_seconds: float) -> None:
        """Todos os ativos saem e entram de novo, espalhados na janela (100 entradas em 60 s)."""
        self._schedule("entry", self._running(), within_seconds)

    def schedule_reconnect(self, fraction: float, within_seconds: float) -> None:
        running = self._running()
        chosen = self._random.sample(running, k=int(round(len(running) * fraction)))
        self._schedule("reconnect", chosen, within_seconds)

    def stop_all(self, join_seconds: float = 15.0) -> None:
        for worker in self._workers:
            worker.stop.set()
        deadline = time.monotonic() + join_seconds
        for worker in self._workers:
            if worker.thread is not None:
                worker.thread.join(max(0.0, deadline - time.monotonic()))
            worker.participant.session.close()

    def last_write_ack(self) -> float | None:
        acks = [w.participant.state.last_write_ack for w in self._workers if w.participant.state.last_write_ack]
        return max(acks) if acks else None

    def _running(self) -> list[_Worker]:
        return [worker for worker in self._workers if worker.thread is not None and worker.thread.is_alive()
                and not worker.stop.is_set()]

    def _schedule(self, action: str, workers: list[_Worker], within_seconds: float) -> None:
        now = time.monotonic()
        for worker in workers:
            with worker.lock:
                worker.pending.append((now + self._random.uniform(0.0, within_seconds), action))

    def _run(self, worker: _Worker) -> None:
        participant = worker.participant
        jitter = random.Random(self._random.random())
        if not participant.state.logged_in and not participant.login():
            worker.stop.wait(COMMAND_SECONDS)
        now = time.monotonic()
        next_read = now + jitter.uniform(0, READ_SECONDS)
        next_command = now + jitter.uniform(0, COMMAND_SECONDS)
        next_message = now + jitter.uniform(0, MESSAGE_SECONDS)
        while not worker.stop.is_set():
            now = time.monotonic()
            self._run_due_actions(worker, now)
            if not participant.state.logged_in:
                if not participant.login():
                    worker.stop.wait(COMMAND_SECONDS)
                continue
            if now >= next_read:
                participant.read()
                next_read += READ_SECONDS
            if now >= next_command:
                participant.command()
                next_command += COMMAND_SECONDS
            if now >= next_message:
                # Mensagens não existem no produto (pendente): conta a oportunidade, não simula.
                self.messages_skipped += 1
                next_message += MESSAGE_SECONDS
            wake = min(next_read, next_command, next_message, self._next_pending(worker) or float("inf"))
            worker.stop.wait(max(0.0, min(1.0, wake - time.monotonic())))

    def _next_pending(self, worker: _Worker) -> float | None:
        with worker.lock:
            if not worker.pending:
                return None
            return min(when for when, _ in worker.pending)

    def _run_due_actions(self, worker: _Worker, now: float) -> None:
        with worker.lock:
            due = [item for item in worker.pending if item[0] <= now]
            worker.pending[:] = [item for item in worker.pending if item[0] > now]
        for _, action in sorted(due):
            if action == "entry":
                worker.participant.logout()
                worker.participant.session.close()
                worker.participant.login()
            elif action == "reconnect":
                worker.participant.reconcile()


def load_accounts(path: Path) -> list[Account]:
    """Arquivo gerado pelo `c2_shadow seed`: só contas sintéticas (@c2.invalid)."""
    data = json.loads(path.read_text(encoding="utf-8"))
    accounts = [Account(str(item["email"]), str(item["password"])) for item in data["accounts"]]
    foreign = [account.email for account in accounts if not account.email.endswith("@c2.invalid")]
    if foreign:
        raise ValueError(f"{len(foreign)} conta(s) fora do domínio sintético @c2.invalid")
    return accounts
