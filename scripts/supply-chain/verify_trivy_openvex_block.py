"""Check Trivy policy and the repository audit with inert scanner responses."""

from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import os
from pathlib import Path
import subprocess
from tempfile import TemporaryDirectory, mkdtemp
from threading import Thread

from check_picomatch_backport import (
    ADVISORY_IDS, PACKAGE, PURL, TARGET, VERSION, VEX_SOURCE, audit, read_json, require, sha256, write_json,
)

BACKPORT = 'CVE-2026-112366'
CRITICAL = 'CVE-2099-10001'
SEVERITY_HIGH = 3
SEVERITY_CRITICAL = 4
FIXTURE = 'fixture/critical.json'
FIXTURE_PACKAGE = 'fixture-critical'
SCAN_PATH = '/twirp/trivy.scanner.v1.Scanner/Scan'
CACHE_PATHS = {
    f'/twirp/trivy.cache.v1.Cache/{method}'
    for method in ('MissingBlobs', 'PutArtifact', 'PutBlob', 'DeleteBlobs')
}


# Minimal protobuf fields from rpc/{scanner,common}/service.proto at Trivy v0.75.0.
# Only vulnerability metadata is serialized. The fixture never contains package code.
def varint(value):
    data = bytearray()
    while value >= 128:
        data.append((value & 127) | 128)
        value >>= 7
    data.append(value)
    return bytes(data)


def field(number, value):
    if isinstance(value, int):
        return varint(number << 3) + varint(value)
    data = value.encode() if isinstance(value, str) else value
    return varint((number << 3) | 2) + varint(len(data)) + data


def result_fixture(target, ecosystem, package, version, purl, advisory, severity):
    package_id = f'{package}@{version}'
    identifier = field(1, purl) + field(3, package_id)
    inventory = field(1, package) + field(2, version) + field(13, package_id) + field(19, identifier)
    finding = (field(1, advisory) + field(2, package) + field(3, version) + field(7, severity)
               + field(23, package_id) + field(25, identifier))
    result = (field(1, target) + field(2, finding) + field(3, ecosystem)
              + field(5, inventory) + field(6, 'lang-pkgs'))
    return field(3, result)


class ScannerFixture(ThreadingHTTPServer):
    response = b''
    scans = 0


class FixtureHandler(BaseHTTPRequestHandler):
    def reply(self, data, content_type):
        self.send_response(200)
        self.send_header('Content-Type', content_type)
        self.send_header('Content-Length', str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        if self.path == '/version':
            self.reply(b'{"Version":"fixture"}', 'application/json')
        else:
            self.send_error(404)

    def do_POST(self):
        length = int(self.headers.get('Content-Length', '0'))
        if not 0 <= length <= 1024 * 1024 or self.headers.get('Content-Type') != 'application/protobuf':
            self.send_error(400)
            return
        self.rfile.read(length)
        if self.path == SCAN_PATH:
            server = self.server
            assert isinstance(server, ScannerFixture)
            server.scans += 1
            self.reply(server.response, 'application/protobuf')
        elif self.path in CACHE_PATHS:
            # The fixture scanner has no cache. Acknowledge writes and declare no missing blobs.
            self.reply(b'', 'application/protobuf')
        else:
            self.send_error(404)

    def log_message(self, format, *args):
        pass


def has_critical_fixture(report):
    return any(
        finding['VulnerabilityID'] == CRITICAL and finding['PkgName'] == FIXTURE_PACKAGE
        and finding['Severity'] == 'CRITICAL'
        for result in report['Results'] if result['Target'] == FIXTURE
        for finding in result.get('Vulnerabilities', [])
    )


def count_blockers(report):
    return sum(
        len(result.get('Vulnerabilities', []))
        + sum(finding['Status'] == 'FAIL' for finding in result.get('Misconfigurations', []))
        for result in report['Results']
    )


def verify_fixed(report):
    require(any(
        row['Source'] == VEX_SOURCE and row['Status'] == 'fixed'
        and row['Finding']['VulnerabilityID'] in ADVISORY_IDS
        for result in report['Results'] if result['Target'] == TARGET
        for row in result.get('ExperimentalModifiedFindings', [])
    ), 'the policy scan did not apply the Picomatch OpenVEX declaration')


def verify_reports(reports):
    require((reports / 'policy-exit-code.txt').read_text().strip() == '1',
            'the audited Trivy policy scan did not exit with code 1')
    raw = read_json(reports / 'trivy-raw.json')
    policy = read_json(reports / 'trivy-policy.json')
    require(has_critical_fixture(raw) and has_critical_fixture(policy),
            f'{CRITICAL} must be present and CRITICAL in both scan reports')
    require(count_blockers(policy) == 1, f'{CRITICAL} must be the only blocker in the regression scan')
    verify_fixed(policy)


def scan(server, source, reports, target, name, expected_exit, *, vex=False):
    output = f'{name}.json'
    args = [
        'docker', 'run', '--rm', '--pull=never', '--network', 'host',
        '-v', f'{target}:/input:ro', '-v', f'{reports}:/reports',
        '-v', f'{source / ".trivyignore"}:/fixture-ignore:ro',
        os.environ['TRIVY_IMAGE'], '--config', '', 'fs', '/input',
        '--server', f'http://127.0.0.1:{server.server_port}', '--offline-scan', '--scanners', 'vuln',
        '--skip-version-check', '--disable-telemetry', '--timeout', '45s',
        '--severity', 'HIGH,CRITICAL', '--format', 'json', '--list-all-pkgs', '--show-suppressed',
        '--ignorefile', '/fixture-ignore' if vex else '/dev/null',
        '--exit-code', '1', '--output', f'/reports/{output}',
    ]
    if vex:
        args.extend(('--vex', VEX_SOURCE))
    previously_scanned = server.scans
    result = subprocess.run(args, check=False, timeout=60)
    (reports / f'{name.removeprefix("trivy-")}-exit-code.txt').write_text(f'{result.returncode}\n', encoding='utf-8')
    require(result.returncode == expected_exit, f'{name} expected exit {expected_exit}, got {result.returncode}')
    require(server.scans == previously_scanned + 1, f'{name} did not consume the static scanner response')
    return reports / output


def main():
    source = Path(os.environ['GITHUB_WORKSPACE'])
    run = os.environ['GITHUB_RUN_ID']
    attempt = os.environ['GITHUB_RUN_ATTEMPT']
    commit = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=source, text=True).strip()
    require(commit == os.environ['GITHUB_SHA'] and run.isdecimal() and attempt.isdecimal(), 'unexpected checkout identity')
    reports = Path(mkdtemp(prefix='openvex-regression.', dir=os.environ['RUNNER_TEMP'])).resolve()
    with Path(os.environ['GITHUB_ENV']).open('a', encoding='utf-8') as environment:
        print(f'OPENVEX_TEST_REPORTS={reports}', file=environment)

    backport = result_fixture(TARGET, 'pnpm', PACKAGE, VERSION, PURL, BACKPORT, SEVERITY_HIGH)
    independent = result_fixture(FIXTURE, 'pip', FIXTURE_PACKAGE, '1.0.0',
                                 f'pkg:pypi/{FIXTURE_PACKAGE}@1.0.0', CRITICAL, SEVERITY_CRITICAL)
    (reports / 'scanner-response.pb').write_bytes(backport + independent)
    (reports / 'scanner-control.pb').write_bytes(backport)
    write_json(reports / Path(VEX_SOURCE).name, {
        '@context': 'https://openvex.dev/ns/v0.2.0',
        '@id': f'urn:duora:fixture:openvex:{commit}',
        'author': 'Duora CI fixture',
        'timestamp': '2026-11-19T00:00:00Z',
        'version': 1,
        'statements': [{'vulnerability': {'name': BACKPORT}, 'products': [{'@id': PURL}],
                        'status': 'fixed', 'action_statement': 'Static policy fixture only.'}],
    })

    with (TemporaryDirectory(dir=os.environ['RUNNER_TEMP']) as temporary,
          ScannerFixture(('127.0.0.1', 0), FixtureHandler) as server):
        target = Path(temporary)
        (target / 'fixture.txt').write_text('Inert Trivy policy fixture.\n', encoding='utf-8')
        worker = Thread(target=server.serve_forever)
        worker.start()
        try:
            server.response = backport + independent
            raw = scan(server, source, reports, target, 'trivy-raw', 1)
            policy = scan(server, source, reports, target, 'trivy-policy', 1, vex=True)
            server.response = backport
            control = scan(server, source, reports, target, 'trivy-control', 0, vex=True)
        finally:
            server.shutdown()
            worker.join()

    # The production audit must account for every finding and reject a changed or lost one.
    require(count_blockers(read_json(raw)) == 2, 'both findings must block without VEX')
    audit(raw, policy)
    verify_reports(reports)
    require(count_blockers(read_json(control)) == 0, 'the VEX-only control must have no blockers')
    verify_fixed(read_json(control))
    control_raw = read_json(raw)
    control_raw['Results'] = [r for r in control_raw['Results'] if r['Target'] == TARGET]
    control_input = reports / 'trivy-control-raw.json'
    write_json(control_input, control_raw)
    audit(control_input, control)

    write_json(reports / 'receipt.json', {
        'schema': 1, 'checkout': commit, 'run': run, 'attempt': attempt, 'trivyImage': os.environ['TRIVY_IMAGE'],
        'scope': 'Trivy policy with inert scanner responses; no advisory database',
        'exitCodes': {'withoutVex': 1, 'withIndependentCritical': 1, 'vexOnlyControl': 0},
        'fixtureCritical': CRITICAL,
        'sha256': {file.name: sha256(file) for file in sorted(reports.iterdir())},
    })
    print(f'OpenVEX policy exercised. {CRITICAL} remained the only CRITICAL blocker. VEX-only control exited 0.')


if __name__ == '__main__':
    main()
