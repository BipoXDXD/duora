"""Testes do validador da chave do apt. Rodar da raiz:
    python3 -m unittest discover -s infra/ansible/roles/docker/files -v
Precisa do gpg (gera chaves descartáveis num GNUPGHOME temporário).
"""

import os
import subprocess
import tempfile
import unittest
from pathlib import Path

import validate_apt_key

KEY_SPEC = ("ed25519", "sign", "never")


class ValidateAptKeyTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls._home = tempfile.TemporaryDirectory(prefix="duora-gpg-")
        cls.env = {**os.environ, "GNUPGHOME": cls._home.name}
        cls.trusted = cls._generate("Docker de teste <docker@example.invalid>")
        cls.attacker = cls._generate("Atacante <attacker@example.invalid>")

    @classmethod
    def tearDownClass(cls) -> None:
        subprocess.run(["gpgconf", "--kill", "gpg-agent"], env=cls.env, check=False, capture_output=True)
        cls._home.cleanup()

    @classmethod
    def _generate(cls, user_id: str) -> str:
        subprocess.run(["gpg", "--batch", "--passphrase", "", "--quick-generate-key", user_id, *KEY_SPEC],
                       env=cls.env, check=True, capture_output=True)
        listing = subprocess.run(["gpg", "--with-colons", "--list-keys", user_id], env=cls.env, check=True,
                                 capture_output=True, text=True).stdout
        return next(line.split(":")[9] for line in listing.splitlines() if line.startswith("fpr:"))

    def export(self, *fingerprints: str, armor: bool = True) -> Path:
        args = ["gpg", "--batch", "--export", *(["--armor"] if armor else []), *fingerprints]
        data = subprocess.run(args, env=self.env, check=True, capture_output=True).stdout
        handle, path = tempfile.mkstemp(dir=self._home.name)
        with os.fdopen(handle, "wb") as file:
            file.write(data)
        return Path(path)

    def validate(self, path: Path) -> None:
        validate_apt_key.validate(path, self.trusted)

    def test_expected_key_alone_passes(self) -> None:
        self.validate(self.export(self.trusted))

    def test_expected_key_dearmored_passes(self) -> None:
        self.validate(self.export(self.trusted, armor=False))

    def test_other_key_fails(self) -> None:
        with self.assertRaisesRegex(validate_apt_key.KeyValidationError, "impressão digital"):
            self.validate(self.export(self.attacker))

    def test_expected_key_plus_extra_key_fails(self) -> None:
        # O apt confia em todas as chaves do arquivo signed_by: uma chave a mais é uma porta para o atacante.
        with self.assertRaisesRegex(validate_apt_key.KeyValidationError, "2 chaves"):
            self.validate(self.export(self.trusted, self.attacker))

    def test_extra_key_before_expected_key_fails(self) -> None:
        with self.assertRaisesRegex(validate_apt_key.KeyValidationError, "2 chaves"):
            self.validate(self.export(self.attacker, self.trusted))

    def test_file_without_key_fails(self) -> None:
        empty = Path(self._home.name) / "vazio.asc"
        empty.write_text("não é uma chave\n", encoding="utf-8")

        with self.assertRaises(validate_apt_key.KeyValidationError):
            self.validate(empty)


if __name__ == "__main__":
    unittest.main()
