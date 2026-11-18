"""Use the real repository gate to check a critical finding with the backport VEX active."""

import os
from pathlib import Path
import shutil
import subprocess
from tempfile import mkdtemp

from check_picomatch_backport import ADVISORY_IDS, TARGET, VEX_SOURCE, read_json, require

GATE = 'scripts/supply-chain/run_trivy_gate.sh'
FIXTURE = 'tools/trivy-openvex-regression/requirements.txt'
CRITICAL = 'CVE-2020-14343'


def has_critical_fixture(report):
    return any(
        finding['VulnerabilityID'] == CRITICAL
        and finding['PkgName'].lower() == 'pyyaml'
        and finding['Severity'] == 'CRITICAL'
        for result in report['Results'] if result['Target'] == FIXTURE
        for finding in result.get('Vulnerabilities', [])
    )


def verify_reports(reports):
    require((reports / 'policy-exit-code.txt').read_text().strip() == '1',
            'the audited Trivy policy scan did not exit with code 1')
    raw = read_json(reports / 'trivy-raw.json')
    policy = read_json(reports / 'trivy-policy.json')
    require(has_critical_fixture(raw) and has_critical_fixture(policy),
            f'{CRITICAL} must be present and CRITICAL in both scan reports')
    blockers = sum(
        len(result.get('Vulnerabilities', []))
        + sum(finding['Status'] == 'FAIL' for finding in result.get('Misconfigurations', []))
        for result in policy['Results']
    )
    require(blockers == 1, f'{CRITICAL} must be the only blocker in the regression scan')
    require(any(
        row['Source'] == VEX_SOURCE and row['Status'] == 'fixed'
        and row['Finding']['VulnerabilityID'] in ADVISORY_IDS
        for result in policy['Results'] if result['Target'] == TARGET
        for row in result.get('ExperimentalModifiedFindings', [])
    ), 'the policy scan did not apply the Picomatch OpenVEX declaration')


def main():
    control = Path(os.environ['SUPPLY_CHAIN_REPORTS'])
    if not (control / 'picomatch.openvex.json').is_file():
        print('OpenVEX regression not exercised: the normal scan found no Picomatch advisory.')
        return

    source = Path(os.environ['GITHUB_WORKSPACE'])
    scratch = Path(mkdtemp(prefix='openvex-regression.', dir=os.environ['RUNNER_TEMP'])).resolve()
    target = scratch / 'repo'
    commit = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=source, text=True).strip()
    subprocess.run(['git', 'clone', '--quiet', '--shared', '--no-checkout', str(source), str(target)], check=True)
    try:
        subprocess.run(['git', 'checkout', '--quiet', '--detach', commit], cwd=target, check=True)
        shutil.copytree(source / 'frontend/node_modules', target / 'frontend/node_modules', symlinks=True)
        fixture = target / FIXTURE
        fixture.parent.mkdir()
        # Scanner input only. Nothing from this manifest is installed or executed.
        fixture.write_text('PyYAML==5.3.1\n', encoding='utf-8')

        environment_file = scratch / 'gate.env'
        env = dict(os.environ, GITHUB_WORKSPACE=str(target), RUNNER_TEMP=str(scratch),
                   GITHUB_ENV=str(environment_file))
        result = subprocess.run(['bash', GATE], cwd=target, env=env, check=False)
        key, _, path = environment_file.read_text(encoding='utf-8').strip().partition('=')
        require(key == 'SUPPLY_CHAIN_REPORTS' and Path(path).parent == scratch, 'missing gate reports')
        with Path(os.environ['GITHUB_ENV']).open('a', encoding='utf-8') as environment:
            print(f'OPENVEX_TEST_REPORTS={path}', file=environment)
        verify_reports(Path(path))
        require(result.returncode == 1, f'expected gate exit 1, got {result.returncode}')
        print(f'OpenVEX active. {CRITICAL} stayed CRITICAL, and the repository gate exited 1.')
    finally:
        shutil.rmtree(target)


if __name__ == '__main__':
    main()
