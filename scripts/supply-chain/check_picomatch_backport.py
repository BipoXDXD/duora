"""Verify the installed Duora backport before declaring it fixed to Trivy."""

import argparse
from collections import Counter, defaultdict
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


def installed_packages(root, consumers):
    """Inspect only the dependency installation used by the frontend."""
    modules = root / 'frontend/node_modules'
    require(modules.is_dir() and modules.resolve(strict=True) == modules,
            f'expected a physical frontend dependency directory: {modules}')
    copies = set()
    installed_consumers = defaultdict(set)
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
            identity = (metadata.get('name'), metadata.get('version'))
            if identity in consumers:
                installed_consumers[identity].add(candidate)
    return copies, installed_consumers


def verify_consumer_bindings(store, expected, installed, package):
    locations = []
    for identity, count in sorted(expected.items()):
        name, version = identity
        candidates = installed[identity]
        # Peer variants can have the same name and version but distinct installed locations.
        require(len(candidates) == count,
                f'unexpected installed consumer count for {name}@{version}: '
                f'expected {count}, found {len(candidates)}')
        for consumer in sorted(candidates):
            require(consumer.is_relative_to(store), f'consumer outside the pnpm virtual store: {consumer}')
            parts = consumer.relative_to(store).parts
            require(parts[1:] == ('node_modules', *name.split('/')),
                    f'unexpected installed consumer path: {consumer}')
            edge = store / parts[0] / 'node_modules' / PACKAGE
            require(edge.is_symlink(), f'missing pnpm Picomatch dependency edge for {name}@{version}')
            require(edge.resolve(strict=True) == package,
                    f'Picomatch dependency edge does not reach the verified package: {edge}')
            locations.append(consumer)

    # Resolve the public CommonJS entry without loading any dependency code.
    resolve = '''
const { createRequire } = require('node:module');
const { realpathSync } = require('node:fs');
const { join, sep } = require('node:path');
const [name, expectedPackage, ...consumers] = process.argv.slice(1);
for (const consumer of consumers) {
  const resolved = createRequire(join(consumer, 'package.json')).resolve(name);
  if (!realpathSync(resolved).startsWith(expectedPackage + sep)) {
    throw new Error(`${consumer} resolves ${name} outside the verified Picomatch package: ${resolved}`);
  }
}
'''
    check = subprocess.run(['node', '-e', resolve, PACKAGE, str(package), *map(str, locations)],
                           cwd=store, capture_output=True, text=True, timeout=30)
    require(check.returncode == 0, f'Picomatch consumer resolution failed: {check.stderr.strip()}')


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
    # The verifier proves dependencies between installed packages. A workspace importer
    # would also need its own resolution check, including aliases to Picomatch.
    importer_edge = r"^(?:      (?:picomatch|'picomatch'):|        specifier: '?npm:picomatch(?:@|'?$))"
    require(not re.search(importer_edge, mapping['importers'], re.MULTILINE),
            'direct Picomatch importer dependency requires explicit verification')
    return snapshot_consumers(mapping['snapshots'], f'{VERSION}(patch_hash={patch_hash})')


def snapshot_consumers(snapshots, dependency):
    # Limit parsing to the indented package snapshots generated by the pinned pnpm writer.
    entries = re.split(r'^  ([^\s].*?):(?: \{\})?[ \t]*$', snapshots, flags=re.MULTILINE)
    consumers = Counter()
    for key, contents in zip(entries[1::2], entries[2::2], strict=True):
        blocks = re.findall(r'^    (?:dependencies|optionalDependencies):\n((?:^      [^\n]+\n?)*)',
                            contents, flags=re.MULTILINE)
        edges = [edge for block in blocks
                 for edge in re.findall(r"^      (?:picomatch|'picomatch'): (.+)$", block, re.MULTILINE)]
        if edges:
            require(edges == [dependency], f'unexpected Picomatch dependency in snapshot: {key}')
            identity = tuple(key.strip("'").split('(', 1)[0].rsplit('@', 1))
            require(len(identity) == 2 and all(identity), f'invalid pnpm consumer snapshot: {key}')
            consumers[identity] += 1
    require(consumers, 'no Picomatch consumers in the approved pnpm lock')
    return consumers


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
    consumers = check_lock(paths['lockSha256'].read_text(encoding='utf-8'), manifest['patchSha256'])
    workspace = paths['workspaceSha256'].read_text(encoding='utf-8')
    require(config_entry(workspace, f'{PACKAGE}@{VERSION}', PATCH),
            'workspace does not bind the expected patch')
    expected_files = manifest['files']
    require(isinstance(expected_files, dict) and {'package.json', 'LICENSE', 'lib/parse.js'} <= expected_files.keys(),
            'incomplete package file manifest')
    copies, installed_consumers = installed_packages(root, consumers)
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
    verify_consumer_bindings(store, consumers, installed_consumers, package)
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


def result_key(result):
    return (result['Target'], result.get('Class'), result.get('Type'))


def finding_key(result, category, finding):
    return (*result_key(result), category, json.dumps(finding, sort_keys=True))


def vulnerability_key(result, finding):
    # Trivy v0.75.0 pkg/result/filter.go deduplicates by these fields within each result.
    return (*result_key(result), *(finding.get(field, '') for field in (
        'VulnerabilityID', 'PkgID', 'PkgName', 'InstalledVersion', 'PkgPath',
    )))


def active_findings(report):
    return Counter(
        finding_key(result, category, finding)
        for result in results(report)
        for category, field in [('vulnerability', 'Vulnerabilities'),
                                ('misconfiguration', 'Misconfigurations')]
        for finding in result.get(field, [])
    )


def reconcile_ignored_vulnerabilities(raw, ignored, accounted):
    raw_groups = defaultdict(list)
    for result in results(raw):
        for finding in result.get('Vulnerabilities', []):
            raw_groups[vulnerability_key(result, finding)].append(finding)
    for key, group in ignored.items():
        if len(group) == 1:
            winner = group[0]
        else:
            # Ignore-file matches are recorded before Trivy deduplicates active vulnerabilities.
            # The raw scan keeps a single finding with the greatest FixedVersion string.
            raw_group = raw_groups[key]
            group_json = {json.dumps(finding, sort_keys=True) for finding in group}
            require(len(raw_group) == 1 and json.dumps(raw_group[0], sort_keys=True) in group_json
                    and raw_group[0].get('FixedVersion', '') == max(f.get('FixedVersion', '') for f in group),
                    'a raw finding is missing or changed in the policy report')
            winner = raw_group[0]
        accounted[(*key[:3], 'vulnerability', json.dumps(winner, sort_keys=True))] += 1


def audit(raw_report, policy_report):
    raw = read_json(raw_report)
    policy = read_json(policy_report)
    require(raw.get('ArtifactName') == policy.get('ArtifactName'), 'Trivy target changed between scans')
    require(raw.get('ArtifactType') == policy.get('ArtifactType'), 'Trivy artifact type changed between scans')
    require(raw.get('ArtifactID') == policy.get('ArtifactID'), 'Trivy artifact identity changed between scans')
    expected = expected_findings(raw)
    accounted = active_findings(policy)
    ignored_vulnerabilities = defaultdict(list)
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
                if modified['Type'] == 'vulnerability':
                    ignored_vulnerabilities[vulnerability_key(result, finding)].append(finding)
                    continue
                if modified['Type'] == 'misconfiguration':
                    status = finding.get('Status')
                    require(status in {'FAIL', 'PASS', 'EXCEPTION'}, 'unexpected ignored misconfiguration status')
                    # The ignore file is applied before Trivy omits known non-failures.
                    if status in {'PASS', 'EXCEPTION'}:
                        continue
            accounted[finding_key(result, modified['Type'], finding)] += 1
    reconcile_ignored_vulnerabilities(raw, ignored_vulnerabilities, accounted)
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
