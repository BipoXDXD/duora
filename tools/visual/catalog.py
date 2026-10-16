"""Catálogo de objetos (catalog.json) e papéis de cor resolvidos nos tokens de frontend/src/index.css."""

from __future__ import annotations

import json
import re
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Mapping

from palette import Oklch, read_tokens

TOOLS_DIR = Path(__file__).resolve().parent
REPO_ROOT = TOOLS_DIR.parents[1]
CATALOG_PATH = TOOLS_DIR / "catalog.json"
STYLESHEET_PATH = REPO_ROOT / "frontend" / "src" / "index.css"

_NAME = re.compile(r"^[a-z][a-z0-9]*(-[a-z0-9]+)*$")
_SOURCES = ("generated", "procedural")


class CatalogError(ValueError):
    pass


@dataclass(frozen=True)
class CatalogObject:
    name: str
    source: str
    use: str
    where: str
    prompt: str | None


@dataclass(frozen=True)
class Catalog:
    prompt_prefix: str
    palette: Mapping[str, Any]
    hue_check: Mapping[str, float]
    process: Mapping[str, Any]
    uses: Mapping[str, int]
    objects: tuple[CatalogObject, ...]

    def find(self, name: str) -> CatalogObject:
        for item in self.objects:
            if item.name == name:
                return item
        raise CatalogError(f"objeto '{name}' não está em {CATALOG_PATH.name}")

    def css_px(self, name: str) -> int:
        return self.uses[self.find(name).use]


def load_catalog(data: Mapping[str, Any] | None = None) -> Catalog:
    raw = data if data is not None else json.loads(CATALOG_PATH.read_text(encoding="utf-8"))
    uses = {use: int(spec["cssPx"]) for use, spec in raw["uses"].items()}
    objects = tuple(_parse_object(item, uses) for item in raw["objects"])
    names = [item.name for item in objects]
    duplicates = sorted({name for name in names if names.count(name) > 1})
    if duplicates:
        raise CatalogError(f"nome duplicado no catálogo: {', '.join(duplicates)}")
    return Catalog(
        prompt_prefix=raw["promptPrefix"],
        palette=raw["palette"],
        hue_check=raw["hueCheck"],
        process=raw["process"],
        uses=uses,
        objects=objects,
    )


def _parse_object(item: Mapping[str, Any], uses: Mapping[str, int]) -> CatalogObject:
    name = item["name"]
    if not _NAME.match(name):
        raise CatalogError(f"nome '{name}' precisa ser kebab-case minúsculo")
    if item["use"] not in uses:
        raise CatalogError(f"'{name}' usa '{item['use']}', que não está em uses")
    if item["source"] not in _SOURCES:
        raise CatalogError(f"'{name}' tem source '{item['source']}'; use {' ou '.join(_SOURCES)}")
    if item["source"] == "generated" and not item.get("prompt"):
        raise CatalogError(f"'{name}' é gerado e não tem prompt")
    return CatalogObject(name, item["source"], item["use"], item["where"], item.get("prompt"))


def resolve_roles(catalog: Catalog, tokens: Mapping[str, Oklch] | None) -> dict[str, Oklch]:
    """Cor-base de cada papel: matiz sempre do token; croma do token ou do override do catálogo."""
    if tokens is None:
        tokens = read_tokens(STYLESHEET_PATH.read_text(encoding="utf-8"))
    roles = {}
    for role, spec in catalog.palette["roles"].items():
        token = tokens.get(spec["token"])
        if token is None:
            raise CatalogError(f"papel '{role}' aponta para --color-{spec['token']}, ausente em index.css")
        roles[role] = token._replace(c=float(spec.get("chroma", token.c)))
    return roles
