"""Contrato estático da KVM 2; executado pelo discover do job ops, sem Docker/segredos.

PyYAML já está fixado no requirements.txt de operações (dependência do Ansible).
Não comprova que o runtime aplica limites nem que a VPS suporta a carga.
"""
from __future__ import annotations

import math
import re
import unittest
from pathlib import Path
from typing import Any

import yaml

REPO = Path(__file__).resolve().parents[2]
HOST_BYTES = 8_000_000_000  # Conservador: 8 GB anunciados não são necessariamente 8 GiB.
HOST_HEADROOM = 1536 * 1024**2
HOST_CPUS = 2


def memory_bytes(value: Any) -> int:
    if isinstance(value, int) and not isinstance(value, bool):
        return value
    match = re.fullmatch(r"(\d+)([bkmg])", str(value), re.IGNORECASE)
    if match is None:
        raise ValueError(f"unidade de memória não suportada: {value!r}")
    return int(match[1]) * 1024 ** "bkmg".index(match[2].lower())


class CapacityBudgetTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.services: dict[str, Any] = yaml.safe_load(
            (REPO / "infra/compose/compose.prod.yml").read_text()
        )["services"]

    def test_every_service_has_finite_individual_limits_within_host_capacity(self) -> None:
        for name, service in self.services.items():
            with self.subTest(service=name):
                limits = service["deploy"]["resources"]["limits"]
                cpu = float(limits["cpus"])
                self.assertTrue(math.isfinite(cpu) and 0 < cpu <= HOST_CPUS)
                self.assertGreater(memory_bytes(limits["memory"]), 0)
                self.assertLessEqual(memory_bytes(limits["memory"]), HOST_BYTES - HOST_HEADROOM)
                self.assertGreater(service["cpu_shares"], 0)
                # O contrato é por instância; escalar silenciosamente invalida a soma de memória.
                self.assertEqual(service.get("scale", 1), 1)
                self.assertEqual(service["deploy"].get("replicas", 1), 1)
                for alias, expected in (("cpus", cpu), ("mem_limit", memory_bytes(limits["memory"]))):
                    if alias in service:
                        actual = float(service[alias]) if alias == "cpus" else memory_bytes(service[alias])
                        self.assertEqual(actual, expected)

    def test_memory_fits_steady_deploy_and_restore_with_host_headroom(self) -> None:
        permanent = {name for name, service in self.services.items() if not service.get("profiles")}
        self.assertTrue({"postgres", "web", "worker", "backup", "edge"} <= permanent)
        profiled = set(self.services) - permanent
        self.assertEqual(profiled, {"restore", "migrator"}, "perfil novo exige orçamento explícito")
        self.assertEqual(self.services["restore"]["profiles"], ["restore"])
        self.assertEqual(self.services["migrator"]["profiles"], ["migrate"])
        # Deploy conservador: conta até worker, embora o deploy o pare antes de migrar.
        # Restore: postgres/web/worker parados; backup/edge podem ainda existir.
        scenarios = (permanent, permanent | {"migrator"}, (permanent - {"postgres", "web", "worker"}) | {"restore"})
        for services in scenarios:
            with self.subTest(services=sorted(services)):
                total = sum(memory_bytes(self.services[name]["deploy"]["resources"]["limits"]["memory"])
                            for name in services)
                self.assertLessEqual(total + HOST_HEADROOM, HOST_BYTES)
        # Não somar CPU como reserva: oversubscription dos tetos é permitida.

    def test_foreground_cpu_priority_does_not_starve_timed_worker_or_wal(self) -> None:
        weights = {name: service["cpu_shares"] for name, service in self.services.items()}
        self.assertEqual(weights["postgres"], weights["web"])
        self.assertEqual(weights["web"], weights["edge"])
        self.assertGreater(weights["edge"], weights["worker"])
        self.assertGreater(weights["worker"], weights["backup"])
        self.assertGreaterEqual(weights["backup"], 512)

    def test_consolidated_cpu_limits(self) -> None:
        for name in ("postgres", "web"):
            self.assertEqual(float(self.services[name]["deploy"]["resources"]["limits"]["cpus"]), 1.5)

    def test_spec_does_not_restore_four_cpu_assumption(self) -> None:
        spec = (REPO / "docs/product/spec.md").read_text()
        self.assertNotRegex(spec, r"\b4\s+vCPU\b")
        self.assertRegex(spec, r"Hostinger KVM 2[: (]+2 vCPU")


if __name__ == "__main__":
    unittest.main()
