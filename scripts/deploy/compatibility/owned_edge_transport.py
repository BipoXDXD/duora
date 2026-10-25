"""Pure selection of the owned internal edge endpoint; no sockets, Docker or native initialization."""
import ipaddress
import re
from typing import Any

LABEL = "io.duora.current-schema-lab"


def need(condition: object, message: str) -> None:
    if not condition:
        raise ValueError(message)


def validate_transport(record: dict) -> None:
    need(set(record) == {"kind", "address", "port", "server_name", "http_host", "origin", "container_id", "network_id"}, "exact direct HTTPS evidence shape")
    need(record["kind"] == "owned_internal_bridge_https" and type(record["port"]) is int and record["port"] == 443
         and record["server_name"] == record["http_host"] == "localhost" and record["origin"] == "https://localhost", "fixed TLS/HTTP/origin identity required")
    for name in ("container_id", "network_id"):
        need(type(record[name]) is str and re.fullmatch(r"[a-f0-9]{64}", record[name]), "known owned resource ID required")
    need(type(record["address"]) is str, "literal IPv4 address required")
    address = ipaddress.IPv4Address(record["address"])
    need(address.is_private and not (address.is_loopback or address.is_link_local or address.is_unspecified or address.is_multicast or address.is_reserved), "private bridge endpoint required")


def select_owned_edge(container: dict[str, Any], network: dict[str, Any], *, nonce: str, name: str, network_name: str, reference: str, image_id: str) -> dict:
    need(re.fullmatch(r"[a-f0-9]{32}", nonce) and re.fullmatch(r"[a-f0-9]{64}", container["Id"]), "known own nonce/CID required")
    need(container["Name"] == "/" + name and container["Config"]["Labels"].get(LABEL) == nonce
         and container["Config"]["Image"] == reference and container["Image"] == image_id
         and re.fullmatch(r"ghcr\.io/bipoxdxd/duora-edge@sha256:[a-f0-9]{64}", reference)
         and re.fullmatch(r"sha256:[a-f0-9]{64}", image_id), "foreign edge provenance or identity")
    need(container["State"]["Status"] == "running" and container["State"]["Running"] is True
         and container["State"]["OOMKilled"] is False and type(container["RestartCount"]) is int and container["RestartCount"] == 0,
         "edge must be the original running owned instance")
    need(container["HostConfig"].get("PortBindings") in (None, {}), "direct internal edge must not request published ports")
    need(network["Name"] == network_name and network["Driver"] == "bridge" and network["Internal"] is True
         and network["EnableIPv6"] is False and network["Labels"].get(LABEL) == nonce
         and re.fullmatch(r"[a-f0-9]{64}", network["Id"]), "exact owned internal IPv4 bridge required")
    endpoints = container["NetworkSettings"]["Networks"]
    need(set(endpoints) == {network_name}, "one owned network endpoint only")
    endpoint = endpoints[network_name]
    need(endpoint["NetworkID"] == network["Id"] and type(endpoint["IPAddress"]) is str, "edge endpoint must bind actual owned bridge ID")
    configs: list[dict[str, Any]] = network["IPAM"]["Config"]
    need(type(configs) is list and len(configs) == 1, "one exact IPv4 IPAM subnet required")
    subnet = ipaddress.IPv4Network(configs[0]["Subnet"], strict=True)
    address = ipaddress.IPv4Address(endpoint["IPAddress"])
    need(subnet.network_address.is_private and subnet.broadcast_address.is_private and address in subnet
         and address not in {subnet.network_address, subnet.broadcast_address, ipaddress.IPv4Address(configs[0]["Gateway"])}, "edge address outside its private IPAM subnet")
    member = network["Containers"][container["Id"]]
    need(member["Name"] == name and member["IPv4Address"] == str(address) + "/" + str(subnet.prefixlen), "bridge membership does not bind the same CID/address")
    record = {"kind": "owned_internal_bridge_https", "address": str(address), "port": 443, "server_name": "localhost",
              "http_host": "localhost", "origin": "https://localhost", "container_id": container["Id"], "network_id": network["Id"]}
    validate_transport(record)
    return record
