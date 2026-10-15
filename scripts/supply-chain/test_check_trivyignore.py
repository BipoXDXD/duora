"""Testes da regra do .trivyignore. Rodar: python3 -m unittest discover -s scripts/supply-chain"""

import unittest

from check_trivyignore import find_problems


class FindProblemsTests(unittest.TestCase):
    def test_empty_file_is_valid(self):
        self.assertEqual([], find_problems(""))

    def test_comments_and_blank_lines_only_are_valid(self):
        self.assertEqual([], find_problems("# Política no topo\n\n# outra nota\n"))

    def test_entry_with_justification_and_expiry_is_valid(self):
        text = "# Sem caminho de exploração: só roda no build\nCVE-2026-0001 exp:2026-12-31\n"
        self.assertEqual([], find_problems(text))

    def test_multiline_justification_is_valid(self):
        text = "# Linha um da justificativa\n# linha dois\nCVE-2026-0001 exp:2026-12-31\n"
        self.assertEqual([], find_problems(text))

    def test_entry_without_comment_above_is_rejected(self):
        self.assertEqual(
            ["linha 1: CVE-2026-0001 sem comentário de justificativa na linha de cima"],
            find_problems("CVE-2026-0001 exp:2026-12-31\n"),
        )

    def test_blank_line_between_comment_and_entry_is_rejected(self):
        text = "# justificativa solta\n\nCVE-2026-0001 exp:2026-12-31\n"
        self.assertEqual(
            ["linha 3: CVE-2026-0001 sem comentário de justificativa na linha de cima"], find_problems(text)
        )

    def test_empty_comment_is_not_a_justification(self):
        text = "#   \nCVE-2026-0001 exp:2026-12-31\n"
        self.assertEqual(
            ["linha 2: CVE-2026-0001 sem comentário de justificativa na linha de cima"], find_problems(text)
        )

    def test_entry_without_expiry_is_rejected(self):
        text = "# justificativa\nCVE-2026-0001\n"
        self.assertEqual(["linha 2: CVE-2026-0001 sem validade exp:AAAA-MM-DD"], find_problems(text))

    def test_entry_with_invalid_date_is_rejected(self):
        text = "# justificativa\nCVE-2026-0001 exp:2026-13-40\n"
        self.assertEqual(["linha 2: CVE-2026-0001 sem validade exp:AAAA-MM-DD"], find_problems(text))

    def test_each_entry_needs_its_own_justification(self):
        text = "# justificativa da primeira\nCVE-2026-0001 exp:2026-12-31\nCVE-2026-0002 exp:2026-12-31\n"
        self.assertEqual(
            ["linha 3: CVE-2026-0002 sem comentário de justificativa na linha de cima"], find_problems(text)
        )


if __name__ == "__main__":
    unittest.main()
