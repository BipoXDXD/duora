#!/usr/bin/env python3
"""One-way, unprivileged snapshot delivery. No response payload becomes code or telemetry."""
import json
import logging
import os
from pathlib import Path
import time
import urllib.parse
import urllib.request
from monitor import NoRedirect
from telemetry import MAX_BYTES, bounded_read, snapshot, tls_context

LOG = logging.getLogger('duora-telemetry')


def endpoint(value):
    url = urllib.parse.urlsplit(value)
    if (url.scheme != 'https' or not url.hostname or url.username or url.password or url.query
            or url.fragment or url.path != '/v1/telemetry' or not url.port or url.port < 1024):
        raise ValueError('explicit HTTPS receiver endpoint required')
    return value


def send(url, value, context):
    raw = json.dumps(snapshot(value, time.time()), allow_nan=False).encode()
    if len(raw) > MAX_BYTES:
        raise ValueError('oversize snapshot')
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect,
                                        urllib.request.HTTPSHandler(context=context))
    request = urllib.request.Request(endpoint(url), data=raw, headers={'Content-Type': 'application/json'}, method='POST')
    with opener.open(request, timeout=8) as response:
        if response.status != 204:
            raise ValueError('receiver did not acknowledge')
    # Do not follow redirects, read a body, execute instructions, or persist returned data.


def main():
    config = json.loads(Path('/etc/duora-telemetry/sender.json').read_text())
    if set(config) != {'endpoint'}:
        raise ValueError('unexpected sender configuration')
    value = snapshot(bounded_read('/var/lib/duora-telemetry/host.json'), time.time())
    send(config['endpoint'], value, tls_context('client', Path(os.environ['CREDENTIALS_DIRECTORY'])))
    LOG.info('snapshot_delivered')


if __name__ == '__main__':
    logging.basicConfig(level=logging.INFO, format='%(name)s %(message)s')
    try:
        main()
    except Exception:
        LOG.error('snapshot_delivery_failed')
        raise SystemExit(1)
