"""Canonical document provenance and packaging parity; executed by the existing supply-chain gate."""
import json
import unittest
from legal_documents import OUTPUT, SOURCE, load_catalog, parse_document


class LegalDocumentSourcesTests(unittest.TestCase):
    def test_canonical_markdown_matches_every_embedded_document(self):
        catalog = load_catalog()
        self.assertEqual(catalog, json.loads(OUTPUT.read_text(encoding="utf-8")))
        identities = {(document["document"], document["locale"]) for document in catalog["documents"]}
        self.assertEqual(identities, {(kind, locale) for kind in ("terms", "privacy") for locale in ("pt-BR", "en-US")})
        for document in catalog["documents"]:
            path = SOURCE / document["document"] / document["version"] / (document["locale"] + ".md")
            self.assertTrue(path.is_file())

    def test_parser_preserves_the_actual_language_and_text(self):
        text = (SOURCE / "terms/2026-10-25/en-US.md").read_text(encoding="utf-8")
        document = parse_document(text)
        self.assertEqual(document["locale"], "en-US")
        self.assertIn("support@meetduora.com", str(document["sections"]))
        self.assertTrue(document["approved"])
        self.assertEqual(document["controllerName"], "Luca Grimmer")

    def test_every_active_document_is_approved_under_the_named_controller(self):
        for document in load_catalog()["documents"]:
            with self.subTest(document=(document["document"], document["locale"])):
                self.assertEqual(document["controllerName"], "Luca Grimmer")
                self.assertTrue(document["approved"])

    def test_missing_identity_or_meaningful_document_body_is_rejected(self):
        original = (SOURCE / "terms/2026-10-25/pt-BR.md").read_text(encoding="utf-8")
        for changed in (original.replace('"locale": "pt-BR",', ''), original.split("# ")[0],
                        original.replace('"approved": true', '"approved": "yes"')):
            with self.subTest(changed=changed[:32]):
                with self.assertRaises(ValueError):
                    parse_document(changed)


if __name__ == "__main__":
    unittest.main()
