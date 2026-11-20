"""Completa o inventário de licenças para dependências empacotadas em tarballs npm."""

import argparse
import hashlib
import json
import os
import re
import stat
import sys
import tempfile
from collections import Counter, defaultdict
from pathlib import Path
from urllib.parse import quote, unquote


_NPM_NAME = re.compile(r"(?:@[a-z0-9._-]+/)?[a-z0-9][a-z0-9._-]*")


def npm_identity(component):
    purl = component.get("purl") or ""
    if component.get("type") != "library" or not purl.startswith("pkg:npm/"):
        return None
    return unquote(purl.partition("?")[0].partition("#")[0])


def _read_manifest(boundary, path, name, version, license, digest):
    label = f"{name}@{version}"
    try:
        resolved = path.resolve(strict=True)
        if not resolved.is_relative_to(boundary) or not resolved.is_file():
            raise ValueError(f"{label}: manifesto fora do diretório do pacote")
        raw = resolved.read_bytes()
    except OSError as error:
        raise ValueError(f"{label}: manifesto empacotado ausente ou ilegível") from error
    if hashlib.sha256(raw).hexdigest() != digest:
        raise ValueError(f"{label}: SHA-256 do manifesto diferente do tarball revisado")
    manifest = json.loads(raw)
    if (manifest.get("name"), manifest.get("version"), manifest.get("license")) != (name, version, license):
        raise ValueError(f"{label}: nome, versão ou licença difere do inventário revisado")
    return manifest


def read_bundled_components(frontend, inventory, sbom_components):
    name = inventory["package"]
    license = inventory["license"]
    if not _NPM_NAME.fullmatch(name) or not license:
        raise ValueError("origem do inventário npm inválida")
    prefix = f"pkg:npm/{name}@"
    matches = [(ref, row) for row in sbom_components if (ref := npm_identity(row)) and ref.startswith(prefix)]
    if len(matches) != 1:
        raise ValueError(f"{name}: esperado exatamente um componente npm de origem no SBOM")
    ref, component = matches[0]
    version = component["version"]
    label = f"{name}@{version}"
    versions = inventory["versions"]
    if ref != prefix + version or version not in versions or not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+", version):
        raise ValueError(f"{label}: origem npm sem versão revisada")
    licenses = component.get("licenses") or []
    if [entry.get("license", {}).get("id") for entry in licenses] != [license]:
        raise ValueError(f"{label}: licença {license} precisa estar explícita no SBOM")

    reviewed = versions[version]
    integrity = reviewed["integrity"]
    key = re.escape(label)
    lock = (frontend / "pnpm-lock.yaml").read_text(encoding="utf-8")
    observed = re.findall(rf"^  (?:{key}|'{key}'):\n    resolution: \{{integrity: ([^}}]+)\}}$", lock, re.MULTILINE)
    if observed != [integrity]:
        raise ValueError(f"{label}: integridade do tarball difere de pnpm-lock.yaml")

    store = (frontend / "node_modules/.pnpm").resolve()
    package = store / f"{name.replace('/', '+')}@{version}" / "node_modules" / name
    parent = _read_manifest(store, package / "package.json", name, version, license, reviewed["manifestSha256"])
    expected = inventory["dependencies"]
    declared = parent.get("bundledDependencies", parent.get("bundleDependencies"))
    if not isinstance(declared, list) or sorted(declared) != sorted(expected):
        raise ValueError(f"{label}: dependências empacotadas diferem do inventário revisado")

    components = []
    for dependency, metadata in sorted(expected.items()):
        if not _NPM_NAME.fullmatch(dependency) or not metadata["license"]:
            raise ValueError(f"{label}: dependência empacotada inválida: {dependency}")
        path = package / "node_modules" / dependency / "package.json"
        version = metadata["version"]
        license = metadata["license"]
        _read_manifest(package, path, dependency, version, license, metadata["manifestSha256"])
        group, _, bare_name = dependency.rpartition("/")
        item = {
            "type": "library",
            "name": bare_name,
            "version": version,
            "purl": f"pkg:npm/{quote(dependency, safe='/')}@{version}",
            "licenses": [{"license": {"id": license}}],
        }
        if group:
            item["group"] = group
        components.append(item)
    return label, integrity, component.get("bom-ref"), components


def _bom_refs(value):
    if isinstance(value, dict):
        if isinstance(value.get("bom-ref"), str):
            yield value["bom-ref"]
        for item in value.values():
            yield from _bom_refs(item)
    elif isinstance(value, list):
        for item in value:
            yield from _bom_refs(item)


def _spdx_ids(component):
    return [entry.get("license", {}).get("id") for entry in component.get("licenses") or []]


def complete_inventory(document, frontend, inventory, *, check):
    if document.get("bomFormat") != "CycloneDX":
        raise ValueError("SBOM de entrada não é CycloneDX")
    components = document["components"]
    origin, integrity, parent, expected = read_bundled_components(frontend, inventory, components)
    refs = Counter(_bom_refs(document))
    if not parent or refs[parent] != 1:
        raise ValueError(f"{origin}: bom-ref de origem ausente ou ambíguo")

    by_identity = defaultdict(list)
    for row in components:
        identity = npm_identity(row)
        if identity:
            by_identity[identity].append(row)

    links = {}
    for entry in expected:
        identity = npm_identity(entry)
        name = identity.removeprefix("pkg:npm/")
        rows = by_identity[identity]
        if len(rows) > 1:
            raise ValueError(f"{name}: componente npm ambíguo no SBOM")
        if not rows:
            if check:
                raise ValueError(f"{name}: componente empacotado ausente no SBOM")
            ref = entry["purl"]
            if refs[ref]:
                raise ValueError(f"{name}: bom-ref já usado no SBOM")
            row = {
                **entry,
                "bom-ref": ref,
                "properties": [
                    {"name": "duora:npm:bundled-in", "value": parent},
                    {"name": "duora:npm:bundle-integrity", "value": integrity},
                ],
            }
            components.append(row)
            refs[ref] = 1
        else:
            row = rows[0]
        ref = row.get("bom-ref")
        if not ref or refs[ref] != 1:
            raise ValueError(f"{name}: bom-ref ausente ou ambíguo no SBOM")
        if any(row.get(key) != entry.get(key) for key in ("group", "name", "version")):
            raise ValueError(f"{name}: identidade npm divergente no SBOM")
        if _spdx_ids(row) != _spdx_ids(entry):
            raise ValueError(f"{name}: licença SPDX divergente no SBOM")
        links[name] = ref

    dependencies = document.setdefault("dependencies", [])
    origins = [entry for entry in dependencies if entry["ref"] == parent]
    if len(origins) > 1:
        raise ValueError(f"{origin}: origem repetida no grafo de dependências")
    if not origins:
        if check:
            raise ValueError(f"{origin}: origem ausente no grafo de dependências")
        origins = [{"ref": parent, "dependsOn": []}]
        dependencies.extend(origins)
    depends_on = origins[0].setdefault("dependsOn", [])
    for name, ref in links.items():
        if ref not in depends_on:
            if check:
                raise ValueError(f"{name}: vínculo com {origin} ausente no grafo do SBOM")
            depends_on.append(ref)
    return origin, integrity, expected


def _replace_json(path, document):
    with tempfile.TemporaryDirectory(dir=path.parent) as directory:
        temporary = Path(directory) / path.name
        with temporary.open("w", encoding="utf-8") as output:
            json.dump(document, output, indent=2, ensure_ascii=False)
            output.write("\n")
        temporary.chmod(stat.S_IMODE(path.stat().st_mode))
        os.replace(temporary, path)


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("mode", choices=("complete", "check"))
    parser.add_argument("frontend", type=Path)
    parser.add_argument("inventory", type=Path)
    parser.add_argument("sbom", type=Path)
    args = parser.parse_args(argv)
    try:
        inventory = json.loads(args.inventory.read_bytes())
        document = json.loads(args.sbom.read_bytes())
        origin, integrity, bundled = complete_inventory(document, args.frontend, inventory,
                                                          check=args.mode == "check")
        if args.mode == "complete":
            _replace_json(args.sbom, document)
    except (OSError, ValueError, TypeError, KeyError) as error:
        print(f"Inventário npm reprovado: {error}", file=sys.stderr)
        return 1
    print(f"### Dependências npm empacotadas em \x60{origin}\x60\n")
    print(f"Integridade do tarball: \x60{integrity}\x60\n")
    print("| Componente | Licença |\n|---|---|")
    for row in bundled:
        name = npm_identity(row).removeprefix("pkg:npm/")
        print(f"| \x60{name}\x60 | \x60{_spdx_ids(row)[0]}\x60 |")
    return 0


if __name__ == "__main__":
    sys.exit(main())
