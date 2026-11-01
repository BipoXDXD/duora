"""Kernel denial and authenticated real FileSystem PUT, not mock storage."""
import unittest
from fixture import fixture


class BoundaryTests(unittest.TestCase):
    def test_legacy_cannot_unlink_control_with_writable_mount(self):
        with fixture(servers=False) as f:
            result = f.file_attack("legacy", "unlink", "domain.lock")
            self.assertIn(result["errno"], {"EACCES", "EPERM"})
            self.assertEqual(result["before"], result["after"])

    def test_control_syscalls_are_denied_without_mutation(self):
        with fixture(servers=False) as f:
            for actor in ("legacy", "client"):
                for target in ("domain.lock", "domain.journal"):
                    for operation in ("unlink", "rename", "truncate", "overwrite", "chmod"):
                        with self.subTest(actor=actor, target=target, operation=operation):
                            result = f.file_attack(actor, operation, target)
                            self.assertIn(result["errno"], {"EACCES", "EPERM"})
                            self.assertEqual(result["before"], result["after"])

    def test_authority_cannot_replace_lock_name(self):
        with fixture(servers=False) as f:
            for operation in ("unlink", "rename", "chmod"):
                result = f.file_attack("authority", operation, "domain.lock")
                self.assertIn(result["errno"], {"EACCES", "EPERM"})
                self.assertEqual(result["before"], result["after"])

    def test_actors_have_no_capabilities_or_authority_group(self):
        with fixture(servers=False) as f:
            for actor, uid in (("legacy", 1654), ("client", 17020), ("authority", 17010), ("controller", 17030)):
                actual = f.call(actor, {"mode": "identity"})
                self.assertEqual(uid, actual["uid"])
                self.assertEqual(0, int(actual["cap_eff"], 16))
                self.assertEqual("1", actual["no_new_privs"])
                if actor in ("legacy", "client"):
                    self.assertNotIn(17010, actual["groups"])
                    self.assertNotIn(17030, actual["groups"])

    def test_authorized_client_writes_real_adapter_bytes(self):
        with fixture() as f:
            self.assertEqual("Written", f.rpc("client", "put")["code"])
            files = f.private_files()
            self.assertEqual(1, len(files))
            expected = "/state/data/" + files[0]
            actual = f.actor("authority", ["python3", "-c", "import json,sys; print(json.dumps(list(open(sys.argv[1],'rb').read())))", expected])
            import json
            self.assertEqual([1, 2, 3], json.loads(actual.stdout))

    def test_legacy_cannot_use_a_valid_client_grant_or_spoof_uid(self):
        with fixture() as f:
            self.assertEqual("Denied", f.rpc("legacy", "put")["code"])
            self.assertEqual("Denied", f.rpc("legacy", "put", request_overrides={"uid": 17020})["code"])
            self.assertEqual([], f.private_files())
            self.assertEqual("Written", f.rpc("client", "put")["code"])

    def test_invalid_requests_create_no_temporary_or_final_bytes(self):
        import copy
        with fixture() as f:
            variants = [dict(request_overrides={"version": 1}),
                        dict(request_overrides={"operation": "reserve"}),
                        dict(request_overrides={"variant": "../full"}),
                        dict(request_overrides={"length": -1}, body_length=0),
                        dict(request_overrides={"length": 98305}, body_length=0),
                        dict(frame_length=4097, body_length=0)]
            for field, value in (("area", "quarantine"), ("nonce", "0" * 64), ("generation", "0" * 32), ("generation", 123)):
                grant = copy.deepcopy(f.active)
                grant[field] = value
                variants.append(dict(grant=grant))
            for kwargs in variants:
                with self.subTest(kwargs=list(kwargs)):
                    self.assertEqual("Denied", f.rpc("client", "put", **kwargs)["code"])
                    self.assertEqual([], f.private_files())

    def test_raw_legacy_cannot_create_partial_or_final_in_private_area(self):
        with fixture(servers=False) as f:
            for suffix in ("", "." + "d" * 32 + ".partial"):
                path = "/state/data/review-candidates/" + f.active["generation"] + "-thumb" + suffix
                result = f.call("legacy", dict(mode="attack", operation="create", target=path))
                self.assertIn(result["errno"], {"EACCES", "EPERM"})
            self.assertEqual([], f.private_files())

    def test_witness_file_is_not_writable_by_authority_or_clients(self):
        with fixture(servers=False) as f:
            path = "/witness-state/control/domain.journal"
            before = f.inspect(path)
            for actor in ("legacy", "client", "authority"):
                self.assertIn(f.call(actor, dict(mode="attack", operation="overwrite", target=path))["errno"], {"EACCES", "EPERM"})
                self.assertEqual(before, f.inspect(path))

    def test_controller_authenticates_kernel_peer_even_with_a_valid_grant(self):
        with fixture() as f:
            request = dict(version=2, operation="close", grant=f.active, variant="thumb", length=0)
            for actor in ("legacy", "client", "controller"):
                reply = f.call(actor, dict(mode="rpc", socket="/state/rpc/controller/service.sock", request=request))
                self.assertEqual("Denied", reply["status"])
            self.assertEqual("Written", f.rpc("client", "put")["code"])

    def test_truncated_and_padded_bodies_do_not_publish(self):
        with fixture() as f:
            for length in (2, 4):
                self.assertEqual("RecoveryRequired", f.rpc("client", "put", body_length=length,
                                 request_overrides={"length": 3})["code"])
                self.assertEqual([], f.private_files())

    def test_missing_changed_linked_or_replaced_control_is_rejected(self):
        faults = [
            "os.unlink('/state/control/domain.lock')",
            "os.chmod('/state/control/domain.lock',0o666)",
            "os.chown('/state/control/domain.lock',1654,1654)",
            "os.link('/state/control/domain.lock','/state/control/extra')",
            "os.rename('/state/control/domain.lock','/state/control/old'); open('/state/control/domain.lock','wb').close(); os.chmod('/state/control/domain.lock',0o660); os.chown('/state/control/domain.lock',0,17010)",
            "os.rename('/state/control/domain.lock','/state/control/old'); os.symlink('old','/state/control/domain.lock')",
            "os.rename('/state/control','/state/old'); os.symlink('old','/state/control')",
        ]
        for fault in faults:
            with self.subTest(fault=fault), fixture(servers=False) as f:
                f.fault("import os; " + fault)
                with self.assertRaises(AssertionError):
                    f.start_server("authority")
                self.assertEqual([], f.private_files())


if __name__ == "__main__":
    unittest.main(verbosity=2)
