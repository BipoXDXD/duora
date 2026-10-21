"""Real loopback TLS with disposable PKI; no tokens, host access or external services."""
import hashlib
import http.client
import json
from pathlib import Path
import shutil
import ssl
import subprocess
import tempfile
import threading
import time
import unittest
import urllib.error
from telemetry import tls_context
from telemetry_receiver import Receiver, Store
from telemetry_sender import endpoint, send
from test_telemetry import sample


class MutualTLSTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temp = tempfile.TemporaryDirectory()
        cls.root = Path(cls.temp.name)
        openssl = shutil.which('openssl')
        if not openssl:
            raise RuntimeError('OpenSSL is required for the mTLS regression suite')
        def run(*args):
            subprocess.run([openssl, *args], cwd=cls.root, check=True, stdout=subprocess.DEVNULL,
                           stderr=subprocess.DEVNULL, timeout=20)
        run('req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-subj', '/CN=Duora disposable test CA',
            '-keyout', 'ca.key', '-out', 'ca.pem', '-days', '1')
        for name, usage in (('server', 'serverAuth'), ('client', 'clientAuth'), ('other', 'clientAuth'), ('expired', 'clientAuth')):
            folder = cls.root / name
            folder.mkdir()
            shutil.copy(cls.root / 'ca.pem', folder / 'ca.pem')
            run('req', '-new', '-newkey', 'rsa:2048', '-nodes', '-subj', f'/CN={name}',
                '-keyout', f'{name}/key.pem', '-out', f'{name}.csr')
            extension = cls.root / f'{name}.ext'
            extension.write_text(f'basicConstraints=CA:FALSE\nkeyUsage=digitalSignature,keyEncipherment\nextendedKeyUsage={usage}\nsubjectAltName=DNS:localhost,IP:127.0.0.1\n')
            if name == 'expired':
                (cls.root / 'issued').mkdir()
                (cls.root / 'index.txt').write_text('')
                (cls.root / 'serial').write_text('1000\n')
                (cls.root / 'ca.cnf').write_text('[ca]\ndefault_ca=test\n[test]\ndatabase=index.txt\n'
                    'serial=serial\nnew_certs_dir=issued\ncertificate=ca.pem\nprivate_key=ca.key\n'
                    'default_md=sha256\npolicy=policy\n[policy]\ncommonName=supplied\n'
                    '[client_cert]\nbasicConstraints=CA:FALSE\nkeyUsage=digitalSignature\nextendedKeyUsage=clientAuth\n')
                run('ca', '-batch', '-config', 'ca.cnf', '-in', f'{name}.csr', '-out', f'{name}/cert.pem',
                    '-startdate', '20200101000000Z', '-enddate', '20200102000000Z', '-extensions', 'client_cert', '-notext')
            else:
                run('x509', '-req', '-in', f'{name}.csr', '-CA', 'ca.pem', '-CAkey', 'ca.key',
                    '-CAcreateserial', '-out', f'{name}/cert.pem', '-days', '1', '-extfile', str(extension))
        der = ssl.PEM_cert_to_DER_cert((cls.root / 'client/cert.pem').read_text())
        cls.pin = hashlib.sha256(der).hexdigest()

    @classmethod
    def tearDownClass(cls):
        cls.temp.cleanup()

    def setUp(self):
        self.state = tempfile.TemporaryDirectory()
        self.path = Path(self.state.name) / 'host.json'
        self.server = Receiver(('127.0.0.1', 0), tls_context('server', self.root / 'server'), Store(self.path, [self.pin]))
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()
        self.port = self.server.server_address[1]
        self.url = f'https://127.0.0.1:{self.port}/v1/telemetry'
        self.client = tls_context('client', self.root / 'client')

    def tearDown(self):
        self.server.shutdown()
        self.server.server_close()
        self.thread.join(timeout=10)
        self.state.cleanup()

    def test_success_idempotent_ack_and_monotonic_publication(self):
        value = sample(time.time() - 1)
        send(self.url, value, self.client)
        modified = self.path.stat().st_mtime_ns
        send(self.url, value, self.client)
        self.assertEqual(self.path.stat().st_mtime_ns, modified)
        with self.assertRaises(urllib.error.HTTPError):
            send(self.url, sample(value['collected_at'] - 1), self.client)
        self.assertEqual(json.loads(self.path.read_text()), value)

    def test_untrusted_unpinned_and_missing_client_certificates(self):
        # Valid CA alone is insufficient: only the pinned client may publish.
        with self.assertRaises(urllib.error.HTTPError):
            send(self.url, sample(time.time() - 1), tls_context('client', self.root / 'other'))
        missing = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
        missing.load_verify_locations(cafile=str(self.root / 'ca.pem'))
        with self.assertRaises((OSError, urllib.error.URLError)):
            send(self.url, sample(time.time() - 1), missing)
        # System trust does not trust the disposable CA.
        with self.assertRaises((OSError, urllib.error.URLError)):
            send(self.url, sample(time.time() - 1), ssl.create_default_context())
        self.assertFalse(self.path.exists())

    def test_server_hostname_verified(self):
        import socket
        with socket.create_connection(('127.0.0.1', self.port), timeout=3) as raw:
            with self.assertRaises(ssl.SSLCertVerificationError):
                self.client.wrap_socket(raw, server_hostname='wrong.invalid')
        self.assertFalse(self.path.exists())

    def test_http_limits_and_untrusted_payloads(self):
        from telemetry import MAX_BYTES
        for body, headers, path in ((b'x' * (MAX_BYTES + 1), {'Content-Type': 'application/json'}, '/v1/telemetry'),
                                  (b'{}', {'Content-Type': 'application/json', 'Transfer-Encoding': 'chunked'}, '/v1/telemetry'),
                                  (b'{}', {'Content-Type': 'application/json'}, '/v1/telemetry?exec=id'),
                                  (b'{"collected_at":1,"collected_at":2}', {'Content-Type': 'application/json'}, '/v1/telemetry')):
            connection = http.client.HTTPSConnection('127.0.0.1', self.port, context=self.client, timeout=3)
            try:
                connection.request('POST', path, body=body, headers=headers)
                self.assertEqual(connection.getresponse().status, 400)
            finally:
                connection.close()
        self.assertFalse(self.path.exists())

    def test_sender_rejects_unsafe_endpoint(self):
        for url in ('http://ops.test:8443/v1/telemetry', 'https://user:secret@ops.test:8443/v1/telemetry',
                    'https://ops.test:8443/v1/telemetry?token=secret', 'https://ops.test:8443/command',
                    'https://ops.test/v1/telemetry'):
            with self.assertRaises(ValueError):
                endpoint(url)

    def test_expired_client_certificate_and_source_address_denied(self):
        expired = ssl.PEM_cert_to_DER_cert((self.root / 'expired/cert.pem').read_text())
        self.server.store.fingerprints = [hashlib.sha256(expired).hexdigest()]
        with self.assertRaises((OSError, urllib.error.URLError)):
            send(self.url, sample(time.time() - 1), tls_context('client', self.root / 'expired'))
        self.server.source_ipv4 = '192.0.2.100'
        with self.assertRaises((OSError, urllib.error.URLError)):
            send(self.url, sample(time.time() - 1), self.client)
        self.assertFalse(self.path.exists())

    def test_sender_does_not_follow_redirect(self):
        from telemetry_receiver import Handler
        class Redirect(Handler):
            calls = 0
            def do_POST(self):
                Redirect.calls += 1
                self.send_response(307)
                self.send_header('Location', '/forbidden')
                self.send_header('Content-Length', '0')
                self.end_headers()
        self.server.RequestHandlerClass = Redirect
        with self.assertRaisesRegex(ValueError, 'redirect refused'):
            send(self.url, sample(time.time() - 1), self.client)
        self.assertEqual(Redirect.calls, 1)

    def test_slow_headers_have_an_absolute_deadline(self):
        import socket
        with socket.create_connection(('127.0.0.1', self.port), timeout=2) as raw:
            with self.client.wrap_socket(raw, server_hostname='localhost') as connection:
                connection.settimeout(8)
                connection.sendall(b'POST /v1/telemetry HTTP/1.0\r\nContent-Type:')
                started = time.monotonic()
                # Without an absolute deadline a peer trickling bytes could occupy the sole handler forever.
                time.sleep(2)
                connection.sendall(b' application/json')
                self.assertEqual(connection.recv(1), b'')
                self.assertLess(time.monotonic() - started, 7)
