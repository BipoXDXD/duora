#!/usr/bin/env python3
"""Alerta da selfie (fornecedor e fila parada): lê a fila uma vez e, nas transições, deixa um evento no outbox do host.

Roda como root em timer curto, só com a stdlib. Sem rota nova: a consulta é fixa, somente leitura e não sabe de quem é a
tentativa. Guia e limites: docs/operations/selfie-provider-alert.md.
"""
from __future__ import annotations

from collections.abc import Callable
from datetime import datetime, timezone
import json
import logging
import math
import os
from pathlib import Path
import subprocess
import sys
import time
from typing import cast

LOG = logging.getLogger('duora-selfie-alert')
OUTBOX = Path('/var/lib/duora-next-deploy-monitor/outbox')
STATE = Path('/var/lib/duora-selfie-alert/state.json')

# Limiares iniciais em segundos, sem medição de produção.
OPEN_AFTER = 120
# OnUnitInactiveSec conta do fim da execução: o pior intervalo é 30 + 16 (dois timeouts de 8 s) + 1 = 47 s. Assim, uma
# leitura perdida (94 s) não recomeça a contagem.
RESTART_AFTER = 100
CLOSE_AFTER = 900
BLIND_AFTER = 600
# A consulta já exige 60 s de espera; somados, o alerta sai cerca de 3 min depois de a fila parar.
STALLED_OPEN_AFTER = 120

GUIDE = 'docs/operations/selfie-provider-alert.md'
NOT_A_DEPLOY = 'SELFIE, NAO E DEPLOY: ignore a orientacao sobre o Coolify.'
REASONS = {
    'selfie_provider_down': f'{NOT_A_DEPLOY} Chamadas do worker a AWS falhando ha 2 min ou mais. Ver {GUIDE}',
    'selfie_provider_recovered': (f'{NOT_A_DEPLOY} A ultima falha observada tem 15 min ou mais. As leituras podem ter '
                                  f'parado nesse intervalo, e com pouco trafego pode ser so falta de tentativas. '
                                  f'Ver {GUIDE}'),
    'selfie_queue_stalled': (f'{NOT_A_DEPLOY} Ha entrega da selfie esperando e nenhum worker com trabalho em maos ha '
                             f'2 min. Ver {GUIDE}'),
    'selfie_queue_resumed': f'{NOT_A_DEPLOY} A fila da selfie voltou a andar ou esvaziou. Ver {GUIDE}',
    'selfie_probe_blind': f'{NOT_A_DEPLOY} A consulta a fila falha ha 10 min e este alerta esta cego. Ver {GUIDE}',
    'selfie_probe_restored': f'{NOT_A_DEPLOY} A consulta a fila voltou a responder. Ver {GUIDE}',
}

# Peer no socket do próprio container, como duora-deploy: nenhuma credencial nem rota nova (ADR-0051).
CONNECTION = ("dbname=duora user=postgres connect_timeout=3 application_name=duora-selfie-alert "
              "options='-c default_transaction_read_only=on -c statement_timeout=3000 -c lock_timeout=1000'")
# Uma tentativa falha agora se a última chamada ao fornecedor falhou. `available_at` e o lease provam que o worker
# gravou isso há pouco: parado, o marcador envelhece em cerca de 65 s. Dois exists, para usar o índice parcial de cada
# status.
PROBE_QUERY = """
select exists (select 1 from ops.outbox_deliveries
               where consumer = 'selfie-provider' and status = 'Pending'
                 and available_at >= now() - interval '60 seconds' and last_error = 'ProviderUnavailable')
    or exists (select 1 from ops.outbox_deliveries
               where consumer = 'selfie-provider' and status = 'InFlight'
                 and lease_expires_at >= now() and last_error = 'ProviderUnavailable')
"""
# Fila parada: entrega pronta ou com lease vencido há 60 s ou mais, sem nenhum lease vivo. O lease vivo prova só um claim
# recente, não que a entrega andou (limites no guia): cobre a fila longa com fornecedor lento, que quase sempre tem uma
# chamada em andamento. Não depende do marcador de falha, que envelhece quando o worker para de escrever.
STALLED_QUERY = """
select (exists (select 1 from ops.outbox_deliveries
                where consumer = 'selfie-provider' and status = 'Pending'
                  and available_at <= now() - interval '60 seconds')
        or exists (select 1 from ops.outbox_deliveries
                   where consumer = 'selfie-provider' and status = 'InFlight'
                     and lease_expires_at <= now() - interval '60 seconds'))
    and not exists (select 1 from ops.outbox_deliveries
                    where consumer = 'selfie-provider' and status = 'InFlight'
                      and lease_expires_at > now())
"""
# Um único psql: outro comando alongaria o pior intervalo do timer, do qual depende o RESTART_AFTER.
QUEUE_QUERY = f'select ({PROBE_QUERY}), ({STALLED_QUERY})'

READINGS = {'t|t': (True, True), 't|f': (True, False), 'f|t': (False, True), 'f|f': (False, False)}

Memory = dict[str, float | bool | None]
Runner = Callable[[list[str]], str]


def run(args: list[str]) -> str:
    result = subprocess.run(args, text=True, capture_output=True, timeout=8, check=True,
                            env={'PATH': '/usr/sbin:/usr/bin:/sbin:/bin', 'LC_ALL': 'C'})
    return result.stdout


def read_queue(runner: Runner) -> tuple[bool, bool] | None:
    """(fornecedor falhando, fila parada). None é falta de evidência: nem abre nem fecha os alertas."""
    try:
        ids = runner(['/usr/bin/docker', 'ps', '-aq', '--filter', 'label=com.docker.compose.project=duora', '--filter',
                      'label=com.docker.compose.service=postgres']).split()
        if len(ids) != 1:
            raise ValueError('missing or duplicate')
        answer = runner(['/usr/bin/docker', 'exec', ids[0], 'psql', '--no-psqlrc', '--no-password', '--quiet',
                         '--tuples-only', '--no-align', '--dbname', CONNECTION, '--command', QUEUE_QUERY]).strip()
        if answer not in READINGS:
            raise ValueError('unexpected answer')
    except (OSError, ValueError, subprocess.SubprocessError):
        LOG.error('selfie_probe_failed')
        return None
    return READINGS[answer]


def fresh_memory() -> Memory:
    return {'since': None, 'last': None, 'open': False, 'blind_since': None, 'blind': False, 'stall_since': None,
            'stall_open': False}


def advance(memory: Memory, failing: bool | None, now: float) -> tuple[Memory, list[tuple[str, float]]]:
    """Decide sem efeito colateral. Cada evento leva uma âncora estável, que dá nome ao arquivo no reenvio."""
    updated = dict(memory)
    if failing is None:
        return _without_reading(updated, now)
    events = []
    if memory['blind']:
        events.append(('selfie_probe_restored', _number(memory['blind_since'])))
    updated.update(blind_since=None, blind=False)
    if failing:
        if not memory['open'] and (memory['last'] is None or now - _number(memory['last']) > RESTART_AFTER):
            updated['since'] = now
        updated['last'] = now
        if not memory['open'] and now - _number(updated['since']) >= OPEN_AFTER:
            updated['open'] = True
            events.append(('selfie_provider_down', _number(updated['since'])))
    elif memory['open'] and now - _number(memory['last']) >= CLOSE_AFTER:
        events.append(('selfie_provider_recovered', _number(memory['last'])))
        updated.update(since=None, last=None, open=False)
    return updated, events


def advance_stall(memory: Memory, stalled: bool | None, now: float) -> tuple[Memory, list[tuple[str, float]]]:
    """Só leituras seguidas contam: worker vivo com fila longa pode aparecer parado por um instante, entre dois lotes."""
    updated = dict(memory)
    if stalled is None:
        return updated, []
    if not stalled:
        events = [('selfie_queue_resumed', _number(updated['stall_since']))] if updated['stall_open'] else []
        updated.update(stall_since=None, stall_open=False)
        return updated, events
    if updated['stall_since'] is None:
        updated['stall_since'] = now
    if updated['stall_open'] or now - _number(updated['stall_since']) < STALLED_OPEN_AFTER:
        return updated, []
    updated['stall_open'] = True
    return updated, [('selfie_queue_stalled', _number(updated['stall_since']))]


def _without_reading(memory: Memory, now: float) -> tuple[Memory, list[tuple[str, float]]]:
    if memory['blind_since'] is None:
        memory['blind_since'] = now
    if memory['blind'] or now - _number(memory['blind_since']) < BLIND_AFTER:
        return memory, []
    memory['blind'] = True
    return memory, [('selfie_probe_blind', _number(memory['blind_since']))]


def _number(value: float | bool | None) -> float:
    if type(value) not in (int, float):
        raise TypeError('timestamp expected')
    return float(value)  # pyright: ignore[reportArgumentType]


def load_memory(path: Path) -> Memory:
    """Estado ilegível vale como ausente: perder a memória é melhor que parar de alertar para sempre."""
    memory: object = None
    try:
        memory = json.loads(path.read_text())
    except FileNotFoundError:
        return fresh_memory()
    except (OSError, ValueError):
        pass  # O log fixo abaixo cobre os dois casos, sem o texto da exceção.
    if memory_is_valid(memory):
        return cast(Memory, memory)
    LOG.error('selfie_alert_state_discarded')
    return fresh_memory()


def memory_is_valid(memory: object) -> bool:
    def stamp(value: object) -> bool:
        return type(value) in (int, float) and math.isfinite(value)  # pyright: ignore[reportArgumentType]

    if type(memory) is not dict or set(memory) != set(fresh_memory()):
        return False
    if any(type(memory[flag]) is not bool for flag in ('open', 'blind', 'stall_open')):
        return False
    since, last, blind_since = memory['since'], memory['last'], memory['blind_since']
    window = (stamp(since) and stamp(last)) or (since is None and last is None and not memory['open'])
    blindness = stamp(blind_since) or (blind_since is None and not memory['blind'])
    stall = stamp(memory['stall_since']) or (memory['stall_since'] is None and not memory['stall_open'])
    return window and blindness and stall


def save_memory(path: Path, memory: Memory) -> None:
    temporary = path.with_suffix('.tmp')
    temporary.write_text(json.dumps(memory, allow_nan=False))
    os.replace(temporary, path)


def emit(outbox: Path, event: str, anchor: float, now: float) -> None:
    """Publica de uma vez, sem nunca sobrescrever: reprocessar não reenvia o que o mail.py já marcou como aceito."""
    target = outbox / f'selfie-provider-{event}-{int(anchor)}.json'
    temporary = outbox / f'.{target.stem}.{os.getpid()}.tmp'
    observed_at = datetime.fromtimestamp(now, timezone.utc).isoformat(timespec='seconds')
    fd = os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
    try:
        with os.fdopen(fd, 'w') as stream:
            json.dump({'event': event, 'reason': REASONS[event], 'deployment_uuid': target.stem,
                       'observed_at': observed_at}, stream)
            stream.flush()
            os.fsync(stream.fileno())
        os.link(temporary, target)
    except FileExistsError:
        LOG.info('selfie_alert_event_already_published')
    finally:
        temporary.unlink(missing_ok=True)
    directory = os.open(outbox, os.O_RDONLY)
    try:
        os.fsync(directory)
    finally:
        os.close(directory)


def run_once(runner: Runner, state: Path, outbox: Path, now: float) -> int:
    """O estado só avança depois do evento gravado. Sem leitura ou sem outbox, sai com erro e a unit fica `failed`."""
    outbox_present = outbox.is_dir()
    if not outbox_present:
        LOG.error('selfie_alert_outbox_missing')
    reading = read_queue(runner)
    failing, stalled = reading if reading is not None else (None, None)
    memory, events = advance(load_memory(state), failing, now)
    memory, stall_events = advance_stall(memory, stalled, now)
    for event, anchor in [*events, *stall_events]:
        emit(outbox, event, anchor, now)
    save_memory(state, memory)
    return 0 if outbox_present and failing is not None else 1


def main() -> int:
    logging.basicConfig(level=logging.INFO, format='%(name)s %(message)s')
    try:
        return run_once(run, STATE, OUTBOX, time.time())
    except Exception as error:  # Só o tipo: nada de texto de exceção no journal.
        LOG.error('selfie_alert_run_failed cause=%s', type(error).__name__)
        return 1


if __name__ == '__main__':
    sys.exit(main())
