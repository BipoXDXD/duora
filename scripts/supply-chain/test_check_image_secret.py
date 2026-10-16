"""Testes do verificador de segredo em imagem. Rodar: python3 -m unittest discover -s scripts/supply-chain"""

import base64
import gzip
import io
import tarfile
import unittest

from check_image_secret import CHUNK, UnsupportedLayer, archive_contains, needles, stream_contains

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


class EncodedNeedlesTests(unittest.TestCase):
    def test_finds_the_secret_stored_as_utf16(self):
        data = b"x" * 100 + SECRET.encode("utf-16-le") + b"y" * 100
        self.assertTrue(stream_contains(io.BytesIO(data), needles(SECRET)))

    def test_finds_the_secret_inside_base64_at_every_alignment(self):
        for prefix in (b"", b"a", b"ab"):
            with self.subTest(prefix=prefix):
                data = b"cfg=" + base64.b64encode(prefix + SECRET.encode() + b"tail") + b"\n"
                self.assertTrue(stream_contains(io.BytesIO(data), needles(SECRET)))

    def test_finds_a_field_inside_urlsafe_base64(self):
        data = base64.urlsafe_b64encode(b"k" + SIGNATURE_A.encode())
        self.assertTrue(stream_contains(io.BytesIO(data), needles(SECRET)))

    def test_base64_of_other_text_has_no_needle(self):
        data = base64.b64encode(b"z" * 4096)
        self.assertFalse(stream_contains(io.BytesIO(data), needles(SECRET)))


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


def tar_bytes(files):
    """Tar sem compressão com os arquivos dados (nome -> conteúdo), como uma camada ou o próprio `docker save`."""
    buffer = io.BytesIO()
    with tarfile.open(fileobj=buffer, mode="w") as archive:
        for name, content in files.items():
            info = tarfile.TarInfo(name)
            info.size = len(content)
            archive.addfile(info, io.BytesIO(content))
    return buffer.getvalue()


def saved_image(*layers):
    """Arquivo de `docker save` no layout OCI: um blob por camada, mais manifesto e índice."""
    files = {f"blobs/sha256/{index:064x}": layer for index, layer in enumerate(layers)}
    files["index.json"] = b'{"schemaVersion": 2}'
    files["manifest.json"] = b"[]"
    return tar_bytes(files)


class ArchiveContainsTests(unittest.TestCase):
    """Camada por camada (`docker save`), e não o filesystem achatado: um arquivo apagado depois continua na imagem."""

    def test_finds_the_secret_in_a_layer_whose_file_a_later_layer_deleted(self):
        written = tar_bytes({"app/sixlabors.lic": SECRET.encode()})
        deleted = tar_bytes({"app/.wh.sixlabors.lic": b""})
        self.assertTrue(archive_contains(io.BytesIO(saved_image(written, deleted)), needles(SECRET)))

    def test_finds_the_secret_in_a_gzip_layer(self):
        layer = gzip.compress(tar_bytes({"app/config.json": b'{"license": "' + SECRET.encode() + b'"}'}))
        self.assertTrue(archive_contains(io.BytesIO(saved_image(layer)), needles(SECRET)))

    def test_a_clean_image_has_no_needle(self):
        layer = gzip.compress(tar_bytes({"app/Duora.Worker.dll": b"z" * 4096}))
        self.assertFalse(archive_contains(io.BytesIO(saved_image(tar_bytes({"etc/os-release": b"x"}), layer)), needles(SECRET)))

    def test_a_zstd_layer_fails_closed_instead_of_passing_unread(self):
        layer = b"\x28\xb5\x2f\xfd" + b"\0" * 64
        with self.assertRaises(UnsupportedLayer):
            archive_contains(io.BytesIO(saved_image(layer)), needles(SECRET))


if __name__ == "__main__":
    unittest.main()
