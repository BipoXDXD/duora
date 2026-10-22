"""Prontidão da fixture Caddy; não opera serviços de produção."""

from __future__ import annotations

import subprocess
import time
from collections.abc import Callable


def wait_for_edge(request: Callable[[str], subprocess.CompletedProcess[str]], *,
                  sleep: Callable[[float], None] = time.sleep,
                  now: Callable[[], float] = time.monotonic) -> None:
    # Janela de novas sondas; cada request conserva o próprio timeout de transporte.
    deadline = now() + 30
    while now() < deadline:
        health = request("http://127.0.0.1:8081/healthz")
        if health.returncode == 0:
            tls = request("https://localhost/api/v1/network-probe")
            if tls.returncode == 0 and tls.stdout == "duora-upstream":
                return
        sleep(0.5)
    raise AssertionError("edge de teste não ficou pronto")
