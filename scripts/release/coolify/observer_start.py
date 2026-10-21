#!/usr/bin/python3
"""Host-only Coolify custom start. Install root:root 0755; no arguments."""
import hashlib
import ipaddress
import json
import os
import pathlib
import re
import stat
import subprocess
import sys
import tempfile

CONFIG = pathlib.Path('/etc/duora/coolify-deploy.json')
SAFE_ENV = {'PATH': '/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin',
            'HOME': '/root', 'LANG': 'C.UTF-8', 'LC_ALL': 'C.UTF-8'}
ENTRYPOINT = ['/bin/sh', '-c', 'exec sleep 2147483647']
HEALTHCHECK = {
    'test': ['CMD-SHELL',
             'wget -q -T 5 -O /dev/null http://duora-web:8080/health/ready && '
             "wget -q -T 5 --header='Host: meetduora.com' -O /dev/null http://duora-edge-1/"],
    'interval': '15s', 'timeout': '12s', 'start_period': '30s', 'retries': 3,
}
OBSERVER_ENV_KEYS = {'DUORA_OBSERVER_IMAGE', 'COOLIFY_BRANCH', 'COOLIFY_RESOURCE_UUID',
                     'COOLIFY_CONTAINER_NAME', 'SERVICE_NAME_DEPLOYMENT_OBSERVER',
                     'COOLIFY_URL', 'COOLIFY_FQDN', 'SOURCE_COMMIT', 'HOST'}
SERVICE_FIELDS = {'image', 'entrypoint', 'command', 'user', 'read_only', 'cap_drop',
                  'security_opt', 'restart', 'mem_limit', 'pids_limit', 'labels',
                  'networks', 'container_name', 'environment', 'healthcheck'}


def protected(path):
    if not path.is_absolute():
        raise ValueError(f'Absolute path required: {path}')
    # A root-owned leaf can still be replaced through a writable/symlinked parent.
    for item in (path, *path.parents):
        info = item.lstat()
        if stat.S_ISLNK(info.st_mode) or info.st_uid != 0 or info.st_mode & 0o022:
            raise ValueError(f'Path must be root-owned, without symlinks or writable parents: {item}')
    if not stat.S_ISREG(path.stat().st_mode):
        raise ValueError(f'Regular file required: {path}')
    return path.read_bytes()


def validate_observer(runtime, config, manifest):
    """Accept a sleeping, unprivileged observer only, including reviewed Coolify metadata."""
    app = config['application_uuid']
    if not isinstance(app, str) or not re.fullmatch(r'[a-z0-9][a-z0-9-]{0,63}', app):
        raise ValueError('Invalid application UUID')
    if set(runtime) - {'name', 'services', 'networks'} or runtime.get('name') != app:
        raise ValueError('Unexpected observer project or top-level resources')
    if set(runtime.get('services', {})) != {'deployment-observer'}:
        raise ValueError('Coolify resource must contain only deployment-observer')
    service = runtime['services']['deployment-observer']
    if set(service) - SERVICE_FIELDS:
        raise ValueError('Unexpected observer service fields')
    edge = manifest['images']['edge']
    if not isinstance(edge, str) or not re.fullmatch(r'[a-z0-9][a-z0-9._/-]*@sha256:[0-9a-f]{64}', edge):
        raise ValueError('Observer requires an immutable release edge digest')
    expected = {'image': edge, 'entrypoint': ENTRYPOINT, 'user': '10001:10001',
                'read_only': True, 'cap_drop': ['ALL'],
                'security_opt': ['no-new-privileges:true'], 'restart': 'unless-stopped',
                'pids_limit': 16}
    for key, value in expected.items():
        if service.get(key) != value:
            raise ValueError(f'Unexpected observer {key}')
    if str(service.get('mem_limit')) != '16777216':
        raise ValueError('Observer requires the reviewed memory limit')
    if service.get('command') is not None:
        raise ValueError('Observer cannot receive commands')
    if (config.get('observer_healthcheck') != HEALTHCHECK
            or service.get('healthcheck') != HEALTHCHECK):
        raise ValueError('Observer requires the reviewed backend and edge health probe')
    environment = config.get('observer_environment', {})
    if not isinstance(environment, dict) or set(environment) - OBSERVER_ENV_KEYS:
        raise ValueError('Unexpected observer environment keys')
    if any(not isinstance(value, str) or '$' in value or any(ord(char) < 32 for char in value)
           for value in environment.values()):
        raise ValueError('Observer environment must contain reviewed literal strings')
    if (service.get('environment') or {}) != environment:
        raise ValueError('Observer environment differs from reviewed metadata')
    expected_metadata = {'DUORA_OBSERVER_IMAGE': edge, 'COOLIFY_RESOURCE_UUID': app,
                         'COOLIFY_CONTAINER_NAME': config.get('observer_container_name'),
                         'SERVICE_NAME_DEPLOYMENT_OBSERVER': 'deployment-observer',
                         'COOLIFY_URL': '', 'COOLIFY_FQDN': '', 'HOST': '0.0.0.0'}
    for key, value in expected_metadata.items():
        if key in environment and environment[key] != value:
            raise ValueError(f'Unexpected observer metadata {key}')
    if 'SOURCE_COMMIT' in environment and not re.fullmatch(r'[0-9a-f]{40}', environment['SOURCE_COMMIT']):
        raise ValueError('Observer source commit must be a full SHA')
    # Coolify metadata is allowed only after the actual rendered values are reviewed
    # and pinned in the root-owned configuration; never accept arbitrary prefixes.
    labels = config.get('observer_labels', {'traefik.enable': 'false'})
    if labels.get('traefik.enable') != 'false' or service.get('labels') != labels:
        raise ValueError('Observer labels differ from reviewed metadata')
    if any(not isinstance(k, str) or not isinstance(v, str) or '$' in k + v
           for k, v in labels.items()):
        raise ValueError('Observer labels must be literal strings')
    if any(k != 'traefik.enable' and not k.startswith('coolify.') for k in labels):
        raise ValueError('Only disabled routing and reviewed Coolify metadata are allowed')
    container_name = config.get('observer_container_name')
    if service.get('container_name') != container_name:
        raise ValueError('Unexpected observer container name')
    if container_name is not None and not re.fullmatch(r'[a-zA-Z0-9][a-zA-Z0-9_.-]*', container_name):
        raise ValueError('Invalid observer container name')
    networks = config.get('observer_networks', {'default': {'name': app + '_default', 'ipam': {}}})
    if runtime.get('networks') != networks:
        raise ValueError('Observer networks differ from reviewed metadata')
    if networks.get('duora_edge') != {'name': 'duora_edge', 'external': True, 'ipam': {}}:
        raise ValueError('Health observer requires the existing private Duora edge network')
    for key, network in networks.items():
        if key == 'duora_edge':
            continue
        if key not in {'default', app, 'coolify'} or set(network) - {'name', 'external', 'ipam'}:
            raise ValueError('Unexpected observer network configuration')
        if network.get('name') not in {app, app + '_default', 'coolify'} or network.get('ipam', {}) != {}:
            raise ValueError('Observer must use an isolated application or reviewed Coolify network')
        if 'external' in network and network['external'] is not True:
            raise ValueError('Unexpected external network setting')
    if service.get('networks') != {key: None for key in networks}:
        raise ValueError('Observer cannot configure network aliases, addresses or priorities')
    return runtime


def normalize_observer(observer, appdir, app, edge):
    # Read once; no auto .env, ambient DOCKER_HOST/COMPOSE_* or inherited credentials.
    # Only the already-pinned release edge digest may interpolate the image. Coolify's
    # injected env_file resolves into service metadata, validated exactly afterward.
    # It never supplies host process credentials or the execution environment.
    if not isinstance(edge, str) or not re.fullmatch(r'[a-z0-9][a-z0-9._/-]*@sha256:[0-9a-f]{64}', edge):
        raise ValueError('Normalization requires immutable release edge digest')
    source = observer.read_bytes()
    output = subprocess.check_output(
        ['/usr/bin/docker', 'compose', '--project-name', app, '--project-directory', str(appdir),
         '--env-file', '/dev/null', '-f', '-', 'config', '--format', 'json'],
        input=source, env={**SAFE_ENV, 'DUORA_OBSERVER_IMAGE': edge})
    return json.loads(output)


def deploy_and_start(runtime, config, manifest):
    # Private root directory + fsynced 0600 snapshot: no reread of mutable Coolify YAML
    # after validation or after the potentially long runtime deployment.
    with tempfile.TemporaryDirectory(prefix='duora-observer-', dir='/run') as temporary:
        frozen = pathlib.Path(temporary) / 'compose.json'
        with frozen.open('x', encoding='utf-8') as handle:
            os.chmod(frozen, 0o600)
            json.dump(runtime, handle, sort_keys=True)
            handle.flush()
            os.fsync(handle.fileno())
        subprocess.run(['/usr/local/sbin/duora-deploy', '--manifest', str(manifest),
                        '--compose-file', config['compose']], env=SAFE_ENV, check=True)
        subprocess.run(['/usr/bin/docker', 'compose', '--env-file', '/dev/null', '-f', str(frozen),
                        '--project-name', config['application_uuid'], 'up', '-d', '--no-build',
                        '--wait', '--wait-timeout', '90',
                        'deployment-observer'], env=SAFE_ENV, check=True)


def main():
    if len(sys.argv) != 1 or os.geteuid() != 0:
        raise ValueError('Host root execution without arguments required')
    config = json.loads(protected(CONFIG))
    app = config['application_uuid']
    if not isinstance(app, str) or not re.fullmatch(r'[a-z0-9][a-z0-9-]{0,63}', app):
        raise ValueError('Invalid application UUID')
    appdir = pathlib.Path('/data/coolify/applications') / app
    if pathlib.Path.cwd().resolve() != appdir.resolve():
        raise ValueError('Unexpected Coolify application working directory')
    manifest = pathlib.Path(config['manifest'])
    manifest_bytes = protected(manifest)
    if hashlib.sha256(manifest_bytes).hexdigest() != config['manifest_sha256']:
        raise ValueError('Release manifest checksum changed')
    for key in ('compose', 'caddy'):
        if hashlib.sha256(protected(pathlib.Path(config[key]))).hexdigest() != config[key + '_sha256']:
            raise ValueError(f'{key} checksum changed')
    proxy = json.loads(subprocess.check_output(
        ['/usr/bin/docker', 'inspect', 'coolify-proxy'], text=True, env=SAFE_ENV))[0]
    actual = proxy['NetworkSettings']['Networks']['coolify']['IPAddress']
    if ipaddress.ip_address(actual) != ipaddress.ip_address(config['proxy_ip']):
        raise ValueError('Proxy IP changed; regenerate and review Caddy trusted proxy before deploy')
    observer = appdir / 'docker-compose.yaml'
    if not observer.exists():
        observer = appdir / 'docker-compose.yml'
    runtime = normalize_observer(observer, appdir, app, json.loads(manifest_bytes)['images']['edge'])
    validate_observer(runtime, config, json.loads(manifest_bytes))
    deploy_and_start(runtime, config, manifest)


if __name__ == '__main__':
    try:
        main()
    except (ValueError, KeyError, TypeError, OSError, subprocess.CalledProcessError) as exc:
        print(f'Duora deployment refused: {exc}', file=sys.stderr)
        sys.exit(1)
