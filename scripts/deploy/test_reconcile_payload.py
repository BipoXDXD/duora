"""Real bounded subprocesses and adversarial archives, without Docker or secrets."""
from __future__ import annotations

import hashlib
import io
import os
from pathlib import Path
import signal
import sys
import tarfile
import tempfile
import time
import unittest

import reconcile_payload as payload


def archive(name: str, data: bytes, *, kind: bytes = tarfile.REGTYPE,
            duplicate: bool = False, pax: dict[str, str] | None = None) -> bytes:
    buffer = io.BytesIO()
    with tarfile.open(fileobj=buffer, mode="w", format=tarfile.PAX_FORMAT if pax else tarfile.USTAR_FORMAT) as target:
        for _ in range(2 if duplicate else 1):
            member = tarfile.TarInfo(name)
            member.type = kind
            member.size = len(data) if kind == tarfile.REGTYPE else 0
            member.linkname = "outside" if kind in [tarfile.SYMTYPE, tarfile.LNKTYPE] else ""
            if pax:
                member.pax_headers = pax
            target.addfile(member, io.BytesIO(data) if kind == tarfile.REGTYPE else None)
    return buffer.getvalue()


class BinaryCaptureTests(unittest.TestCase):
    def child(self, program: str) -> list[str]:
        return [sys.executable, "-c", program]

    def test_non_utf8_stdout_is_returned_without_text_decoding(self) -> None:
        self.assertEqual(payload.capture_binary(self.child("import os;os.write(1,b'\\xff\\x00SQL')")),
                         b"\xff\x00SQL")

    def test_stdout_cannot_exceed_the_capture_budget(self) -> None:
        with self.assertRaises(payload.PayloadError):
            payload.capture_binary(self.child("import os;os.write(1,b'x'*33)"), max_bytes=32)

    def test_stdout_and_stderr_share_the_same_budget(self) -> None:
        with self.assertRaises(payload.PayloadError):
            payload.capture_binary(self.child("import os;os.write(1,b'x'*20);os.write(2,b'y'*20)"),
                                   max_bytes=32)

    def test_stderr_is_drained_without_waiting_for_stdout(self) -> None:
        started = time.monotonic()
        with self.assertRaises(payload.PayloadError):
            payload.capture_binary(self.child("import os;os.write(2,b'x'*1000000);os.write(1,b'ok')"),
                                   max_bytes=32, timeout_seconds=2)
        self.assertLess(time.monotonic() - started, 3)

    def test_failed_tool_does_not_return_its_partial_output(self) -> None:
        with self.assertRaises(payload.PayloadError):
            payload.capture_binary(self.child("import os;os.write(1,b'partial');raise SystemExit(7)"))

    def test_missing_executable_is_a_closed_capture_failure(self) -> None:
        with self.assertRaises(payload.PayloadError):
            payload.capture_binary(["/nonexistent-duora-capture-tool"])

    def test_empty_successful_tool_output_is_observed(self) -> None:
        self.assertEqual(payload.capture_binary(self.child("pass")), b"")

    def stopped_child(self, *, close_pipes: bool) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            pid_file = Path(temporary) / "pid"
            program = ("import os,time;from pathlib import Path;"
                       f"Path({str(pid_file)!r}).write_text(str(os.getpid()));")
            if close_pipes:
                program += "os.close(1);os.close(2);"
            program += "time.sleep(60)"

            def cleanup() -> None:
                if not pid_file.exists():
                    return
                pid = int(pid_file.read_text())
                try:
                    os.kill(pid, signal.SIGKILL)
                except ProcessLookupError:
                    return
                try:
                    os.waitpid(pid, 0)
                except ChildProcessError:
                    pass

            try:
                started = time.monotonic()
                with self.assertRaises(payload.PayloadError):
                    payload.capture_binary(self.child(program), timeout_seconds=0.4)
                self.assertLess(time.monotonic() - started, 3)
                self.assertTrue(pid_file.exists(), "the real child must have started before its deadline")
                with self.assertRaises(ProcessLookupError):
                    os.kill(int(pid_file.read_text()), 0)
            finally:
                cleanup()

    def test_deadline_terminates_and_reaps_the_real_process(self) -> None:
        self.stopped_child(close_pipes=False)

    def test_closed_pipes_do_not_make_a_running_process_successful(self) -> None:
        self.stopped_child(close_pipes=True)


class ArchiveTests(unittest.TestCase):
    def parse(self, data: bytes) -> bytes:
        return payload.parse_regular_archive(data, expected_name=payload.SQL_NAME,
                                             max_payload_bytes=payload.MAX_SQL_BYTES)

    def test_one_regular_file_returns_its_exact_bytes(self) -> None:
        self.assertEqual(self.parse(archive(payload.SQL_NAME, b"SELECT 1;\n")), b"SELECT 1;\n")

    def test_file_paths_duplicates_and_extra_members_are_refused(self) -> None:
        for name in ["../" + payload.SQL_NAME, "/app/" + payload.SQL_NAME,
                     "./" + payload.SQL_NAME, "different.sql"]:
            with self.subTest(name=name), self.assertRaises(payload.PayloadError):
                self.parse(archive(name, b"SELECT 1;"))
        with self.assertRaises(payload.PayloadError):
            self.parse(archive(payload.SQL_NAME, b"SELECT 1;", duplicate=True))

    def test_links_directories_and_sparse_members_are_refused(self) -> None:
        for kind in [tarfile.SYMTYPE, tarfile.LNKTYPE, tarfile.DIRTYPE, tarfile.GNUTYPE_SPARSE]:
            with self.subTest(kind=kind), self.assertRaises(payload.PayloadError):
                self.parse(archive(payload.SQL_NAME, b"", kind=kind))

    def test_declared_sql_size_is_bounded_before_extracting(self) -> None:
        with self.assertRaises(payload.PayloadError):
            self.parse(archive(payload.SQL_NAME, b"x" * (payload.MAX_SQL_BYTES + 1)))

    def test_archive_budget_includes_headers_and_padding(self) -> None:
        data = archive(payload.SQL_NAME, b"SELECT 1;") + bytes(payload.MAX_CAPTURE_BYTES)
        with self.assertRaises(payload.PayloadError):
            self.parse(data)

    def test_truncated_archive_or_missing_end_markers_is_refused(self) -> None:
        good = archive(payload.SQL_NAME, b"SELECT 1;")
        for data in [good[:500], good[:1024], b"not a tar"]:
            with self.subTest(size=len(data)), self.assertRaises(payload.PayloadError):
                self.parse(data)

    def test_nonzero_bytes_after_the_first_tar_end_are_refused(self) -> None:
        data = archive(payload.SQL_NAME, b"SELECT 1;") + b"x" * 512 + bytes(1024)
        with self.assertRaises(payload.PayloadError):
            self.parse(data)

    def test_pax_path_override_is_not_an_authorized_file(self) -> None:
        with self.assertRaises(payload.PayloadError):
            self.parse(archive(payload.SQL_NAME, b"SELECT 1;", pax={"path": payload.SQL_NAME}))


class SqlPayloadTests(unittest.TestCase):
    def pair(self, sql: bytes, checksum: bytes | None = None) -> tuple[bytes, bytes]:
        if checksum is None:
            checksum = hashlib.sha256(sql).hexdigest().encode() + b"  reconcile-privileges.sql\n"
        return archive(payload.SQL_NAME, sql), archive(payload.CHECKSUM_NAME, checksum)

    def test_matching_checksum_returns_canonical_sql(self) -> None:
        self.assertEqual(payload.verify_sql(*self.pair(b"SELECT 1;\n")), "SELECT 1;\n")

    def test_checksum_cannot_substitute_sql_or_filename(self) -> None:
        digest = hashlib.sha256(b"SELECT 1;").hexdigest().encode()
        for checksum in [b"0" * 64 + b"  reconcile-privileges.sql\n",
                         digest + b"  ../other.sql\n", digest + b"  other.sql\n",
                         digest + b"  reconcile-privileges.sql\nextra\n"]:
            with self.subTest(checksum=checksum), self.assertRaises(payload.PayloadError):
                payload.verify_sql(*self.pair(b"SELECT 1;", checksum))

    def test_checksum_metadata_has_its_own_smaller_budget(self) -> None:
        with self.assertRaises(payload.PayloadError):
            payload.verify_sql(*self.pair(b"SELECT 1;", b"x" * (payload.MAX_METADATA_BYTES + 1)))

    def test_empty_invalid_utf8_and_nul_sql_are_refused_even_with_matching_hash(self) -> None:
        for sql in [b"", b"\xff", b"SELECT\x00 1;"]:
            with self.subTest(sql=sql), self.assertRaises(payload.PayloadError):
                payload.verify_sql(*self.pair(sql))


if __name__ == "__main__":
    unittest.main()
