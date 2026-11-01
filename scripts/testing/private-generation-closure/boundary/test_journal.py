"""Explicit states, real process crashes and drained I/O; no SQL completion capability."""
import concurrent.futures
import json
import subprocess
import unittest
from fixture import fixture


class JournalTests(unittest.TestCase):
    def test_close_then_sweep_and_late_put_are_not_reopened(self):
        for area in ("candidate", "promotion"):
            with self.subTest(area=area), fixture(area) as f:
                for kind in ("thumb", "card", "full"):
                    self.assertEqual("Written", f.rpc("client", "put", variant=kind)["code"])
                self.assertEqual("Denied", f.rpc("client", "close", body_length=0)["code"])
                self.assertEqual("ClosedExperimental", f.close()["code"])
                self.assertEqual("SweptExperimental", f.sweep()["code"])
                self.assertEqual([], f.private_files())
                f.restart()
                self.assertEqual("Denied", f.rpc("client", "put")["code"])
                self.assertEqual("ClosedExperimental", f.close()["code"])
                self.assertEqual("SweptExperimental", f.sweep()["code"])

    def test_sweep_without_prior_closure_is_denied(self):
        with fixture() as f:
            self.assertEqual("Written", f.rpc("client", "put")["code"])
            self.assertEqual("Denied", f.sweep()["code"])
            self.assertEqual(1, len(f.private_files()))

    def test_two_closers_and_writer_share_the_same_global_gate(self):
        with fixture() as f:
            f.restart(point="closing-synced")
            with concurrent.futures.ThreadPoolExecutor() as pool:
                pending = pool.submit(f.close)
                f.wait_barrier()
                try:
                    self.assertEqual("Busy", f.close()["code"])
                    self.assertEqual("Busy", f.rpc("client", "put", grant=f.grants[1])["code"])
                finally:
                    f.release_barrier()
                first = pending.result()
            self.assertEqual("ClosedExperimental", first["code"])
            self.assertEqual(first, f.close())

    def test_missing_empty_truncated_or_corrupt_journal_never_means_open(self):
        faults = ["os.unlink('/state/control/domain.journal')",
                  "open('/state/control/domain.journal','wb').close()",
                  "open('/state/control/domain.journal','ab').write(b'{')",
                  "p='/state/control/domain.journal'; b=bytearray(open(p,'rb').read()); b[20]^=1; open(p,'wb').write(b)"]
        for fault in faults:
            with self.subTest(fault=fault), fixture() as f:
                f.fault("import os; " + fault)
                self.assertEqual("RecoveryRequired", f.rpc("client", "put")["code"])
                self.assertEqual("RecoveryRequired", f.close()["code"])
                self.assertEqual([], f.private_files())

    def test_removing_a_legacy_sidecar_does_not_reopen_explicit_closed_state(self):
        with fixture() as f:
            self.assertEqual("ClosedExperimental", f.close()["code"])
            f.fault("p='/state/control/closed'; open(p,'w').write('legacy'); import os; os.unlink(p)")
            self.assertEqual("Denied", f.rpc("client", "put")["code"])
            self.assertEqual([], f.private_files())

    def test_paused_put_holds_lock_through_eof_and_data_fds_are_cloexec(self):
        for area in ("candidate", "promotion"):
            for index, kind in enumerate(("thumb", "card", "full")):
                with self.subTest(area=area, kind=kind), fixture(area) as f:
                    f.restart(point="eof", variant=index)
                    with concurrent.futures.ThreadPoolExecutor() as pool:
                        pending = pool.submit(f.rpc, "client", "put", 98304, variant=kind)
                        f.wait_barrier()
                        try:
                            for _ in range(2):
                                self.assertEqual("Busy", f.close()["code"])
                                self.assertEqual("Busy", f.rpc("client", "put")["code"])
                            pid = f.processes["authority"].remote_pid
                            result = f.actor("authority", ["python3", "-c", "import os,pathlib,json,sys; pid=sys.argv[1]; seen={};\nfor p in pathlib.Path('/proc/'+pid+'/fd').iterdir():\n try: t=os.readlink(p)\n except FileNotFoundError: continue\n if t.endswith('/domain.lock') or t.endswith('.partial'):\n  flags=next(x.split()[1] for x in pathlib.Path('/proc/'+pid+'/fdinfo/'+p.name).read_text().splitlines() if x.startswith('flags:')); seen['lock' if t.endswith('domain.lock') else 'data']=bool(int(flags,8)&os.O_CLOEXEC)\nprint(json.dumps(seen))", str(pid)])
                            self.assertEqual({"lock": True, "data": True}, json.loads(result.stdout))
                        finally:
                            f.release_barrier()
                        self.assertEqual("Written", pending.result()["code"])
                    self.assertEqual("ClosedExperimental", f.close()["code"])
                    self.assertEqual("SweptExperimental", f.sweep()["code"])
                    self.assertEqual([], f.private_files())

    def test_sigkill_before_or_after_put_recovers_without_writer_ack(self):
        for area in ("candidate", "promotion"):
            for point in ("before-put", "eof", "after-put"):
                with self.subTest(area=area, point=point), fixture(area) as f:
                    f.restart(point=point)
                    with concurrent.futures.ThreadPoolExecutor() as pool:
                        pending = pool.submit(f.rpc, "client", "put", 98304)
                        f.wait_barrier()
                        f.kill_server("authority")
                        with self.assertRaises(subprocess.CalledProcessError):
                            pending.result()
                    f.start_server("authority")
                    self.assertEqual("ClosedExperimental", f.close()["code"])
                    self.assertEqual("SweptExperimental", f.sweep()["code"])
                    self.assertEqual([], f.private_files())

    def test_close_process_crash_recovers_each_durable_transition(self):
        for area in ("candidate", "promotion"):
            for point in ("closing-synced", "witness-confirmed", "closed-synced"):
                with self.subTest(area=area, point=point), fixture(area) as f:
                    f.restart(point=point)
                    with concurrent.futures.ThreadPoolExecutor() as pool:
                        pending = pool.submit(f.close)
                        f.wait_barrier()
                        f.kill_server("authority")
                        with self.assertRaises(subprocess.CalledProcessError):
                            pending.result()
                    f.start_server("authority")
                    self.assertEqual("Denied", f.rpc("client", "put")["code"])
                    first = f.close()
                    self.assertEqual("ClosedExperimental", first["code"])
                    self.assertEqual(first, f.close())

    def test_unavailable_or_corrupt_controller_never_acknowledges_closed(self):
        with fixture() as f:
            f.kill_server("controller")
            self.assertEqual("AdmissionBlocked", f.rpc("client", "put")["code"])
            self.assertEqual("RecoveryRequired", f.close()["code"])
            self.assertEqual([], f.private_files())
            f.start_server("controller")
            self.assertEqual("ClosedExperimental", f.close()["code"])
        with fixture() as f:
            f.fault("open('/witness-state/control/domain.journal','ab').write(b'{')")
            self.assertEqual("AdmissionBlocked", f.rpc("client", "put")["code"])
            self.assertEqual("RecoveryRequired", f.close()["code"])

    def test_witness_response_lost_is_recovered_idempotently(self):
        with fixture() as f:
            f.restart("controller", point="witness-synced")
            with concurrent.futures.ThreadPoolExecutor() as pool:
                pending = pool.submit(f.close)
                f.wait_barrier("controller")
                f.kill_server("authority")
                with self.assertRaises(subprocess.CalledProcessError):
                    pending.result()
                f.release_barrier("controller")
            f.start_server("authority")
            reply = f.close()
            self.assertEqual("ClosedExperimental", reply["code"])
            self.assertEqual(reply, f.close())

    def test_controller_sigkill_after_persistence_does_not_create_a_false_ack(self):
        with fixture() as f:
            f.restart("controller", point="witness-synced")
            with concurrent.futures.ThreadPoolExecutor() as pool:
                pending = pool.submit(f.close)
                f.wait_barrier("controller")
                f.kill_server("controller")
                self.assertEqual("RecoveryRequired", pending.result()["code"])
            self.assertEqual("Denied", f.rpc("client", "put")["code"])
            f.start_server("controller")
            first = f.close()
            self.assertEqual("ClosedExperimental", first["code"])
            self.assertEqual(first, f.close())

    def test_sigkill_during_each_delete_allows_only_repeatable_sweep(self):
        for area in ("candidate", "promotion"):
            for index in range(3):
                with self.subTest(area=area, variant=index), fixture(area) as f:
                    for kind in ("thumb", "card", "full"):
                        self.assertEqual("Written", f.rpc("client", "put", variant=kind)["code"])
                    self.assertEqual("ClosedExperimental", f.close()["code"])
                    f.restart(point="after-delete", variant=index)
                    with concurrent.futures.ThreadPoolExecutor() as pool:
                        pending = pool.submit(f.sweep)
                        f.wait_barrier()
                        f.kill_server("authority")
                        with self.assertRaises(subprocess.CalledProcessError):
                            pending.result()
                    f.start_server("authority")
                    self.assertEqual("SweptExperimental", f.sweep()["code"])
                    self.assertEqual([], f.private_files())
                    self.assertEqual("Denied", f.rpc("client", "put")["code"])

    def test_retry_resynchronizes_existing_closed_records_before_ack(self):
        for role in ("authority", "controller"):
            with self.subTest(role=role), fixture() as f:
                self.assertEqual("ClosedExperimental", f.close()["code"])
                f.restart(role, point="fail-recovery-sync")
                self.assertEqual("RecoveryRequired", f.close()["code"])
                f.restart(role)
                self.assertEqual("ClosedExperimental", f.close()["code"])

    def test_sweep_preserves_neighbor_committed_and_unknown_partial(self):
        with fixture() as f:
            neighbor = f.grants[1]
            self.assertEqual("Written", f.rpc("client", "put", grant=neighbor)["code"])
            self.assertEqual("Written", f.rpc("client", "put")["code"])
            committed = next(g for g in f.manifest["generations"] if g["area"] == f.area and g["committed"])
            committed_path = "/state/data/review-candidates/" + committed["generation"] + "-thumb"
            f.actor("authority", ["python3", "-c", "import sys; open(sys.argv[1],'wb').write(b'committed-sentinel')", committed_path])
            before = f.inspect(committed_path, actor="authority")
            grant = dict(f.active, generation=committed["generation"], nonce=committed["nonce"])
            self.assertEqual("Denied", f.rpc("authority", "close", grant=grant, body_length=0)["code"])
            self.assertEqual("Denied", f.rpc("client", "put", grant=grant)["code"])
            prefix = "/state/data/review-candidates/" + f.active["generation"] + "-thumb"
            f.actor("authority", ["python3", "-c", "import sys; open(sys.argv[1]+'.unknown.partial','wb').write(b'unknown'); open(sys.argv[1]+'.'+'a'*32+'.partial','wb').write(b'orphan')", prefix])
            self.assertEqual("ClosedExperimental", f.close()["code"])
            self.assertEqual("SweptExperimental", f.sweep()["code"])
            self.assertEqual(before, f.inspect(committed_path, actor="authority"))
            self.assertEqual(1, len(f.private_files(neighbor)))
            self.assertEqual([prefix.removeprefix('/state/data/') + '.unknown.partial'], f.private_files())

    def test_failed_sync_or_delete_does_not_return_sweep_success(self):
        with fixture() as f:
            self.assertEqual("Written", f.rpc("client", "put")["code"])
            f.restart(point="fail-closing-sync")
            self.assertEqual("RecoveryRequired", f.close()["code"])
            f.restart()
            self.assertEqual("ClosedExperimental", f.close()["code"])
            # A directory at a final-object name causes a real File.Delete failure, without
            # failing the earlier directory-mode validation instead of reaching DELETE.
            path = "/state/data/" + f.private_files()[0]
            f.actor("authority", ["python3", "-c", "import os,sys; os.unlink(sys.argv[1]); os.mkdir(sys.argv[1])", path])
            self.assertEqual("RecoveryRequired", f.sweep()["code"])
            f.actor("authority", ["python3", "-c", "import os,sys; os.rmdir(sys.argv[1])", path])
            f.restart(point="fail-removal-sync")
            self.assertEqual("RecoveryRequired", f.sweep()["code"])
            f.restart()
            self.assertEqual("SweptExperimental", f.sweep()["code"])
            self.assertEqual([], f.private_files())


if __name__ == "__main__":
    unittest.main(verbosity=2)
