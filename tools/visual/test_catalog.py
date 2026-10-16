"""Testes do catálogo de objetos e da resolução dos papéis de cor nos tokens do frontend."""

import copy
import json
import unittest

from catalog import CATALOG_PATH, CatalogError, load_catalog, resolve_roles
from palette import Oklch

TOKENS = {"action": Oklch(0.47, 0.13, 265), "text-muted": Oklch(0.47, 0.014, 85)}


def raw_catalog():
    return json.loads(CATALOG_PATH.read_text(encoding="utf-8"))


class RealCatalogTest(unittest.TestCase):
    def test_loads_and_every_object_has_a_known_use(self):
        catalog = load_catalog()

        self.assertGreaterEqual(len([o for o in catalog.objects if o.source == "generated"]), 8)
        self.assertTrue(all(o.use in catalog.uses for o in catalog.objects))

    def test_prompt_prefix_forbids_people_and_text(self):
        prefix = load_catalog().prompt_prefix

        for term in ("no people", "no faces", "no body parts", "no text", "no letters", "no numbers"):
            self.assertIn(term, prefix)

    def test_roles_resolve_against_the_real_stylesheet(self):
        roles = resolve_roles(load_catalog(), tokens=None)

        self.assertEqual(roles["action"].h, 265)
        self.assertEqual(roles["neutral"].h, 85)


class CatalogValidationTest(unittest.TestCase):
    def load(self, mutate):
        data = raw_catalog()
        mutate(data)
        return load_catalog(data=copy.deepcopy(data))

    def test_rejects_unknown_use(self):
        def mutate(data):
            data["objects"][1]["use"] = "banner"

        with self.assertRaisesRegex(CatalogError, "banner"):
            self.load(mutate)

    def test_rejects_duplicate_names(self):
        def mutate(data):
            data["objects"][2]["name"] = data["objects"][1]["name"]

        with self.assertRaisesRegex(CatalogError, "duplicado"):
            self.load(mutate)

    def test_rejects_name_that_is_not_kebab_case(self):
        def mutate(data):
            data["objects"][1]["name"] = "Compass_1"

        with self.assertRaisesRegex(CatalogError, "Compass_1"):
            self.load(mutate)

    def test_rejects_generated_object_without_prompt(self):
        def mutate(data):
            data["objects"][1]["prompt"] = None

        with self.assertRaisesRegex(CatalogError, "prompt"):
            self.load(mutate)


class ResolveRolesTest(unittest.TestCase):
    def test_takes_hue_and_chroma_from_the_token(self):
        roles = resolve_roles(load_catalog(), TOKENS)

        self.assertEqual(roles["action"], Oklch(0.47, 0.13, 265))

    def test_chroma_override_keeps_the_token_hue(self):
        roles = resolve_roles(load_catalog(), TOKENS)

        self.assertEqual(roles["neutral"], Oklch(0.47, 0.03, 85))

    def test_missing_token_is_an_error(self):
        with self.assertRaisesRegex(CatalogError, "text-muted"):
            resolve_roles(load_catalog(), {"action": TOKENS["action"]})


if __name__ == "__main__":
    unittest.main()
