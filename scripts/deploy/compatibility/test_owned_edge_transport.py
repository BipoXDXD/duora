"""New synthetic endpoint guards only; never native networking evidence."""
import copy
import unittest
from owned_edge_transport import select_owned_edge, validate_transport

def fixture():
    cid, nid, nonce = "a" * 64, "b" * 64, "c" * 32
    ref = "ghcr.io/bipoxdxd/duora-edge@sha256:" + "d" * 64
    kwargs = dict(nonce=nonce, name="owned-edge", network_name="owned-net", reference=ref, image_id="sha256:" + "e" * 64)
    container = {"Id": cid, "Name": "/owned-edge", "Image": kwargs["image_id"],
                 "Config": {"Image": ref, "Labels": {"io.duora.current-schema-lab": nonce}},
                 "State": {"Status": "running", "Running": True, "OOMKilled": False}, "RestartCount": 0,
                 "HostConfig": {"PortBindings": {}}, "NetworkSettings": {"Networks": {"owned-net": {"NetworkID": nid, "IPAddress": "172.18.0.4"}}}}
    network = {"Id": nid, "Name": "owned-net", "Driver": "bridge", "Internal": True, "EnableIPv6": False,
               "Labels": {"io.duora.current-schema-lab": nonce}, "IPAM": {"Config": [{"Subnet": "172.18.0.0/16", "Gateway": "172.18.0.1"}]},
               "Containers": {cid: {"Name": "owned-edge", "IPv4Address": "172.18.0.4/16"}}}
    return container, network, kwargs

class OwnedEdgeTransportTests(unittest.TestCase):
    def test_only_owned_private_edge_is_selected_with_coherent_tls_identity(self):
        c, n, kw = fixture(); value = select_owned_edge(c, n, **kw)
        self.assertEqual(value, {"kind": "owned_internal_bridge_https", "address": "172.18.0.4", "port": 443,
                                "server_name": "localhost", "http_host": "localhost", "origin": "https://localhost",
                                "container_id": c["Id"], "network_id": n["Id"]})
        validate_transport(value)

    def test_foreign_container_nonce_name_image_or_state_refused(self):
        for change in (lambda c: c.__setitem__("Id", "foreign"), lambda c: c.__setitem__("Name", "/foreign"),
                       lambda c: c.__setitem__("Image", "sha256:" + "f" * 64),
                       lambda c: c["Config"]["Labels"].clear(), lambda c: c["Config"].__setitem__("Image", "mutable:tag"),
                       lambda c: c["State"].__setitem__("Running", False), lambda c: c["State"].__setitem__("OOMKilled", True),
                       lambda c: c.__setitem__("RestartCount", 1)):
            c, n, kw = fixture(); change(c)
            with self.assertRaises((ValueError, KeyError, TypeError)): select_owned_edge(c, n, **kw)

    def test_foreign_network_noninternal_or_ambiguous_endpoint_refused(self):
        for change in (lambda n: n.__setitem__("Internal", False), lambda n: n.__setitem__("Id", "f" * 64),
                       lambda n: n["Labels"].clear(), lambda n: n.__setitem__("Driver", "host"),
                       lambda n: n.__setitem__("EnableIPv6", True), lambda n: n["Containers"].clear()):
            c, n, kw = fixture(); change(n)
            with self.assertRaises((ValueError, KeyError, TypeError)): select_owned_edge(c, n, **kw)
        c, n, kw = fixture(); c["NetworkSettings"]["Networks"]["foreign"] = {}
        with self.assertRaises(ValueError): select_owned_edge(c, n, **kw)

    def test_dns_public_loopback_gateway_or_subnet_mismatch_refused(self):
        for address in ("localhost", "8.8.8.8", "127.0.0.1", "0.0.0.0", "172.18.0.1", "172.19.0.4", "172.18.255.255", "::1"):
            c, n, kw = fixture(); c["NetworkSettings"]["Networks"]["owned-net"]["IPAddress"] = address
            with self.assertRaises((ValueError, KeyError, TypeError)): select_owned_edge(c, n, **kw)
        c, n, kw = fixture(); n["Containers"][c["Id"]]["IPv4Address"] = "172.18.0.99/16"
        with self.assertRaises(ValueError): select_owned_edge(c, n, **kw)

    def test_published_ports_or_weakened_tls_evidence_refused(self):
        c, n, kw = fixture(); c["HostConfig"]["PortBindings"] = {"443/tcp": [{"HostIp": "127.0.0.1", "HostPort": "12345"}]}
        with self.assertRaises(ValueError): select_owned_edge(c, n, **kw)
        c, n, kw = fixture(); value = select_owned_edge(c, n, **kw)
        for field, bad in (("server_name", "foreign"), ("origin", "http://localhost"), ("http_host", "foreign"), ("port", 80),
                           ("address", "public.example"), ("kind", "published_loopback")):
            changed = copy.deepcopy(value); changed[field] = bad
            with self.assertRaises(ValueError): validate_transport(changed)

if __name__ == "__main__": unittest.main()
