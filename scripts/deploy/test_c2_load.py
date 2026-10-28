"""Cliente de carga C2 (issue #86) contra um servidor HTTP falso no loopback; sem rede, Docker ou VPS."""
from __future__ import annotations

import io
import json
import tempfile
import threading
import time
import unittest
from datetime import UTC, datetime, timedelta
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

import c2_load as load

EVENT = "11111111-1111-1111-1111-111111111111"
ROOM = "22222222-2222-2222-2222-222222222222"
SESSION = "session-synthetic"


class FakeDuora:
    """O suficiente da API real para exercitar sessão, CSRF, If-Match e o fluxo do evento."""

    def __init__(self) -> None:
        self.lock = threading.Lock()
        self.calls: list[tuple[str, str]] = []
        self.csrf_missing: list[str] = []
        self.version = 1
        self.zone = "UTC"
        self.checked_in = False
        self.fail_me = False

    def handler(self) -> type[BaseHTTPRequestHandler]:
        fake = self

        class Handler(BaseHTTPRequestHandler):
            def log_message(self, format: str, *args: object) -> None:  # noqa: A002 - assinatura da stdlib
                return

            def do_GET(self) -> None:
                fake.dispatch(self, "GET")

            def do_POST(self) -> None:
                fake.dispatch(self, "POST")

            def do_PUT(self) -> None:
                fake.dispatch(self, "PUT")

        return Handler

    def dispatch(self, request: BaseHTTPRequestHandler, method: str) -> None:
        path = request.path
        length = int(request.headers.get("Content-Length") or 0)
        body = json.loads(request.rfile.read(length)) if length else None
        cookies = request.headers.get("Cookie") or ""
        with self.lock:
            self.calls.append((method, path))
            if method != "GET" and request.headers.get("X-XSRF-TOKEN") != "tok en":
                self.csrf_missing.append(path)
        logged = f".Duora.Session={SESSION}" in cookies
        if path == "/api/v1/auth/csrf":
            return self.reply(request, 204, cookies=["XSRF-TOKEN=tok%20en; Path=/"])
        if path == "/api/v1/auth/login":
            if body != {"email": "p1@c2.invalid", "password": "frase sintética"}:
                return self.reply(request, 400)
            return self.reply(request, 204, cookies=[f".Duora.Session={SESSION}; Path=/; HttpOnly"])
        if path == "/api/v1/auth/logout":
            return self.reply(request, 204, cookies=[".Duora.Session=; expires=Thu, 01 Jan 1970 00:00:00 GMT"])
        if not logged:
            return self.reply(request, 401)
        now = datetime.now(UTC)
        routes = {
            ("GET", "/api/v1/me"): lambda: (500, None) if self.fail_me else (200, {"id": "x"}),
            ("GET", "/api/v1/events"): lambda: (200, [
                {"eventId": "old", "title": "x", "startsAt": (now - timedelta(hours=1)).isoformat(),
                 "checkInOpensAt": (now - timedelta(hours=2)).isoformat(), "state": "Published"},
                {"eventId": EVENT, "title": "x", "startsAt": (now + timedelta(minutes=5)).isoformat(),
                 "checkInOpensAt": (now - timedelta(minutes=5)).isoformat(), "state": "Published"}]),
            ("PUT", f"/api/v1/events/{EVENT}/registration"): lambda: (204, None),
            ("POST", f"/api/v1/events/{EVENT}/check-in"): self.check_in,
            ("GET", f"/api/v1/events/{EVENT}/participation"): lambda: (
                200, {"state": "Invited" if self.checked_in else "Registered",
                      "roomId": ROOM if self.checked_in else None}),
            ("GET", f"/api/v1/rooms/{ROOM}"): lambda: (200, {"roomId": ROOM}),
            ("POST", f"/api/v1/rooms/{ROOM}/accept"): lambda: (204, None),
            ("GET", "/api/v1/me/time-zone"): lambda: (200, {"timeZone": self.zone}),
        }
        if (method, path) == ("PUT", "/api/v1/me/time-zone"):
            if request.headers.get("If-Match") != f'"{self.version}"':
                return self.reply(request, 412)
            self.version += 1
            self.zone = (body or {}).get("timeZone", "UTC")
            return self.reply(request, 200, {"timeZone": self.zone}, etag=f'"{self.version}"')
        action = routes.get((method, path))
        if action is None:
            return self.reply(request, 404)
        status, payload = action()
        etag = f'"{self.version}"' if path == "/api/v1/me/time-zone" else None
        return self.reply(request, status, payload, etag=etag)

    def check_in(self) -> tuple[int, None]:
        self.checked_in = True
        return 204, None

    @staticmethod
    def reply(request: BaseHTTPRequestHandler, status: int, payload: object = None, *,
              cookies: list[str] | None = None, etag: str | None = None) -> None:
        data = b"" if payload is None else json.dumps(payload).encode()
        request.send_response(status)
        for cookie in cookies or []:
            request.send_header("Set-Cookie", cookie)
        if etag:
            request.send_header("ETag", etag)
        if data:
            request.send_header("Content-Type", "application/json; charset=utf-8")
        request.send_header("Content-Length", str(len(data)))
        request.end_headers()
        request.wfile.write(data)


class LoadAgainstFakeServer(unittest.TestCase):
    def setUp(self) -> None:
        self.fake = FakeDuora()
        self.server = ThreadingHTTPServer(("127.0.0.1", 0), self.fake.handler())
        threading.Thread(target=self.server.serve_forever, daemon=True).start()
        self.target = load.Target("127.0.0.1", self.server.server_address[1], "c2.duora.test", None, tls=False)
        self.out = io.StringIO()
        self.recorder = load.Recorder(self.out)
        self.participant = load.SyntheticParticipant(load.Session(self.target, self.recorder),
                                                     load.Account("p1@c2.invalid", "frase sintética"))

    def tearDown(self) -> None:
        self.server.shutdown()
        self.server.server_close()

    def records(self) -> list[dict]:
        return [json.loads(line) for line in self.out.getvalue().splitlines()]

    def test_login_sends_csrf_header_and_keeps_session_cookie(self) -> None:
        self.assertTrue(self.participant.login())
        self.participant.read()
        self.assertEqual(self.fake.csrf_missing, [])
        self.assertEqual([r["op"] for r in self.records()], ["csrf", "login", "csrf", "me"])
        self.assertEqual(self.records()[-1]["status"], 200)
        self.assertEqual({r["kind"] for r in self.records()[:3]}, {"login"})

    def test_event_flow_registers_checks_in_and_accepts_the_room(self) -> None:
        self.participant.login()
        self.participant.read()  # /me
        self.participant.read()  # /events -> escolhe o evento futuro, nunca o que já começou
        self.assertEqual(self.participant.state.event_id, EVENT)
        self.participant.command()  # inscrição
        self.participant.command()  # check-in (janela aberta)
        self.participant.read()  # participação -> sala
        self.participant.command()  # aceite
        self.assertEqual(self.participant.state.room_id, ROOM)
        self.assertTrue(self.participant.state.accepted)
        ops = [r["op"] for r in self.records() if r["kind"] == "command"]
        self.assertEqual(ops, ["register", "check-in", "accept"])

    def test_time_zone_toggle_uses_if_match_and_recovers_from_412(self) -> None:
        self.participant.login()
        self.participant.command()  # sem evento: GET + PUT do fuso
        self.participant.command()  # PUT com o ETag novo
        self.assertEqual(self.fake.version, 3)
        self.participant.state.time_zone_etag = '"stale"'
        self.participant.command()  # 412 -> esquece o ETag
        self.assertIsNone(self.participant.state.time_zone_etag)
        self.participant.command()  # relê e grava
        self.assertEqual(self.fake.version, 4)
        statuses = [r["status"] for r in self.records() if r["op"] == "time-zone-put"]
        self.assertEqual(statuses, [200, 200, 412, 200])
        self.assertIsNotNone(self.participant.state.last_write_ack)

    def test_reconcile_opens_a_new_connection_and_rereads_state(self) -> None:
        self.participant.login()
        self.participant.read()
        self.participant.read()
        self.participant.reconcile()
        reconcile = [r["op"] for r in self.records() if r["kind"] == "reconcile"]
        self.assertEqual(reconcile, ["me", "participation"])

    def test_logout_drops_the_session_cookie(self) -> None:
        self.participant.login()
        self.participant.logout()
        self.participant.read()
        self.assertEqual(self.records()[-1]["status"], 401)

    def test_server_errors_and_refused_connections_are_recorded_as_failures(self) -> None:
        self.participant.login()
        self.fake.fail_me = True
        self.participant.read()
        self.assertEqual(self.records()[-1]["status"], 500)
        self.server.shutdown()
        self.server.server_close()
        self.participant.session.close()
        self.participant.read()
        last = self.records()[-1]
        self.assertEqual((last["status"], load.classify(last["status"])), (0, "transport"))
        self.assertTrue(last["error"])

    def test_driver_runs_clients_and_schedules_reconnects(self) -> None:
        accounts = [load.Account("p1@c2.invalid", "frase sintética")] * 2
        driver = load.LoadDriver(self.target, accounts, self.recorder, seed=7)
        driver.set_clients(2)
        deadline = time.monotonic() + 5
        while time.monotonic() < deadline and not any(r["op"] == "login" for r in self.records()):
            time.sleep(0.05)
        self.assertEqual(driver.active(), 2)
        driver.schedule_reconnect(0.5, within_seconds=0.1)
        deadline = time.monotonic() + 5
        while time.monotonic() < deadline and not any(r["kind"] == "reconcile" for r in self.records()):
            time.sleep(0.05)
        driver.stop_all(join_seconds=5)
        self.assertEqual(driver.active(), 0)
        self.assertTrue(any(r["kind"] == "reconcile" for r in self.records()))
        with self.assertRaises(ValueError):
            driver.set_clients(3)


class UnitTests(unittest.TestCase):
    def test_classify_separates_429_from_other_4xx_and_failures(self) -> None:
        self.assertEqual([load.classify(s) for s in (200, 204, 404, 409, 429, 500, 503, 0)],
                         ["ok", "ok", "client", "client", "throttled", "server", "server", "transport"])

    def test_tls_requires_a_ca_and_plain_http_only_on_loopback(self) -> None:
        with self.assertRaises(ValueError):
            load.Target("127.0.0.1", 18443, "c2.duora.test", None)
        with self.assertRaises(ValueError):
            load.Target("meetduora.com", 80, "meetduora.com", None, tls=False)

    def test_expired_cookie_is_removed(self) -> None:
        cookies = load.parse_set_cookies([("Set-Cookie", "a=1; Path=/"), ("set-cookie", "b=; Max-Age=0"),
                                          ("X-Other", "c=3")])
        self.assertEqual(cookies, {"a": "1", "b": None})

    def test_choose_event_skips_started_and_unpublished(self) -> None:
        now = datetime(2026, 10, 28, 12, tzinfo=UTC)
        events = [
            {"eventId": "a", "state": "Published", "startsAt": "2026-10-28T11:59:00+00:00",
             "checkInOpensAt": "2026-10-28T11:49:00+00:00"},
            {"eventId": "b", "state": "Cancelled", "startsAt": "2026-10-28T12:01:00+00:00",
             "checkInOpensAt": "2026-10-28T11:51:00+00:00"},
            {"eventId": "c", "state": "Published", "startsAt": "2026-10-28T12:30:00+00:00",
             "checkInOpensAt": "2026-10-28T12:20:00+00:00"},
            {"eventId": "d", "state": "Published", "startsAt": "2026-10-28T12:10:00+00:00",
             "checkInOpensAt": "2026-10-28T12:00:00+00:00"},
            "lixo",
        ]
        self.assertEqual(load.choose_event(events, now.timestamp()),
                         ("d", datetime(2026, 10, 28, 12, tzinfo=UTC).timestamp()))
        self.assertIsNone(load.choose_event([], now.timestamp()))

    def test_accounts_outside_the_synthetic_domain_are_refused(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "accounts.json"
            path.write_text(json.dumps({"accounts": [{"email": "p1@c2.invalid", "password": "x"}]}))
            self.assertEqual(load.load_accounts(path), [load.Account("p1@c2.invalid", "x")])
            path.write_text(json.dumps({"accounts": [{"email": "real@gmail.com", "password": "x"}]}))
            with self.assertRaises(ValueError):
                load.load_accounts(path)

    def test_recorder_window_drops_old_entries(self) -> None:
        now = [1000.0]
        recorder = load.Recorder(None, clock=lambda: now[0])
        recorder.record("me", "read", 200, 1000.0, 5.0)
        now[0] = 1000.0 + load.WINDOW_SECONDS + 1
        recorder.record("me", "read", 200, now[0], 5.0)
        self.assertEqual(len(recorder.recent(load.WINDOW_SECONDS)), 1)


if __name__ == "__main__":
    unittest.main()
