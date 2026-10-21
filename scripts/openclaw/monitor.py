#!/usr/bin/env python3
"""Unprivileged monitor. GitHub read-only token and Slack webhook never reach the agent."""
from datetime import datetime
import json
import logging
import os
from pathlib import Path
import re
import time
import urllib.error
import urllib.parse
import urllib.request
from collect import atomic

LOG = logging.getLogger('duora-observer')
BAD = {'failure', 'timed_out', 'cancelled', 'action_required', 'startup_failure', 'stale'}


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        raise ValueError('redirect refused')


def request(url, token=None, payload=None):
    headers = {'Accept': 'application/vnd.github+json', 'User-Agent': 'duora-observer',
               'X-GitHub-Api-Version': '2022-11-28'}
    if token:
        headers['Authorization'] = f'Bearer {token}'
    data = None
    if payload is not None:
        headers = {'Content-Type': 'application/json'}
        data = json.dumps(payload).encode()
    with urllib.request.build_opener(NoRedirect).open(
            urllib.request.Request(url, data=data, headers=headers), timeout=10) as response:
        body = response.read(2_000_001)
        if len(body) > 2_000_000:
            raise ValueError('response too large')
        return body


def host_checks(snapshot, now):
    if not isinstance(snapshot.get('collected_at'), (int, float)):
        return {'observer:host-fresh': False}
    age = now - snapshot['collected_at']
    if not 0 <= age <= 120:
        return {'observer:host-fresh': False}
    checks = snapshot.get('checks', {})
    if not checks or any(type(value) is not bool for value in checks.values()):
        return {'observer:host-fresh': False}
    return {'observer:host-fresh': True, **checks}


def remote_checks(product_path, gateway_path, now):
    from telemetry import bounded_read, snapshot
    try:
        checks = host_checks(snapshot(bounded_read(product_path), now), now)
    except (OSError, ValueError, KeyError, TypeError, OverflowError):
        checks = {'observer:host-fresh': False}
    try:
        local = bounded_read(gateway_path)
        if (set(local) != {'collected_at', 'checks'} or local['checks'] != {'service:gateway': True}
                or type(local['collected_at']) not in (int, float)):
            raise ValueError('invalid gateway state')
        checks['service:gateway'] = host_checks(local, now)['observer:host-fresh']
    except (OSError, ValueError, KeyError, TypeError):
        checks['service:gateway'] = False
    return checks


def github_runs(repo, token, fetch=request):
    if not re.fullmatch(r'[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+', repo):
        raise ValueError('invalid repository')
    result = []
    # Bounded catch-up; the caller raises a coverage alert on saturation.
    for page in range(1, 11):
        body = json.loads(fetch(f'https://api.github.com/repos/{repo}/actions/runs?per_page=100&page={page}', token))
        runs = body['workflow_runs']
        for item in runs:
            rid, attempt = int(item['id']), int(item.get('run_attempt', 1))
            conclusion = item.get('conclusion')
            result.append({'id': rid, 'attempt': attempt, 'status': item['status'],
                           'conclusion': conclusion, 'failed': conclusion in BAD,
                           'updated_at': datetime.fromisoformat(item['updated_at'].replace('Z', '+00:00')).timestamp(),
                           'url': f'https://github.com/{repo}/actions/runs/{rid}'})
        if len(runs) < 100:
            return result
    # Monitoring all historical runs forever is intentionally bounded.
    return result


def deliver(checks, failures, state, send, now):
    """Persist only acknowledged delivery. Retry pending alerts; bounded reminder cadence."""
    sent = state.setdefault('checks', {})
    for key, good in checks.items():
        previous = sent.get(key)
        if previous is None and good:
            sent[key] = {'good': True, 'at': now}
            continue
        if previous and previous['good'] == good and (good or now - previous['at'] < 3600):
            continue
        send(f"Duora {'RECUPERADO' if good else 'ALERTA'}: {key}")
        sent[key] = {'good': good, 'at': now}
    seen = state.setdefault('runs', [])
    for repo, run in failures:
        key = f"{repo}:{run['id']}:{run['attempt']}"
        if key not in seen:
            send(f"Duora GitHub Actions: {repo} / {run['conclusion']} — {run['url']}")
            seen.append(key)
    state['runs'] = seen[-10000:]


def main():
    config = json.loads(Path('/etc/duora-observer/monitor.json').read_text())
    credentials = Path(os.environ['CREDENTIALS_DIRECTORY'])
    token = (credentials / 'github-token').read_text().strip()
    webhook = (credentials / 'slack-webhook').read_text().strip()
    url = urllib.parse.urlsplit(webhook)
    if url.scheme != 'https' or url.netloc != 'hooks.slack.com' or not url.path.startswith('/services/'):
        raise ValueError('invalid Slack webhook')
    state_path = Path('/var/lib/duora-monitor/state.json')
    state = json.loads(state_path.read_text()) if state_path.exists() else {}
    repos = config['repositories']
    if not 1 <= len(repos) <= 10:
        raise ValueError('configure 1..10 repositories')
    state.setdefault('monitoring_since', time.time() - 3600)
    profile = os.environ.get('DUORA_MONITOR_PROFILE', 'colocated')
    if profile not in ('colocated', 'remote-gateway'):
        raise ValueError('invalid monitor profile')
    runs_by_repo = {}
    gh_checks = {}
    next_github = 0
    while True:
        now = time.time()
        try:
            checks = (remote_checks('/var/lib/duora-remote/host.json', '/var/lib/duora-observer/host.json', now)
                      if profile == 'remote-gateway' else
                      host_checks(json.loads(Path('/var/lib/duora-observer/host.json').read_text()), now))
        except (OSError, ValueError):
            checks = {'observer:host-fresh': False}
        if now >= next_github:
            for repo in repos:
                try:
                    runs_by_repo[repo] = github_runs(repo, token)
                    gh_checks[f'github:{repo}:reachable'] = True
                    gh_checks[f'github:{repo}:coverage'] = len(runs_by_repo[repo]) < 1000
                except (OSError, ValueError, KeyError, TypeError):
                    gh_checks[f'github:{repo}:reachable'] = False
                    LOG.error('github_poll_failed repo=%s', repo)
            next_github = now + 60
        checks.update(gh_checks)
        failures = [(repo, run) for repo, runs in runs_by_repo.items() for run in runs if run['failed'] and run['updated_at'] >= state['monitoring_since']]
        def send(message):
            if request(webhook, payload={'text': message}) != b'ok':
                raise ValueError('Slack did not acknowledge')
            LOG.info('alert_delivered')
        try:
            deliver(checks, failures, state, send, now)
            checks['observer:slack-delivery'] = True
        except (OSError, ValueError):
            checks['observer:slack-delivery'] = False
            LOG.error('slack_delivery_failed')
        atomic(state_path, state, 0o600)
        atomic('/var/lib/duora-monitor/public/status.json', {
            'collected_at': now, 'checks': checks,
            'github': {repo: runs[:20] for repo, runs in runs_by_repo.items()}})
        # Optional independent external dead-man endpoint, never interpreted by an LLM.
        if config.get('heartbeat_url') and all(checks.values()):
            try:
                heartbeat = config['heartbeat_url']
                if urllib.parse.urlsplit(heartbeat).scheme != 'https':
                    raise ValueError('heartbeat requires HTTPS')
                request(heartbeat)
            except (OSError, ValueError):
                LOG.error('external_heartbeat_failed')
        time.sleep(15)


if __name__ == '__main__':
    logging.basicConfig(level=logging.INFO, format='%(asctime)s %(name)s %(message)s')
    try:
        main()
    except Exception:
        # Do not log URLs, response bodies, tokens, exception text or tracebacks.
        LOG.error('monitor_stopped_invalid_configuration_or_state')
        raise SystemExit(1)
