"""Publish multi-platform release indices from the exact native, scanned digests."""

import json
import os
from pathlib import Path
import re
import subprocess
import sys

IMAGES = ('web', 'worker', 'migrator', 'edge', 'postgres')
ARCHITECTURES = ('amd64', 'arm64')
REPOSITORY = 'BipoXDXD/duora'
RUN_PREFIX = f'https://github.com/{REPOSITORY}/actions/runs/'


def positive(value):
    if not re.fullmatch('[1-9][0-9]*', value):
        raise ValueError('Positive workflow ID/attempt required')
    return value


def select_artifacts(response):
    artifacts = response['artifacts']
    if response['total_count'] != len(artifacts):
        raise ValueError('Incomplete artifact inventory')
    result = {}
    for arch in ARCHITECTURES:
        candidates = [a for a in artifacts if a.get('expired') is False
                      and re.fullmatch(f'release-{arch}-[1-9][0-9]*', a.get('name', ''))]
        if not candidates:
            raise ValueError(f'Missing unexpired {arch} publication')
        result[arch] = max(candidates, key=lambda a: a['id'])['name']
    return result


def validate_publications(documents, commit, ci_run, ci_attempt, run_id):
    if not re.fullmatch('[0-9a-f]{40}', commit):
        raise ValueError('Full lowercase commit SHA required')
    for value in (ci_run, ci_attempt, run_id):
        positive(value)
    if set(documents) != set(ARCHITECTURES):
        raise ValueError('Both native publications are required')
    sources = {image: {} for image in IMAGES}
    for arch, document in documents.items():
        if (document.get('commit') != commit or document.get('architecture') != arch
                or document.get('ciRun') != f'{RUN_PREFIX}{ci_run}/attempts/{ci_attempt}'
                or not re.fullmatch(re.escape(f'{RUN_PREFIX}{run_id}/attempts/') + '[1-9][0-9]*',
                                    document.get('publishRun', ''))
                or set(document.get('images', {})) != set(IMAGES)):
            raise ValueError('Native publication identity or image set mismatch')
        for image, reference in document['images'].items():
            if not re.fullmatch(f'ghcr.io/bipoxdxd/duora-{image}@sha256:[0-9a-f]{{64}}', reference):
                raise ValueError('Only exact Duora repository digests qualify')
            sources[image][arch] = reference
    return sources


def native_descriptors(manifest):
    return [item for item in manifest.get('manifests', [])
            if item.get('platform') != {'os': 'unknown', 'architecture': 'unknown'}]


def source_digest(manifest, config, arch, digest):
    configs = [config] if 'os' in config else list(config.values())
    configs = [c for c in configs if isinstance(c, dict) and c.get('os') != 'unknown']
    if (len(configs) != 1 or configs[0].get('os') != 'linux'
            or configs[0].get('architecture') != arch):
        raise ValueError('Native image configuration does not match the declared architecture')
    if 'manifests' in manifest:
        descriptors = native_descriptors(manifest)
        if (len(descriptors) != 1 or descriptors[0]['platform'].get('os') != 'linux'
                or descriptors[0]['platform'].get('architecture') != arch):
            raise ValueError('Native source must contain only its declared platform')
        return descriptors[0]['digest']
    if 'config' not in manifest:
        raise ValueError('Native image manifest required')
    return digest


def validate_index(index, expected):
    descriptors = native_descriptors(index)
    if len(descriptors) != 2:
        raise ValueError('Release index needs exactly AMD64 and ARM64')
    actual = {}
    for item in descriptors:
        platform = item.get('platform', {})
        arch = platform.get('architecture')
        if platform.get('os') != 'linux' or arch not in ARCHITECTURES or arch in actual:
            raise ValueError('Unexpected or duplicate release platform')
        actual[arch] = item['digest']
    if actual != expected:
        raise ValueError('Release index must retain the approved native digests')


def docker_json(*arguments):
    return json.loads(subprocess.check_output(['docker', 'buildx', 'imagetools', *arguments], text=True))


def publish(directory, output, commit, ci_run, ci_attempt, run_id, run_attempt):
    positive(run_attempt)
    documents = {arch: json.loads((directory / arch / 'publication.json').read_text())
                 for arch in ARCHITECTURES}
    sources = validate_publications(documents, commit, ci_run, ci_attempt, run_id)
    plans = {}
    # Validate every source and dry-run every index before the first registry write.
    for image, references in sources.items():
        expected = {}
        for arch, reference in references.items():
            manifest = docker_json('inspect', reference, '--raw')
            config = docker_json('inspect', reference, '--format', '{{json .Image}}')
            expected[arch] = source_digest(manifest, config, arch, reference.split('@')[1])
        validate_index(docker_json('create', '--dry-run', *references.values()), expected)
        plans[image] = expected
    result = dict(commit=commit, architectures=list(ARCHITECTURES),
                  ciRun=f'{RUN_PREFIX}{ci_run}/attempts/{ci_attempt}',
                  publishRun=f'{RUN_PREFIX}{run_id}/attempts/{run_attempt}', images={})
    for image, references in sources.items():
        tag = f'ghcr.io/bipoxdxd/duora-{image}:{commit}-{run_id}-{run_attempt}'
        subprocess.run(['docker', 'buildx', 'imagetools', 'create', '--tag', tag,
                        *references.values()], check=True)
        manifest = docker_json('inspect', tag, '--format', '{{json .Manifest}}')
        validate_index(manifest, plans[image])
        digest = manifest.get('digest', '')
        if not re.fullmatch('sha256:[0-9a-f]{64}', digest):
            raise ValueError('Published index digest missing')
        result['images'][image] = dict(reference=tag, digest=tag.split(':')[0] + '@' + digest,
                                       native=sources[image])
    output.mkdir(parents=True, exist_ok=True)
    (output / 'publication.json').write_text(json.dumps(result, indent=2) + '\n')
    print(json.dumps(result, indent=2))


def main():
    if os.environ['GITHUB_REPOSITORY'] != REPOSITORY:
        raise ValueError('Only the Duora repository may publish these indices')
    run_id = positive(os.environ['GITHUB_RUN_ID'])
    if sys.argv[1:] == ['select']:
        response = json.loads(subprocess.check_output(
            ['gh', 'api', f'repos/{REPOSITORY}/actions/runs/{run_id}/artifacts?per_page=100'], text=True))
        with open(os.environ['GITHUB_OUTPUT'], 'a') as output:
            for arch, name in select_artifacts(response).items():
                output.write(f'{arch}={name}\n')
    elif sys.argv[1:] == ['publish']:
        publish(Path(os.environ['NATIVE_PUBLICATIONS']), Path(os.environ['RELEASE_INDEX_DIR']),
                os.environ['RELEASE_COMMIT'], os.environ['CI_RUN'], os.environ['CI_ATTEMPT'],
                run_id, os.environ['GITHUB_RUN_ATTEMPT'])
    else:
        raise ValueError('Expected select or publish')


if __name__ == '__main__':
    main()
