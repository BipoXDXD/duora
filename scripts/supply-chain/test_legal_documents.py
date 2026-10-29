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

    def test_active_terms_restrict_the_service_to_adults_who_declare_it(self):
        # Issue #9, item 5: product coherence with the 18+ requirement; not age verification nor legal compliance.
        catalog = load_catalog()
        expected = {"pt-BR": ("Idade mínima", "18 anos ou mais", "você declara ter 18 anos ou mais"),
                    "en-US": ("Minimum age", "18 or older", "you declare that you are 18 or older")}
        for document in catalog["documents"]:
            if document["document"] != "terms" or document["version"] != catalog["termsVersion"]:
                continue
            title, restriction, declaration = expected.pop(document["locale"])
            with self.subTest(locale=document["locale"]):
                section = next(section for section in document["sections"] if section["title"] == title)
                self.assertIn(restriction, " ".join(section["paragraphs"]))
                self.assertIn(declaration, " ".join(section["paragraphs"]))
        self.assertEqual(expected, {})

    def test_superseded_terms_stay_resolvable_and_unchanged(self):
        catalog = load_catalog()
        self.assertEqual(catalog["termsVersion"], "2026-10-29")
        archived = [document for document in catalog["documents"] if document["version"] == "2026-10-25" and document["document"] == "terms"]
        self.assertEqual({document["locale"] for document in archived}, {"pt-BR", "en-US"})
        self.assertFalse(any("18" in str(document["sections"]) for document in archived))

    def test_missing_identity_or_meaningful_document_body_is_rejected(self):
        original = (SOURCE / "terms/2026-10-25/pt-BR.md").read_text(encoding="utf-8")
        for changed in (original.replace('"locale": "pt-BR",', ''), original.split("# ")[0],
                        original.replace('"approved": true', '"approved": "yes"')):
            with self.subTest(changed=changed[:32]):
                with self.assertRaises(ValueError):
                    parse_document(changed)


if __name__ == "__main__":
    unittest.main()
