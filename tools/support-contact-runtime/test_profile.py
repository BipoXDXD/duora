"""Only new packaging boundaries; no preserved runner or IOptions cases."""
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import tarfile
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).parent


class ProfileTests(unittest.TestCase):
    def load(self):
        self.assertTrue((ROOT / "profile.py").exists(), "hermetic profile packaging is missing")
        spec = importlib.util.spec_from_file_location("profile_packaging", ROOT / "profile.py")
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        return module

    def archive(self, members):
        stream = io.BytesIO()
        with tarfile.open(fileobj=stream, mode="w:gz") as tf:
            for name, data, kind, target in members:
                info = tarfile.TarInfo(name)
                info.type, info.linkname, info.mode = kind, target, 0o755
                info.size = len(data) if kind == tarfile.REGTYPE else 0
                tf.addfile(info, io.BytesIO(data) if info.size else None)
        return stream.getvalue()

    def members(self):
        return [("python/bin/python3.13", b"synthetic-not-executable", tarfile.REGTYPE, ""),
                ("python/bin/python3", b"", tarfile.SYMTYPE, "python3.13"),
                ("python/lib/python3.13/os.py", b"synthetic", tarfile.REGTYPE, "")]

    def test_reviewed_normal_parent_and_alias_chain_materialize_final_regular_file(self):
        module = self.load()
        aliases = {"python/share/terminfo/1/1178": "../a/adm1178",
                   "python/share/terminfo/a/adm1178": "real-terminal"}
        members = self.members()[:1] + [self.members()[2],
            ("python/share/terminfo/1/1178", b"", tarfile.SYMTYPE, "../a/adm1178"),
            ("python/share/terminfo/a/adm1178", b"", tarfile.SYMTYPE, "real-terminal"),
            ("python/share/terminfo/a/real-terminal", b"synthetic-terminal", tarfile.REGTYPE, "")]
        raw = self.archive(members)
        with tempfile.TemporaryDirectory() as tmp, patch.dict(module.REVIEWED_ALIASES, aliases):
            try:
                closure = module.unpack_python(io.BytesIO(raw), Path(tmp) / "out", hashlib.sha256(raw).hexdigest())
            except module.Refusal as error:
                self.fail("reviewed relative-parent alias chain was refused: " + str(error))
            alias = Path(tmp) / "out/python/share/terminfo/1/1178"
            self.assertEqual(alias.read_bytes(), b"synthetic-terminal")
            self.assertFalse(alias.is_symlink())
            self.assertEqual(closure["python/share/terminfo/1/1178"]["source_link"], "../a/adm1178")

    def test_reviewed_alias_cycles_and_escapes_fail_before_extraction(self):
        module = self.load()
        for aliases in [
            {"python/share/terminfo/a/one": "two", "python/share/terminfo/a/two": "one"},
            {"python/share/terminfo/a/one": "../../../../outside"},
            {"python/share/terminfo/a/one": "/etc/passwd"},
        ]:
            entries = self.members()[:1] + [self.members()[2]] + [
                (name, b"", tarfile.SYMTYPE, target) for name, target in aliases.items()]
            raw = self.archive(entries)
            with self.subTest(aliases=aliases), tempfile.TemporaryDirectory() as tmp, patch.dict(module.REVIEWED_ALIASES, aliases):
                target = Path(tmp) / "out"
                with self.assertRaises(module.Refusal):
                    module.unpack_python(io.BytesIO(raw), target, hashlib.sha256(raw).hexdigest())
                self.assertFalse(target.exists())

    def test_minimal_closure_omits_dbm_pip_and_unneeded_native_files(self):
        module = self.load()
        self.assertTrue(hasattr(module, "select_minimal_python"), "license-scoped runtime selection missing")
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            files = {"python/bin/python3.13": b"synthetic-interpreter",
                     "python/lib/libpython3.13.so.1.0": b"synthetic-core",
                     "python/lib/python3.13/LICENSE.txt": b"synthetic-license",
                     "python/lib/python3.13/os.py": b"synthetic-source",
                     "python/lib/python3.13/lib-dynload/_dbm.cpython-313-x86_64-linux-gnu.so": b"excluded-db",
                     "python/lib/python3.13/site-packages/pip/__init__.py": b"excluded-pip",
                     "python/lib/python3.13/ensurepip/__init__.py": b"excluded-bootstrap"}
            for name, content in files.items():
                target = root / name
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_bytes(content)
            missing_alias = "python/share/terminfo/A/CASE"
            inventory = {name: {"sha256": hashlib.sha256(content).hexdigest(), "bytes": len(content)} for name, content in files.items()}
            inventory[missing_alias] = {"sha256": hashlib.sha256(b"").hexdigest(), "bytes": 0}
            try:
                selection = module.select_minimal_python(root, inventory)
            except TypeError:
                self.fail("selection lost original archive entries absent from host filesystem")
            self.assertIn(missing_alias, selection["excluded"])
            self.assertEqual(set(selection["included"]), {
                "python/bin/python3.13", "python/lib/libpython3.13.so.1.0",
                "python/lib/python3.13/LICENSE.txt", "python/lib/python3.13/os.py"})
            self.assertFalse((root / "python/lib/python3.13/site-packages/pip/__init__.py").exists())
            self.assertEqual(selection["excluded"]["python/lib/python3.13/lib-dynload/_dbm.cpython-313-x86_64-linux-gnu.so"]["reason"], "native_not_required")

    def test_minimal_closure_refuses_native_binary_hidden_as_source(self):
        module = self.load()
        self.assertTrue(hasattr(module, "select_minimal_python"), "closed native selection missing")
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            target = root / "python/lib/python3.13/unknown.py"
            target.parent.mkdir(parents=True)
            target.write_bytes(b"\x7fELFunknown")
            with self.assertRaisesRegex(module.Refusal, "native_binary_unreviewed"):
                module.select_minimal_python(root)

    def test_declared_notice_missing_or_tampered_is_refused(self):
        module = self.load()
        self.assertTrue(hasattr(module, "notice_inputs"), "verified supplemental notice packaging missing")
        with tempfile.TemporaryDirectory() as tmp, patch.object(module, "ROOT", Path(tmp)):
            root = Path(tmp)
            (root / "notices").mkdir()
            record = {"path": "tools/support-contact-runtime/notices/LICENSE.fixture.txt", "bytes": 8,
                      "sha256": hashlib.sha256(b"verbatim").hexdigest(), "verbatim": True}
            (root / "python-license-inventory.json").write_text(json.dumps({"supplemental_notice_files": [record]}))
            for content in (None, b"tampered"):
                target = root / "notices/LICENSE.fixture.txt"
                if content is not None:
                    target.write_bytes(content)
                with self.subTest(content=content), self.assertRaises(module.Refusal):
                    module.notice_inputs()
            target.write_bytes(b"verbatim")
            inventory, files, manifest = module.notice_inputs()
            self.assertEqual(files, {"LICENSE.fixture.txt": b"verbatim"})
            self.assertEqual(manifest["notices"]["LICENSE.fixture.txt"]["sha256"], record["sha256"])
            (root / "notices/EXTRA.txt").write_text("unapproved")
            with self.assertRaises(module.Refusal):
                module.notice_inputs()

    def test_published_digest_checked_before_destination_is_created(self):
        module = self.load()
        with tempfile.TemporaryDirectory() as tmp:
            target = Path(tmp) / "unpacked"
            with self.assertRaisesRegex(module.Refusal, "archive_digest_mismatch"):
                module.unpack_python(io.BytesIO(self.archive(self.members())), target, "0" * 64)
            self.assertFalse(target.exists())

    def test_internal_reviewed_python_alias_becomes_regular_file_and_closure(self):
        module = self.load()
        raw = self.archive(self.members())
        with tempfile.TemporaryDirectory() as tmp:
            target = Path(tmp) / "unpacked"
            closure = module.unpack_python(io.BytesIO(raw), target, hashlib.sha256(raw).hexdigest())
            alias = target / "python/bin/python3"
            self.assertFalse(alias.is_symlink())
            self.assertEqual(alias.read_bytes(), b"synthetic-not-executable")
            self.assertEqual(closure["python/bin/python3"]["source_link"], "python3.13")
            self.assertEqual(set(closure), {"python/bin/python3", "python/bin/python3.13", "python/lib/python3.13/os.py"})

    def test_unsafe_archive_members_fail_without_partial_extraction(self):
        module = self.load()
        bad = [("../escape", b"x", tarfile.REGTYPE, ""),
               ("/absolute", b"x", tarfile.REGTYPE, ""),
               ("python/bin/../../../escape", b"x", tarfile.REGTYPE, ""),
               ("python/bin/evil", b"", tarfile.SYMTYPE, "/etc/passwd"),
               ("python/lib/directory", b"", tarfile.SYMTYPE, "../bin"),
               ("python/bin/python3", b"", tarfile.LNKTYPE, "python/bin/python3.13"),
               ("python/device", b"", tarfile.FIFOTYPE, "")]
        for item in bad:
            with self.subTest(item=item[0], kind=item[2]), tempfile.TemporaryDirectory() as tmp:
                raw = self.archive(self.members()[:1] + [item])
                target = Path(tmp) / "unpacked"
                with self.assertRaises(module.Refusal):
                    module.unpack_python(io.BytesIO(raw), target, hashlib.sha256(raw).hexdigest())
                self.assertFalse(target.exists())

    def test_duplicate_members_and_decompression_ceiling_are_refused(self):
        module = self.load()
        for entries, cap in [(self.members()[:1] * 2, 4096), (self.members(), 1)]:
            with tempfile.TemporaryDirectory() as tmp:
                raw = self.archive(entries)
                with self.assertRaises(module.Refusal):
                    module.unpack_python(io.BytesIO(raw), Path(tmp) / "out", hashlib.sha256(raw).hexdigest(), max_bytes=cap)

    def test_context_tar_is_reproducible_and_excludes_ambient_files(self):
        module = self.load()
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            context = root / "context"
            context.mkdir()
            (context / "allowed").write_bytes(b"public")
            (root / "private-sentinel").write_bytes(b"DO-NOT-COPY")
            first, second = root / "one.tar", root / "two.tar"
            module.write_context(context, first)
            (context / "allowed").chmod(0o600)
            module.write_context(context, second)
            self.assertEqual(first.read_bytes(), second.read_bytes())
            with tarfile.open(first) as tf:
                self.assertEqual(tf.getnames(), ["allowed"])
                member = tf.getmember("allowed")
                self.assertEqual((member.uid, member.gid, member.mtime), (0, 0, 0))
                self.assertEqual(tf.extractfile(member).read(), b"public")

    def test_context_preserves_normalized_executable_modes_in_closure(self):
        module = self.load()
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            context = root / "context"
            context.mkdir()
            library = context / "libpython.so"
            library.write_bytes(b"synthetic")
            library.chmod(0o755)
            output = root / "context.tar"
            module.write_context(context, output)
            with tarfile.open(output) as tf:
                self.assertEqual(tf.getmember("libpython.so").mode, 0o755)

    def test_artifact_and_runner_checks_refuse_modified_or_extra_inputs(self):
        module = self.load()
        with tempfile.TemporaryDirectory() as tmp:
            artifact = Path(tmp)
            (artifact / "unapproved").write_text("sentinel")
            with self.assertRaisesRegex(module.Refusal, "artifact_untrusted"):
                module.verify_artifact(artifact)
            with self.assertRaisesRegex(module.Refusal, "runner_untrusted"):
                module.verified_runner(artifact / "unapproved")

    def test_qualification_controls_are_candidate_only_and_fixed(self):
        module = self.load()
        plan = module.qualification_plan("sha256:" + "a" * 64)
        self.assertEqual(plan["state"], "NOT_BUILT_NOT_QUALIFIED_NOT_ADMITTED")
        self.assertEqual(plan["controls"], {"uid_gid": "1654:1654", "network": "none", "read_only": True, "cap_drop": ["ALL"], "no_new_privileges": True, "memory_bytes": 536870912, "memory_swap_bytes": 536870912, "cpus": "0.5", "pids_limit": 64, "tmpfs_bytes": 16777216, "core_bytes": 0})
        self.assertFalse(plan["operational_capture_authorized"])
        with self.assertRaises(module.Refusal):
            module.qualification_plan("latest")


if __name__ == "__main__":
    unittest.main()
