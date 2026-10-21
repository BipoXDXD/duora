#!/usr/bin/env python3
"""Fail closed before starting the gateway. Does not print config or credentials."""
import json
import os
from pathlib import Path
import re
import stat
from capacity import check_capacity

IMAGE = re.compile(r'OPENCLAW_IMAGE=([a-z0-9][a-z0-9./:_-]*@sha256:[0-9a-f]{64})\n?\Z')


def pinned(text):
    match = IMAGE.fullmatch(text)
    if not match:
        raise ValueError('image.env must contain exactly OPENCLAW_IMAGE=<registry/image>@sha256:<digest>')
    return match[1]


def validate(config):
    raw = json.dumps(config)
    if 'REPLACE_' in raw:
        raise ValueError('replace installation placeholders')
    slack = config['channels']['slack']
    if slack['mode'] != 'socket' or slack['dmPolicy'] != 'disabled' or slack['groupPolicy'] != 'allowlist':
        raise ValueError('Slack ingress policy changed')
    if slack.get('allowBots') is not False or len(slack['channels']) != 1:
        raise ValueError('one explicit channel required, bots denied')
    for channel, entry in slack['channels'].items():
        if not re.fullmatch(r'[CG][A-Z0-9]{8,}', channel) or not entry.get('requireMention'):
            raise ValueError('invalid channel or mention policy')
        if not entry['users'] or any(not re.fullmatch(r'U[A-Z0-9]{8,}', user) for user in entry['users']):
            raise ValueError('explicit Slack member IDs required')
    if config['gateway']['bind'] != 'loopback' or config['gateway']['auth']['mode'] != 'token':
        raise ValueError('gateway must be authenticated loopback')
    if config['tools'].get('elevated', {}).get('enabled') is not False:
        raise ValueError('elevated tools forbidden')
    if config['tools']['profile'] != 'minimal' or config['tools']['alsoAllow'] != ['duora__*']:
        raise ValueError('unexpected tool allowlist')
    required = {'gateway', 'group:runtime', 'group:fs', 'group:web', 'group:ui',
                'group:automation', 'group:nodes', 'group:messaging', 'sessions_spawn'}
    if not required <= set(config['tools']['deny']):
        raise ValueError('missing tool denials')
    for command in ('native', 'nativeSkills', 'text', 'bash', 'config', 'restart'):
        if config['commands'].get(command) is not False:
            raise ValueError('chat administration forbidden')
    if set(config['mcp']['servers']) != {'duora'}:
        raise ValueError('unexpected MCP server')
    mcp = config['mcp']['servers']['duora']
    if mcp['command'] != 'node' or mcp['args'] != ['/opt/duora-agent/ops-mcp.mjs']:
        raise ValueError('unexpected MCP executable')


def secure(path, owner, mode):
    info = path.lstat()
    if not stat.S_ISREG(info.st_mode) or info.st_uid != owner or stat.S_IMODE(info.st_mode) != mode:
        raise ValueError(f'unsafe ownership/mode/type: {path.name}')


def main():
    check_capacity()
    if os.geteuid() != 0:
        raise ValueError('administrator execution required')
    if not os.path.ismount('/var/lib/duoraagent'):
        raise ValueError('dedicated bounded state filesystem is not mounted')
    root = Path('/etc/duora-agent')
    secure(root / 'image.env', 0, 0o600)
    secure(root / 'openclaw.json', 0, 0o644)
    pinned((root / 'image.env').read_text())
    validate(json.loads((root / 'openclaw.json').read_text()))
    for name in ('SLACK_APP_TOKEN', 'SLACK_BOT_TOKEN', 'OPENCLAW_GATEWAY_TOKEN', 'ANTHROPIC_API_KEY'):
        path = root / 'secrets' / name
        secure(path, 22001, 0o400)
        if path.stat().st_size < 20:
            raise ValueError(f'credential missing: {name}')
    print('preflight: local policy and credential file permissions passed; live validation still required')


if __name__ == '__main__':
    try:
        main()
    except (ValueError, OSError, KeyError) as error:
        raise SystemExit(str(error))
