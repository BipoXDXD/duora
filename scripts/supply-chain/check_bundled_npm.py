"""Completa o inventário de licenças para dependências empacotadas em tarballs npm."""

import hashlib
import json
import re
from urllib.parse import unquote


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
            "purl": f"pkg:npm/{dependency}@{version}",
            "licenses": [{"license": {"id": license}}],
        }
        if group:
            item["group"] = group
        components.append(item)
    return label, integrity, components
