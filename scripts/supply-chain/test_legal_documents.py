"""Canonical document provenance and packaging parity; executed by the existing supply-chain gate."""
import hashlib
import json
import re
import unittest
from legal_documents import OUTPUT, SOURCE, load_catalog, parse_document

# Temporary (decisions-2026-11-07): the identification returns once there is a registered entity to publish.
NEUTRAL_CONTROLLER = "Responsável pelo Duora"
# SHA-256 of each casefolded word of the former personal identification, so the guard does not republish it.
FORMER_IDENTIFICATION_WORDS = {
    "6ad24e7987574a99f016032e7603f0cad2842d85c3a3162fe53a05e6e58547cb",
    "d70f47790f689414789eeff231703429c7f88a10210775906460edbf38589d90",
}
FORMER_IDENTIFICATION_PHRASES = ("pessoa física", "pessoa fisica", ", an individual")


def active_documents(catalog: dict) -> list:
    return [document for document in catalog["documents"]
            if document["version"] == catalog[document["document"] + "Version"]]


def former_identification_in(text: str) -> list:
    words = re.findall(r"[^\W\d_]+", text.casefold())
    found = [word for word in words if hashlib.sha256(word.encode()).hexdigest() in FORMER_IDENTIFICATION_WORDS]
    return found + [phrase for phrase in FORMER_IDENTIFICATION_PHRASES if phrase in text.casefold()]


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
        catalog = load_catalog()
        text = (SOURCE / "terms" / catalog["termsVersion"] / "en-US.md").read_text(encoding="utf-8")
        document = parse_document(text)
        self.assertEqual(document["locale"], "en-US")
        self.assertIn("support@meetduora.com", str(document["sections"]))
        self.assertTrue(document["approved"])
        self.assertEqual(document["controllerName"], NEUTRAL_CONTROLLER)

    def test_every_active_document_is_approved_under_the_exact_neutral_controller(self):
        active = active_documents(load_catalog())
        self.assertEqual(len(active), 4)
        for document in active:
            with self.subTest(document=(document["document"], document["locale"])):
                self.assertEqual(document["controllerName"], NEUTRAL_CONTROLLER)
                self.assertTrue(document["approved"])

    def test_active_documents_do_not_carry_the_former_personal_identification(self):
        for document in active_documents(load_catalog()):
            with self.subTest(document=(document["document"], document["locale"])):
                self.assertEqual(former_identification_in(json.dumps(document, ensure_ascii=False)), [])

    def test_the_guard_reads_case_and_punctuation_variations(self):
        self.assertEqual(former_identification_in("Operated by someone, an individual."), [", an individual"])
        self.assertEqual(former_identification_in("mantido por PESSOA FÍSICA"), ["pessoa física"])
        self.assertEqual(former_identification_in("Duora, support@meetduora.com"), [])

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

    def test_superseded_versions_stay_in_the_canonical_catalog(self):
        catalog = load_catalog()
        self.assertEqual(catalog["termsVersion"], "2026-11-07")
        self.assertEqual(catalog["privacyVersion"], "2026-11-07")
        archived = [document for document in catalog["documents"] if document["version"] == "2026-10-25" and document["document"] == "terms"]
        self.assertEqual({document["locale"] for document in archived}, {"pt-BR", "en-US"})
        self.assertFalse(any("18" in str(document["sections"]) for document in archived))
        retained = {(document["document"], document["version"]) for document in catalog["documents"]}
        self.assertLessEqual({("terms", "2026-10-29"), ("privacy", "2026-10-25")}, retained)

    def test_missing_identity_or_meaningful_document_body_is_rejected(self):
        original = (SOURCE / "terms/2026-10-25/pt-BR.md").read_text(encoding="utf-8")
        for changed in (original.replace('"locale": "pt-BR",', ''), original.split("# ")[0],
                        original.replace('"approved": true', '"approved": "yes"')):
            with self.subTest(changed=changed[:32]):
                with self.assertRaises(ValueError):
                    parse_document(changed)


if __name__ == "__main__":
    unittest.main()
