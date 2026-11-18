"""Verify the installed Duora backport before declaring it fixed to Trivy."""

import argparse
from collections import Counter
import datetime
import hashlib
import json
import os
import re
from pathlib import Path, PurePosixPath
import stat
import subprocess
import sys

PACKAGE = 'picomatch'
VERSION = '4.0.7'
PURL = f'pkg:npm/{PACKAGE}@{VERSION}'
ADVISORY_IDS = {'CVE-2026-112366', 'GHSA-fc5q-39xc-vjpf'}
TARGET = 'frontend/pnpm-lock.yaml'
REGRESSION = 'frontend/security/backports/picomatch-regression.cjs'
MANIFEST = 'frontend/security/backports/picomatch.json'
PATCH = f'patches/{PACKAGE}@{VERSION}.patch'
UPSTREAM = 'https://registry.npmjs.org/picomatch/-/picomatch-4.0.7.tgz'
SRI = ('sha512-qcJu88Q2IWqJsDD529JKMdwGm/dvInW4HvQnRwiH9JtihJvzGOscDtHE3x1pBKe'
       'UOTysQ8kVmLnJ2kJu7yhcGA==')
VEX_SOURCE = '/reports/picomatch.openvex.json'


def require(condition, message):
    if not condition:
        raise ValueError(message)


def unique_keys(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, f'duplicate JSON key: {key}')
        result[key] = value
    return result


def read_json(path):
    return json.loads(path.read_text(encoding='utf-8'), object_pairs_hook=unique_keys)


def write_json(path, payload):
    with path.open('x', encoding='utf-8') as stream:
        json.dump(payload, stream, indent=2, sort_keys=True)
        stream.write('\n')


def check_regular(path):
    require(path.resolve(strict=True) == path and stat.S_ISREG(path.lstat().st_mode),
            f'expected a regular file without symlinks: {path}')


def sha256(path):
    check_regular(path)
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def fail_walk(error):
    raise error


def package_tree_sha256(package):
    files = {}
    for directory, subdirs, names in os.walk(package, onerror=fail_walk):
        here = Path(directory)
        for subdir in subdirs:
            require(not (here / subdir).is_symlink(), f'symlink in package: {here / subdir}')
        for name in names:
            path = here / name
            files[path.relative_to(package).as_posix()] = sha256(path)
    return files


def installed_copies(root):
    """Inspect only the dependency installation used by the frontend."""
    modules = root / 'frontend/node_modules'
    require(modules.is_dir() and modules.resolve(strict=True) == modules,
            f'expected a physical frontend dependency directory: {modules}')
    copies = set()
    for directory, subdirs, names in os.walk(modules, onerror=fail_walk):
        here = Path(directory)
        raw_candidates = [here] if 'package.json' in names or here.name == PACKAGE else []
        raw_candidates.extend(here / name for name in subdirs if (here / name).is_symlink())
        if PACKAGE in names:
            raw_candidates.append(here / PACKAGE)
        for entry in raw_candidates:
            require(not (entry.name == 'node_modules' and entry.is_symlink()),
                    f'untracked node_modules link: {entry}')
            require(entry.is_dir(), f'expected a dependency directory: {entry}')
            candidate = entry.resolve(strict=True)
            require(candidate.is_relative_to(modules), f'package link escapes frontend node_modules: {entry}')
            manifest = candidate / 'package.json'
            if entry.name != PACKAGE and not manifest.is_file():
                continue
            check_regular(manifest)
            metadata = read_json(manifest)
            if entry.name == PACKAGE or metadata.get('name') == PACKAGE:
                require(metadata.get('name') == PACKAGE and metadata.get('version') == VERSION,
                        f'unexpected Picomatch identity: {entry}')
                copies.add(candidate)
    return copies


def config_entry(text, key, value):
    key = re.escape(key)
    return re.search(rf"^  (?:{key}|'{key}'): {re.escape(value)}$", text, re.MULTILINE)


def verify_committed_manifest(root, committed_hash):
    require(sha256(root / MANIFEST) == committed_hash, 'backport manifest differs from the checked-out commit')


def check_lock(lock, patch_hash):
    documents = [doc for doc in re.split(r'^---[ \t]*$', lock, flags=re.MULTILINE) if doc.strip()]
    require(len(documents) == 2 and '    packageManagerDependencies:' in documents[0],
            'expected pnpm 12 env and project lock documents')
    project = documents[1]
    require(project.lstrip().startswith("lockfileVersion: '9.0'\n"),
            'unexpected pnpm project lock version')
    sections = re.split(r'^([A-Za-z]+):[ \t]*$', project, flags=re.MULTILINE)
    mapping = dict(zip(sections[1::2], sections[2::2], strict=True))
    key = f'{PACKAGE}@{VERSION}'
    require(config_entry(mapping['patchedDependencies'], key, patch_hash),
            'missing approved pnpm patch hash')
    require(f'\n  {key}:\n    resolution: {{integrity: {SRI}}}\n' in mapping['packages'] or
            f"\n  '{key}':\n    resolution: {{integrity: {SRI}}}\n" in mapping['packages'],
            'upstream pnpm integrity mismatch')
    snapshot = re.escape(f'{key}(patch_hash={patch_hash})')
    require(re.search(rf"^  '?{snapshot}(?:\(|'?:)", mapping['snapshots'], re.MULTILINE),
            'missing patched snapshot')


def verify(root):
    manifest_path = root / MANIFEST
    check_regular(manifest_path)
    manifest = read_json(manifest_path)
    for key, value in {'schema': 1, 'package': PACKAGE, 'version': VERSION, 'purl': PURL,
                       'patch': PATCH, 'upstreamSource': UPSTREAM, 'upstreamIntegrity': SRI}.items():
        require(manifest.get(key) == value, f'unexpected manifest value: {key}')
    require(set(manifest['vulnerabilityIds']) == ADVISORY_IDS, 'unexpected advisory identity')
    paths = {
        'patchSha256': root / 'frontend' / PATCH,
        'lockSha256': root / TARGET,
        'workspaceSha256': root / 'frontend/pnpm-workspace.yaml',
        'regressionSha256': root / REGRESSION,
    }
    for field, path in paths.items():
        require(sha256(path) == manifest[field], f'{field} mismatch: {path}')
    patch = paths['patchSha256'].read_bytes()
    require(b'\r' not in patch, 'reviewed patch must use LF, as hashed by pnpm')
    check_lock(paths['lockSha256'].read_text(encoding='utf-8'), manifest['patchSha256'])
    workspace = paths['workspaceSha256'].read_text(encoding='utf-8')
    require(config_entry(workspace, f'{PACKAGE}@{VERSION}', PATCH),
            'workspace does not bind the expected patch')
    expected_files = manifest['files']
    require(isinstance(expected_files, dict) and {'package.json', 'LICENSE', 'lib/parse.js'} <= expected_files.keys(),
            'incomplete package file manifest')
    copies = installed_copies(root)
    require(len(copies) == 1, f'expected one physical Picomatch copy, found {len(copies)}')
    package = next(iter(copies))
    store = root / 'frontend/node_modules/.pnpm'
    require(package.is_relative_to(store) and package.parent.name == 'node_modules',
            f'Picomatch is outside the tracked pnpm virtual store: {package}')
    actual_files = package_tree_sha256(package)
    require(actual_files.keys() == expected_files.keys(),
            f'unknown or absent package file: {sorted(actual_files.keys() ^ expected_files.keys())}')
    for file, digest in actual_files.items():
        require(digest == expected_files[file], f'installed package digest mismatch: {file}')
    return manifest, package


def results(report):
    require(report.get('SchemaVersion') == 2 and report.get('ArtifactType') in {'filesystem', 'repository'}
            and report.get('Trivy', {}).get('Version') == '0.75.0', 'unexpected Trivy report identity')
    scan_results = report.get('Results') or []
    packages = [(result, package) for result in scan_results
                for package in result.get('Packages', []) if package.get('Name') == PACKAGE]
    require(len(packages) == 1, 'Trivy must inventory exactly one Picomatch package')
    result, package = packages[0]
    require(PurePosixPath(result['Target']).as_posix() == TARGET and result.get('Type') == 'pnpm'
            and package.get('Version') == VERSION and package.get('Identifier', {}).get('PURL') == PURL,
            'Trivy inventory contains an unexpected Picomatch package')
    return scan_results


def advisory_scope(result, finding):
    identifier = finding.get('PkgIdentifier') or {}
    return (PurePosixPath(result['Target']).as_posix() == TARGET and result.get('Type') == 'pnpm'
            and finding.get('PkgName') == PACKAGE and finding.get('InstalledVersion') == VERSION
            and identifier.get('PURL') == PURL and finding.get('VulnerabilityID') in ADVISORY_IDS)


def expected_findings(report):
    findings = []
    for result in results(report):
        for finding in result.get('Vulnerabilities', []):
            if finding['VulnerabilityID'] in ADVISORY_IDS and finding.get('PkgName') == PACKAGE:
                require(advisory_scope(result, finding),
                        f'Picomatch advisory outside permitted scope: {result["Target"]}')
                findings.append(finding['VulnerabilityID'])
    return sorted(findings)


def emit(root, raw_report, vex_path, proof_path):
    raw_digest = sha256(raw_report)
    seen_ids = expected_findings(read_json(raw_report))
    manifest, package = verify(root)
    subprocess.run(['node', str(root / REGRESSION), str(package)], cwd=root, check=True, timeout=30)
    require(verify(root) == (manifest, package), 'backport changed during regression')
    require(sha256(raw_report) == raw_digest, 'raw scan report changed during regression')
    evidence = verified_evidence(root, manifest, package, raw_digest, seen_ids)
    commit = evidence['commit']
    if seen_ids:
        write_json(vex_path, {
            '@context': 'https://openvex.dev/ns/v0.2.0',
            '@id': f'urn:duora:backport:picomatch:{commit}:{evidence["manifestSha256"]}',
            'author': 'Duora CI',
            'timestamp': datetime.datetime.now(datetime.UTC).isoformat(timespec='seconds'),
            'version': 1,
            'statements': [{'vulnerability': {'name': advisory}, 'products': [{'@id': PURL}],
                            'status': 'fixed',
                            'action_statement': f'Backport verified in this CI run at commit {commit}.'}
                           for advisory in sorted(set(seen_ids))],
        })
    evidence['vexSha256'] = vex_digest(vex_path, seen_ids)
    write_json(proof_path, evidence)


def vex_digest(vex_path, seen_ids):
    if seen_ids:
        return sha256(vex_path)
    require(not vex_path.exists() and not vex_path.is_symlink(), 'unexpected VEX document')
    return None


def verified_evidence(root, manifest, package, raw_digest, seen_ids):
    return {
        'commit': subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=root, text=True).strip(),
        'manifestSha256': sha256(root / MANIFEST),
        'lockSha256': manifest['lockSha256'],
        'patchSha256': manifest['patchSha256'],
        'regressionSha256': manifest['regressionSha256'],
        'regressionResult': 'passed',
        'installedPackage': package.relative_to(root).as_posix(),
        'trivyRawReportSha256': raw_digest,
        'vulnerabilityIds': sorted(set(seen_ids)),
    }


def check_evidence(root, raw_report, vex_path, proof_path):
    manifest, package = verify(root)
    seen_ids = expected_findings(read_json(raw_report))
    current = verified_evidence(root, manifest, package, sha256(raw_report), seen_ids)
    current['vexSha256'] = vex_digest(vex_path, seen_ids)
    require(read_json(proof_path) == current, 'backport evidence no longer matches the workspace')


def finding_key(result, category, finding):
    return (result['Target'], result.get('Class'), result.get('Type'), category,
            json.dumps(finding, sort_keys=True))


def active_findings(report):
    return Counter(
        finding_key(result, category, finding)
        for result in results(report)
        for category, field in [('vulnerability', 'Vulnerabilities'),
                                ('misconfiguration', 'Misconfigurations')]
        for finding in result.get(field, [])
    )


def audit(raw_report, policy_report):
    raw = read_json(raw_report)
    policy = read_json(policy_report)
    require(raw.get('ArtifactName') == policy.get('ArtifactName'), 'Trivy target changed between scans')
    require(raw.get('ArtifactType') == policy.get('ArtifactType'), 'Trivy artifact type changed between scans')
    require(raw.get('ArtifactID') == policy.get('ArtifactID'), 'Trivy artifact identity changed between scans')
    expected = expected_findings(raw)
    accounted = active_findings(policy)
    actual = []
    for result in results(policy):
        for modified in result.get('ExperimentalModifiedFindings', []):
            finding = modified['Finding']
            if modified['Source'] == VEX_SOURCE:
                require(modified['Type'] == 'vulnerability' and modified['Status'] == 'fixed'
                        and advisory_scope(result, finding), 'unexpected VEX suppression')
                actual.append(finding['VulnerabilityID'])
            else:
                require(modified['Source'] == '.trivyignore' and modified['Status'] == 'ignored',
                        'unexpected source of a Trivy suppression')
                require(finding.get('VulnerabilityID') not in ADVISORY_IDS,
                        'backport advisory was suppressed outside the verified VEX')
            accounted[finding_key(result, modified['Type'], finding)] += 1
    require(sorted(actual) == expected, 'VEX audit differs from the raw advisory findings')
    require(accounted == active_findings(raw), 'a raw finding is missing or changed in the policy report')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest='command', required=True)
    prepare = commands.add_parser('emit')
    prepare.add_argument('root', type=Path)
    prepare.add_argument('raw', type=Path)
    prepare.add_argument('vex', type=Path)
    prepare.add_argument('proof', type=Path)
    for name in ['guard', 'audit']:
        check = commands.add_parser(name)
        check.add_argument('root', type=Path)
        check.add_argument('raw', type=Path)
        check.add_argument('vex', type=Path)
        check.add_argument('proof', type=Path)
        if name == 'audit':
            check.add_argument('policy', type=Path)
    args = parser.parse_args()
    root = args.root.resolve()
    checked_out_manifest = subprocess.check_output(['git', 'show', f'HEAD:{MANIFEST}'], cwd=root)
    verify_committed_manifest(root, hashlib.sha256(checked_out_manifest).hexdigest())
    if args.command == 'emit':
        emit(root, args.raw.resolve(), args.vex, args.proof)
    else:
        check_evidence(root, args.raw.resolve(), args.vex, args.proof)
        if args.command == 'audit':
            audit(args.raw, args.policy)


if __name__ == '__main__':
    try:
        main()
    except (OSError, ValueError, KeyError, TypeError, AttributeError, subprocess.SubprocessError) as error:
        sys.exit(f'Picomatch backport verification failed: {error}')
