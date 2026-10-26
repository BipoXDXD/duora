"""Render the existing #174 client command and candidate profile; never run them.

This module is not an admission authority or a host orchestrator. Only 69/E3 may
bind reviewed CURRENT-host facts and enforce their independent lease/ledger and
cleanup/guards. Rendering always reports execution_authorized=false.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
import re

SOURCE = 'f3f8c38d4619cccaf32c0c66c03e7421e0ab5fff'
EXPECTED_MACHINE = '7c71fc9573038d7b0f81a308e907dc0c88b6582e0b565f10ef4d657f0cbcce25'
KIT_RUN = 40321926935
KIT_ARCHIVE_SHA256 = '0e6fa11c0f92f3d1ac2f08ff305fe85dfb926e320428e97d4661ea10c0178a89'
KIT_MANIFEST_SHA256 = 'f57def57991c587c6d06fef16d0d84c485d338c5519eda97849b673757bbc908'
PRESERVATION_REF = 'ca856aa1598bb915e33125a40753cdfdd8f8cec55c5a9ac51c134b28a2f9f7e6'
CARRIER = 'ghcr.io/bipoxdxd/duora-web@sha256:d648548203a4bd3ee1f874382c5a36c5856cf1129ecfaeabcdb0fb90df0c69dd'
DECODER = 'ghcr.io/bipoxdxd/duora-media-sanitizer@sha256:2f3f41425e670775797fd573d2c32011013676fe5803d596185ad01c69413d28'
ASSEMBLY = '/kit/backend/tests/Duora.MediaCorpusTests/bin/Debug/net10.0/Duora.MediaCorpusTests.dll'
CASE_CLASS = 'Duora.IntegrationTests.Media.SanitizerResourceEnvelopeTests'
HOST_FIELDS = {'schema', 'current_product_commit', 'machine_identity_sha256',
               'worker_uid', 'worker_gid', 'launcher_uid', 'control_gid',
               'control_socket', 'journal_directory', 'kit_root', 'results_root',
               'cgroup_parent', 'receipts'}
RECEIPTS = {'current_preservation', 'native174_admission', 'current_authoritative_activity',
            'integral_resource_budget', 'device_and_cgroup_enforcement',
            'rootless_launcher_identity_and_profile', 'carrier_framework_and_uid_mapping',
            'owned_cleanup_and_independent_supervisor', 'new_finite_passage'}
NON_ADMISSION = {'f4786de6e59f73712868cec6a43b418844d5960aec71d2d2966ce82c4bae2435',
                 'eeaf8ddfd3a71400a26d76cd2536b6bbddb3e9c4c5b554df00ce46ff2dc7e1c4',
                 '2eee318c75a484c080b5000a1f450d9bb1c0e2a904767a4065a136cfaa8e9f04',
                 '06e53bb0c8c4bba5d03f9ce9f7cf4156c55341e1a3a55329950488216fbb86b2',
                 PRESERVATION_REF,
                 'c0c43a96c32a4fe9736fa62d8913f270a1d429109f5673e12a36fdf84bd9cdaf'}


def require(condition: bool, message: str) -> None:
    if not condition:
        raise ValueError(message)


def pin(value: object) -> bool:
    return isinstance(value, str) and re.fullmatch(r'[0-9a-f]{64}', value) is not None


def owned_path(value: object, prefix: str) -> str:
    require(isinstance(value, str), 'unknown host path')
    path = str(value)
    require(re.fullmatch(r'/[A-Za-z0-9_./-]+', path) is not None,
            'host path must not inject mount options')
    require('..' not in PurePosixPath(path).parts and str(PurePosixPath(path)) == path,
            'host path must be canonical')
    require(path.startswith(prefix), 'host path must be inside its owned qualification namespace')
    return path


def profile_digest(profile: dict) -> str:
    """Exact existing Profile.digest encoding; keep producer proof/profile immutable."""
    fields = {key: value for key, value in profile.items() if key != 'schema'}
    return hashlib.sha256(json.dumps({'policy': 's2-candidate-v1', **fields},
                                     sort_keys=True).encode()).hexdigest()


def compose(host: dict, nonce: str) -> dict:
    require(set(host) == HOST_FIELDS and type(host['schema']) is int and host['schema'] == 1,
            'closed host binding schema required')
    require(re.fullmatch(r'[0-9a-f]{32}', nonce) is not None, 'operator nonce required')
    require(host['current_product_commit'] == SOURCE, 'consumer source must be reconciled explicitly')
    require(host['machine_identity_sha256'] == EXPECTED_MACHINE,
            'CURRENT target identity must be bound; OLD is not a target')
    receipts = host['receipts']
    require(isinstance(receipts, dict) and set(receipts) == RECEIPTS,
            'closed independent receipt references required')
    require(all(pin(value) for value in receipts.values()), 'missing operational receipt: STOP')
    require(receipts['current_preservation'] == PRESERVATION_REF,
            'the exact accepted F3 preservation baseline must be bound')
    require(all(value not in NON_ADMISSION for name, value in receipts.items()
                if name != 'current_preservation'),
            'observation/QG policy/returned261 passage cannot admit native174')
    for key in ('worker_uid', 'worker_gid', 'launcher_uid', 'control_gid'):
        require(type(host[key]) is int and 0 < host[key] < 2**31, 'CURRENT UID/GID is unknown')
    require(host['worker_uid'] != host['launcher_uid'], 'client and launcher authority must differ')
    control = owned_path(host['control_socket'], '/run/duora-qualification/')
    require(len(control.encode()) < 100, 'Unix control socket exceeds existing profile bound')
    journal = owned_path(host['journal_directory'], '/var/lib/duora-qualification/')
    kit = owned_path(host['kit_root'], '/opt/duora-qualification/')
    results = owned_path(host['results_root'], '/var/lib/duora-qualification/')
    marker = f'{KIT_RUN}-1'
    require(marker in PurePosixPath(kit).parts and marker in PurePosixPath(results).parts,
            'kit/results must be versioned; never replace old qualification state')
    require(PurePosixPath(results).name == nonce, 'fresh results must be owned by this nonce')
    require(not PurePosixPath(journal).is_relative_to(results) and
            not PurePosixPath(results).is_relative_to(journal),
            'client results cannot expose launcher authority/journal')
    parent = host['cgroup_parent']
    require(isinstance(parent, str) and
            re.fullmatch(r'duora174[0-9a-f]{32}\.slice', parent) is not None,
            'an independently admitted own native174 slice is required')
    require(parent == 'duora174' + nonce + '.slice', 'slice ownership must match this nonce')
    profile = {'schema': 1, 'image': DECODER,
               'runtime_socket': f"/run/user/{host['launcher_uid']}/docker.sock",
               'control_socket': control, 'journal_directory': journal,
               'worker_uid': host['worker_uid'], 'control_gid': host['control_gid']}
    digest = profile_digest(profile)
    control_root = str(PurePosixPath(control).parent)
    environment = {'DOTNET_ROOT': '/usr/share/dotnet', 'DOTNET_NOLOGO': 'true',
                   'DOTNET_CLI_TELEMETRY_OPTOUT': 'true', 'DUORA_NATIVE_CORPUS_REQUIRED': 'true',
                   'DUORA_NATIVE_CORPUS_SOCKET': control,
                   'DUORA_NATIVE_CORPUS_UID': str(host['launcher_uid']),
                   'DUORA_NATIVE_CORPUS_PROFILE': digest,
                   'DUORA_NATIVE_CORPUS_REPORT': '/results/report.json'}
    command = ['/usr/bin/docker', '--host', 'unix:///var/run/docker.sock', 'create',
               '--name', 'duora-qualification-native-' + nonce,
               '--label', 'io.duora.qualification=native-corpus', '--pull', 'never',
               '--network', 'none', '--user', f"{host['worker_uid']}:{host['worker_gid']}",
               '--group-add', str(host['control_gid']), '--cap-drop', 'ALL',
               '--security-opt', 'no-new-privileges:true', '--read-only',
               '--cgroup-parent', parent, '--memory', '1073741824',
               '--memory-swap', '1073741824', '--cpus', '0.5', '--pids-limit', '256',
               '--init', '--restart', 'no', '--workdir', '/kit', '--log-driver', 'local',
               '--log-opt', 'max-size=5m', '--log-opt', 'max-file=2',
               '--tmpfs', '/tmp:rw,nosuid,nodev,noexec,size=536870912,mode=1777',
               '--mount', 'type=bind,src=' + kit + ',dst=/kit,readonly',
               '--mount', 'type=bind,src=' + control_root + ',dst=' + control_root + ',readonly',
               '--mount', 'type=bind,src=' + results + ',dst=/results']
    for key, value in environment.items():
        command.extend(['--env', key + '=' + value])
    command.extend(['--entrypoint', '/usr/share/dotnet/dotnet', CARRIER, ASSEMBLY,
                    '-class', CASE_CLASS, '-showLiveOutput', '-reporter', 'verbose'])
    return {'schema': 1, 'scope': 'native174-source-plan-only', 'source': SOURCE,
            'execution_authorized': False, 'profile_admitted': False,
            'candidate_launcher_profile': profile, 'candidate_profile_sha256': digest,
            'kit_archive_sha256': KIT_ARCHIVE_SHA256, 'kit_manifest_sha256': KIT_MANIFEST_SHA256,
            'docker_create_argv': command, 'independent_receipt_references': receipts,
            'host_profile_or_corpus_green_claimed': False}


def unique_object(pairs: list) -> dict:
    result = {}
    for key, value in pairs:
        require(key not in result, 'duplicate binding field')
        result[key] = value
    return result


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--bindings', type=Path, required=True)
    parser.add_argument('--nonce', required=True)
    args = parser.parse_args()
    raw = args.bindings.read_bytes()
    require(len(raw) <= 16384, 'binding file too large')
    host = json.loads(raw, object_pairs_hook=unique_object)
    require(isinstance(host, dict), 'binding must be an object')
    print(json.dumps(compose(host, args.nonce), indent=2))


if __name__ == '__main__':
    main()
