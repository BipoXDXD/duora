#!/usr/bin/env python3
"""Unprivileged, bounded mTLS receiver on the operations VPS; accepts a single fixed document."""
import hashlib
import hmac
from http.server import BaseHTTPRequestHandler, HTTPServer
import ipaddress
import json
import logging
import os
from pathlib import Path
import ssl
import socket
import threading
import time
from typing import cast
from telemetry import MAX_BYTES, bounded_read, confirm_durable, decode, pins, publish, snapshot, tls_context

LOG = logging.getLogger('duora-telemetry')


class Store:
    def __init__(self, path, fingerprints):
        self.path = Path(path)
        self.fingerprints = pins(fingerprints)

    def accept(self, raw, fingerprint, now):
        if not any(hmac.compare_digest(fingerprint, pin) for pin in self.fingerprints):
            raise ValueError('client identity denied')
        current = snapshot(decode(raw), now)
        if self.path.exists():
            previous = bounded_read(self.path)
            # Corrupt state is never silently reset. Operator must recover the watermark.
            stamp = previous['collected_at']
            snapshot(previous, stamp)  # Also validate persisted fields and timestamp.
            if current['collected_at'] < stamp:
                raise ValueError('replayed snapshot')
            if current['collected_at'] == stamp:
                if current != previous:
                    raise ValueError('conflicting snapshot')
                confirm_durable(self.path)
                return 204  # Lost ACK retry: no rewrite or extension of freshness.
        publish(self.path, current)
        return 204


class Handler(BaseHTTPRequestHandler):
    server_version = 'DuoraTelemetry'
    sys_version = ''

    def log_message(self, format, *args):
        pass  # No URLs, request lines, headers, certificate identities or bodies in journal.

    def handle(self):
        # Absolute deadline covers slow headers/body, not only inactivity between bytes.
        def expire():
            try:
                self.connection.shutdown(socket.SHUT_RDWR)
            except OSError:
                pass
        timer = threading.Timer(5, expire)
        timer.daemon = True
        timer.start()
        try:
            super().handle()
        finally:
            timer.cancel()

    def do_POST(self):
        try:
            lengths = self.headers.get_all('Content-Length', [])
            if (self.path != '/v1/telemetry' or self.headers.get('Transfer-Encoding') is not None
                    or len(lengths) != 1 or not lengths[0].isascii() or not lengths[0].isdigit()
                    or self.headers.get_all('Content-Type') != ['application/json']):
                raise ValueError('invalid framing')
            length = int(lengths[0])
            if not 0 < length <= MAX_BYTES:
                raise ValueError('invalid size')
            raw = self.rfile.read(length)
            if len(raw) != length:
                raise ValueError('incomplete body')
            cert = self.connection.getpeercert(binary_form=True)
            if not cert:
                raise ValueError('client certificate required')
            status = cast('Receiver', self.server).store.accept(raw, hashlib.sha256(cert).hexdigest(), time.time())
        except (ValueError, KeyError, TypeError, OSError, OverflowError):
            LOG.warning('snapshot_rejected')
            status = 400
        self.send_response(status)
        self.send_header('Content-Length', '0')
        self.send_header('Connection', 'close')
        self.end_headers()
        self.close_connection = True

    def do_GET(self):
        self.send_error(405, 'method denied')


class Receiver(HTTPServer):
    request_queue_size = 4
    allow_reuse_address = True

    def __init__(self, address, context, store, source_ipv4='127.0.0.1'):
        self.source_ipv4 = str(ipaddress.IPv4Address(source_ipv4))
        self.context = context
        self.store = store
        super().__init__(address, Handler)

    def get_request(self):
        connection, address = super().get_request()
        if address[0] != self.source_ipv4:
            connection.close()
            raise OSError('source denied')
        connection.settimeout(5)
        try:
            return self.context.wrap_socket(connection, server_side=True), address
        except (OSError, ssl.SSLError):
            connection.close()
            raise

    def handle_error(self, request, client_address):
        LOG.warning('request_failed')


def main():
    config = json.loads(Path('/etc/duora-telemetry/receiver.json').read_text())
    if set(config) != {'bind', 'port', 'client_fingerprints', 'source_ipv4'}:
        raise ValueError('invalid receiver configuration')
    address = ipaddress.IPv4Address(config['bind'])
    if address.is_unspecified or address.is_multicast:
        raise ValueError('explicit receiver IPv4 address required')
    if type(config['port']) is not int or not 1024 <= config['port'] <= 65535:
        raise ValueError('unprivileged receiver port required')
    credentials = Path(os.environ['CREDENTIALS_DIRECTORY'])
    with Receiver((str(address), config['port']), tls_context('server', credentials),
                  Store('/var/lib/duora-remote/host.json', config['client_fingerprints']),
                  source_ipv4=config['source_ipv4']) as server:
        server.serve_forever()


if __name__ == '__main__':
    logging.basicConfig(level=logging.INFO, format='%(name)s %(message)s')
    try:
        main()
    except Exception:
        LOG.error('receiver_stopped')
        raise SystemExit(1)
