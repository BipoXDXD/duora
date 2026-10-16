"""Testes do verificador de segredo em imagem. Rodar: python3 -m unittest discover -s scripts/supply-chain"""

import io
import unittest

from check_image_secret import CHUNK, needles, stream_contains

# Licença sintética de baixa entropia, montada em partes: com cara de chave de verdade, o Gitleaks a apontaria.
ID = "i" * 20
SIGNATURE_A = "a" * 24
SIGNATURE_B = "b" * 24
SECRET = "Id=" + ID + ";Kind=Community;ExpiryDateUtc=2099-01-01;Key=Assembly:" + SIGNATURE_A + "|" + SIGNATURE_B


class NeedlesTests(unittest.TestCase):
    def test_the_whole_secret_is_a_needle(self):
        self.assertIn(SECRET.encode(), needles(SECRET))

    def test_long_fields_are_needles_too(self):
        found = needles(SECRET)
        self.assertIn(SIGNATURE_A.encode(), found)
        self.assertIn(ID.encode(), found)

    def test_short_common_fields_are_not_needles(self):
        found = needles(SECRET)
        self.assertNotIn(b"Community", found)
        self.assertNotIn(b"Kind", found)


class StreamContainsTests(unittest.TestCase):
    def test_finds_a_needle_in_the_middle(self):
        data = b"x" * 1000 + SIGNATURE_A.encode() + b"y" * 1000
        self.assertTrue(stream_contains(io.BytesIO(data), needles(SECRET)))

    def test_finds_a_needle_split_across_blocks(self):
        needle = SIGNATURE_A.encode()
        data = b"x" * (CHUNK - 5) + needle + b"y" * 10
        self.assertTrue(stream_contains(io.BytesIO(data), [needle]))

    def test_clean_stream_has_no_needle(self):
        self.assertFalse(stream_contains(io.BytesIO(b"z" * (CHUNK * 2 + 7)), needles(SECRET)))

    def test_empty_stream_has_no_needle(self):
        self.assertFalse(stream_contains(io.BytesIO(b""), needles(SECRET)))


if __name__ == "__main__":
    unittest.main()
