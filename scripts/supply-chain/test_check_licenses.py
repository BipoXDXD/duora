"""Testes do verificador de licenças. Rodar: python3 -m unittest discover -s scripts/supply-chain"""

import unittest

from check_licenses import Policy, PolicyError, check_components, is_allowed

ALLOWED = frozenset({"MIT", "Apache-2.0", "CC0-1.0"})


def component(name, licenses=None, group=None, version="1.0.0"):
    result = {"type": "library", "name": name, "version": version}
    if group is not None:
        result["group"] = group
    if licenses is not None:
        result["licenses"] = licenses
    return result


def license_id(spdx_id):
    return {"license": {"id": spdx_id}}


def policy(allowed=ALLOWED, exceptions=(), missing=()):
    return Policy.from_dict(
        {
            "allowed": sorted(allowed),
            "exceptions": list(exceptions),
            "missingLicenseAllowed": list(missing),
        }
    )


class IsAllowedTests(unittest.TestCase):
    def test_single_allowed_id(self):
        self.assertTrue(is_allowed("MIT", ALLOWED))

    def test_single_id_outside_allowlist(self):
        self.assertFalse(is_allowed("GPL-3.0-only", ALLOWED))

    def test_or_needs_one_allowed_side(self):
        self.assertTrue(is_allowed("GPL-3.0-only OR MIT", ALLOWED))

    def test_or_with_no_allowed_side(self):
        self.assertFalse(is_allowed("GPL-3.0-only OR AGPL-3.0-only", ALLOWED))

    def test_and_needs_every_side_allowed(self):
        self.assertFalse(is_allowed("MIT AND GPL-3.0-only", ALLOWED))

    def test_and_with_every_side_allowed(self):
        self.assertTrue(is_allowed("MIT AND Apache-2.0", ALLOWED))

    def test_and_binds_tighter_than_or(self):
        # MIT OR (GPL AND LGPL) passa; lido como (MIT OR GPL) AND LGPL, reprovaria.
        self.assertTrue(is_allowed("MIT OR GPL-3.0-only AND LGPL-3.0-only", ALLOWED))

    def test_parentheses_change_precedence(self):
        # (GPL OR MIT) AND LGPL -> LGPL fora da lista reprova.
        self.assertFalse(is_allowed("(GPL-3.0-only OR MIT) AND LGPL-3.0-only", ALLOWED))

    def test_with_exception_is_not_the_bare_license(self):
        self.assertFalse(is_allowed("Apache-2.0 WITH LLVM-exception", ALLOWED))

    def test_operators_are_case_insensitive(self):
        self.assertTrue(is_allowed("gpl-3.0-only or MIT", ALLOWED))

    def test_empty_expression_is_not_allowed(self):
        self.assertFalse(is_allowed("", ALLOWED))

    def test_malformed_expression_is_not_allowed(self):
        self.assertFalse(is_allowed("(MIT OR", ALLOWED))

    def test_trailing_tokens_are_not_allowed(self):
        self.assertFalse(is_allowed("MIT )", ALLOWED))

    def test_with_exception_listed_whole_is_allowed(self):
        self.assertTrue(is_allowed("Apache-2.0 WITH LLVM-exception", ALLOWED | {"Apache-2.0 WITH LLVM-exception"}))


class CheckComponentsTests(unittest.TestCase):
    def test_allowed_license_passes(self):
        result = check_components([component("react", [license_id("MIT")])], policy())
        self.assertEqual([], result.violations)
        self.assertEqual({"MIT": 1}, result.inventory)

    def test_license_outside_allowlist_fails(self):
        result = check_components([component("evil", [license_id("GPL-3.0-only")])], policy())
        self.assertEqual(["evil@1.0.0: licença fora da allowlist: GPL-3.0-only"], result.violations)

    def test_free_text_license_name_fails(self):
        result = check_components([component("x", [{"license": {"name": "Proprietary"}}])], policy())
        self.assertEqual(["x@1.0.0: licença fora da allowlist: Proprietary"], result.violations)

    def test_expression_entry_is_evaluated(self):
        result = check_components([component("type-fest", [{"expression": "MIT OR CC0-1.0"}])], policy())
        self.assertEqual([], result.violations)

    def test_every_license_entry_must_be_allowed(self):
        entries = [license_id("MIT"), license_id("GPL-3.0-only")]
        result = check_components([component("dual", entries)], policy())
        self.assertEqual(["dual@1.0.0: licença fora da allowlist: GPL-3.0-only"], result.violations)

    def test_missing_license_fails(self):
        result = check_components([component("mystery")], policy())
        self.assertEqual(["mystery@1.0.0: sem licença declarada"], result.violations)

    def test_empty_license_list_fails(self):
        result = check_components([component("mystery", [])], policy())
        self.assertEqual(["mystery@1.0.0: sem licença declarada"], result.violations)

    def test_scoped_name_uses_group(self):
        result = check_components([component("playwright", [license_id("GPL-3.0-only")], group="@axe-core")], policy())
        self.assertEqual(["@axe-core/playwright@1.0.0: licença fora da allowlist: GPL-3.0-only"], result.violations)

    def test_exception_accepts_exact_package_and_license(self):
        exception = {"package": "axe-core", "license": "MPL-2.0", "reason": "teste"}
        result = check_components([component("axe-core", [license_id("MPL-2.0")])], policy(exceptions=[exception]))
        self.assertEqual([], result.violations)
        self.assertEqual({"MPL-2.0": 1}, result.inventory)

    def test_exception_package_accepts_glob(self):
        exception = {"package": "lightningcss-*", "license": "MPL-2.0", "reason": "teste"}
        result = check_components(
            [component("lightningcss-linux-x64-gnu", [license_id("MPL-2.0")])], policy(exceptions=[exception])
        )
        self.assertEqual([], result.violations)

    def test_exception_does_not_cover_another_license(self):
        exception = {"package": "axe-core", "license": "MPL-2.0", "reason": "teste"}
        result = check_components([component("axe-core", [license_id("GPL-3.0-only")])], policy(exceptions=[exception]))
        self.assertEqual(["axe-core@1.0.0: licença fora da allowlist: GPL-3.0-only"], result.violations)

    def test_exception_does_not_cover_another_package(self):
        exception = {"package": "axe-core", "license": "MPL-2.0", "reason": "teste"}
        result = check_components([component("other", [license_id("MPL-2.0")])], policy(exceptions=[exception]))
        self.assertEqual(["other@1.0.0: licença fora da allowlist: MPL-2.0"], result.violations)

    def test_missing_license_pattern_accepts_matching_package(self):
        missing = {"pattern": "lightningcss-*", "reason": "teste"}
        result = check_components([component("lightningcss-win32-x64-msvc")], policy(missing=[missing]))
        self.assertEqual([], result.violations)
        self.assertEqual({"(sem licença, exceção)": 1}, result.inventory)

    def test_missing_license_pattern_does_not_waive_a_declared_license(self):
        missing = {"pattern": "lightningcss-*", "reason": "teste"}
        result = check_components(
            [component("lightningcss-linux-x64-gnu", [license_id("GPL-3.0-only")])], policy(missing=[missing])
        )
        self.assertEqual(["lightningcss-linux-x64-gnu@1.0.0: licença fora da allowlist: GPL-3.0-only"], result.violations)

    def test_non_library_components_are_ignored(self):
        app = {"type": "application", "name": "frontend"}
        result = check_components([app], policy())
        self.assertEqual([], result.violations)
        self.assertEqual({}, result.inventory)

    def test_unused_exception_is_reported(self):
        exception = {"package": "axe-core", "license": "MPL-2.0", "reason": "teste"}
        result = check_components([component("react", [license_id("MIT")])], policy(exceptions=[exception]))
        self.assertEqual(["exceção sem uso: axe-core (MPL-2.0)"], result.unused_exceptions)


class PolicyTests(unittest.TestCase):
    def test_exception_without_reason_is_rejected(self):
        with self.assertRaises(PolicyError):
            policy(exceptions=[{"package": "axe-core", "license": "MPL-2.0", "reason": "  "}])

    def test_missing_license_pattern_without_reason_is_rejected(self):
        with self.assertRaises(PolicyError):
            policy(missing=[{"pattern": "x-*"}])

    def test_empty_allowlist_is_rejected(self):
        with self.assertRaises(PolicyError):
            policy(allowed=frozenset())


if __name__ == "__main__":
    unittest.main()
