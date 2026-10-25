"""Build the embedded legal catalog from the chosen versioned Markdown sources; no network."""
import argparse
import json
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / "docs/legal"
OUTPUT = ROOT / "backend/src/Duora.Infrastructure/Identity/LegalDocuments/catalog.json"
FIELDS = {"document", "version", "locale", "controllerName", "privacyContact", "approved"}
IDENTITY = re.compile(r"^(terms|privacy)/([A-Za-z0-9][A-Za-z0-9._-]{0,31})/(pt-BR|en-US)\.md$")


def parse_document(raw: str) -> dict:
    lines = raw.splitlines()
    if not lines or lines[0] != "---":
        raise ValueError("Document must start with JSON metadata front matter")
    try:
        end = lines.index("---", 1)
    except ValueError as error:
        raise ValueError("Document front matter is incomplete") from error
    metadata = json.loads("\n".join(lines[1:end]))
    if not isinstance(metadata, dict) or set(metadata) != FIELDS:
        raise ValueError("Document metadata fields must be exact")
    if metadata["document"] not in ("terms", "privacy") or metadata["locale"] not in ("pt-BR", "en-US"):
        raise ValueError("Unsupported document identity")
    if not isinstance(metadata["version"], str) or not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._-]{0,31}", metadata["version"]):
        raise ValueError("Invalid document version")
    if not isinstance(metadata["approved"], bool):
        raise ValueError("Publication approval must be explicit")
    for field in ("controllerName", "privacyContact"):
        if metadata[field] is not None and (not isinstance(metadata[field], str) or not metadata[field].strip()):
            raise ValueError("Controller/contact must be explicit nonempty text or null")
    blocks = [block.strip() for block in "\n".join(lines[end + 1:]).strip().split("\n\n") if block.strip()]
    if not blocks or not blocks[0].startswith("# ") or "\n" in blocks[0]:
        raise ValueError("Document must have one plain title")
    title = blocks[0][2:].strip()
    if not title:
        raise ValueError("Document title is empty")
    sections = []
    for block in blocks[1:]:
        if block.startswith("## ") and "\n" not in block:
            sections.append({"title": block[3:].strip(), "paragraphs": []})
        elif block.startswith("#") or not sections:
            raise ValueError("Unsupported heading or paragraph outside section")
        else:
            sections[-1]["paragraphs"].append(" ".join(block.splitlines()))
    if not sections or any(not section["title"] or not section["paragraphs"] for section in sections):
        raise ValueError("Document sections must contain text")
    return {"document": metadata["document"], "version": metadata["version"], "locale": metadata["locale"],
            "title": title, "controllerName": metadata["controllerName"], "privacyContact": metadata["privacyContact"],
            "approved": metadata["approved"], "sections": sections}


def load_catalog(source: Path = SOURCE) -> dict:
    manifest = json.loads((source / "catalog.json").read_text(encoding="utf-8"))
    if not isinstance(manifest, dict) or set(manifest) != {"termsVersion", "privacyVersion", "documents"}:
        raise ValueError("Catalog metadata fields must be exact")
    documents = []
    seen = set()
    for relative in manifest["documents"]:
        match = IDENTITY.fullmatch(relative) if isinstance(relative, str) else None
        if match is None or relative in seen:
            raise ValueError("Invalid or duplicate canonical document path")
        seen.add(relative)
        path = source / relative
        if not path.resolve().is_relative_to(source.resolve()) or path.is_symlink():
            raise ValueError("Canonical document must be a regular file inside the source tree")
        document = parse_document(path.read_text(encoding="utf-8"))
        if (document["document"], document["version"], document["locale"]) != match.groups():
            raise ValueError("Document metadata differs from its canonical path")
        documents.append(document)
    return {"termsVersion": manifest["termsVersion"], "privacyVersion": manifest["privacyVersion"], "documents": documents}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--write", action="store_true", help="Regenerate the embedded artifact from canonical documents")
    options = parser.parse_args()
    expected = json.dumps(load_catalog(), ensure_ascii=False, indent=2) + "\n"
    if options.write:
        OUTPUT.write_text(expected, encoding="utf-8")
    elif OUTPUT.read_text(encoding="utf-8") != expected:
        parser.error("Embedded legal artifact differs from canonical Markdown; regenerate with --write")
    print("Canonical legal sources match the embedded catalog")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
