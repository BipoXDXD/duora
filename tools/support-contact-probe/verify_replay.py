"""Hosted-only, entirely synthetic provider-order and output-redaction checks."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile


def main():
    if (os.environ.get('GITHUB_ACTIONS') != 'true'
            or os.environ.get('RUNNER_ENVIRONMENT') != 'github-hosted'
            or sys.platform != 'linux'):
        raise SystemExit('Requires an ephemeral GitHub-hosted Linux runner')
    probe = Path(sys.argv[1]).resolve()
    application = Path(sys.argv[2]).resolve()
    approved = 'support@meetduora.com'
    private_marker = 'synthetic-do-not-export-value'
    completed = []
    with tempfile.TemporaryDirectory(prefix='support-provider-') as directory:
        root = Path(directory)
        staging = root / 'probe'
        staging.mkdir()
        # Exercise delivery without copies of the application libraries or helper deps file.
        for suffix in ('.dll', '.runtimeconfig.json'):
            shutil.copyfile(probe.with_suffix(suffix), staging / ('Duora.IntegrationTests' + suffix))
        for role in ('web', 'worker'):
            for case in ('json', 'environment', 'command_line', 'key_per_file', 'missing_directory', 'invalid_value'):
                content = root / (role + '-' + case)
                content.mkdir()
                (content / 'appsettings.json').write_text(json.dumps({'Duora': {'SupportContact': approved}}))
                environment = {'DOTNET_ENVIRONMENT': 'Production', 'ASPNETCORE_ENVIRONMENT': 'Production',
                               'UNRELATED_PRIVATE_VALUE': private_marker,
                               'ConnectionStrings__Duora': 'not-a-database-connection-' + private_marker}
                arguments = []
                expected_match = True
                expected_source = 'json'
                if case in ('environment', 'command_line'):
                    environment['Duora__SupportContact'] = 'other@example.invalid'
                    expected_match = False
                    expected_source = 'environment'
                if case == 'command_line':
                    arguments = ['--Duora:SupportContact=' + approved]
                    expected_match = True
                    expected_source = 'command_line'
                if case in ('key_per_file', 'invalid_value'):
                    secrets = content / 'secrets'
                    secrets.mkdir()
                    value = 'other@example.invalid' if case == 'key_per_file' else private_marker
                    (secrets / 'Duora__SupportContact').write_text(value + '\n')
                    environment.update(SecretsDirectory=str(secrets), Duora__SupportContact=approved)
                    arguments = ['--Duora:SupportContact', approved]
                    expected_match = False
                    expected_source = 'key_per_file'
                if case == 'missing_directory':
                    environment['SecretsDirectory'] = str(content / 'absent')
                capture = {'Role': role, 'ApplicationAssembly': str(application), 'ContentRoot': str(content),
                           'Environment': environment, 'Arguments': arguments}
                result = subprocess.run(['dotnet', str(staging / 'Duora.IntegrationTests.dll')],
                                        input=json.dumps(capture), capture_output=True, text=True, timeout=30)
                if private_marker in result.stdout + result.stderr:
                    raise AssertionError('private_value_leaked')
                observed = json.loads(result.stdout)
                label = role + '_' + case
                if case in ('missing_directory', 'invalid_value'):
                    if result.returncode != 2 or observed['status'] != 'unknown':
                        raise AssertionError(label + '_must_remain_unknown')
                elif (result.returncode != 0 or observed['status'] != 'replayed'
                      or observed['configuration_matches_approved'] is not expected_match
                      or observed['ioptions_matches_approved'] is not expected_match
                      or observed['source'] != expected_source
                      or observed['cached_runtime_options_observed'] is not False):
                    raise AssertionError(label + '_replay_failed_' + observed.get('stage', 'unknown'))
                completed.append(label)
    print(json.dumps({'synthetic_cases_passed': completed, 'production_accessed': False,
                      'cached_runtime_options_observed': False}))


if __name__ == '__main__':
    main()
