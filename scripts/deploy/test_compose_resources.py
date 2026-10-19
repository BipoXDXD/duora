"""Orçamento de CPU e memória do compose.prod.yml para a VPS de produção (issue #86, spec 15.1).

A VPS é uma Hostinger KVM 2: 2 vCPU e 8 GB. A tabela com os valores e o porquê fica em docs/operations/deploy.md
("Orçamento de CPU e memória"); PyYAML já faz parte das ferramentas fixadas do job ops.
"""

import unittest
from pathlib import Path

import yaml

ROOT = Path(__file__).resolve().parents[2]

HOST_VCPUS = 2.0
HOST_MEMORY_MIB = 8 * 1024
# Spec 15.1: cerca de 2 GiB para o sistema, o cache de página e a folga, e o OpenTelemetry Collector (256 MiB), que
# ainda não é um serviço deste Compose. O que sobra é o teto da soma dos mem_limit que rodam juntos.
HOST_RESERVE_MIB = 2 * 1024
COLLECTOR_MIB = 256
COMPOSE_MEMORY_BUDGET_MIB = HOST_MEMORY_MIB - HOST_RESERVE_MIB - COLLECTOR_MIB

# Peso relativo sob contenção (cpu_shares): o caminho da requisição primeiro, o processamento em segundo plano
# depois e o backup por último. O limite (cpus) é teto, não reserva; quem decide a disputa é o peso.
REQUEST_PATH = ("postgres", "web", "edge")
BACKGROUND = ("worker", "backup")


def _mebibytes(value: str) -> int:
    units = {"M": 1, "G": 1024}
    number, unit = value[:-1], value[-1].upper()
    if unit not in units:
        raise ValueError(f"memória sem unidade M ou G: {value}")
    return int(float(number) * units[unit])


class ProductionResourceBudgetTests(unittest.TestCase):
    def setUp(self) -> None:
        compose = yaml.safe_load((ROOT / "infra/compose/compose.prod.yml").read_text(encoding="utf-8"))
        self.services: dict[str, dict] = compose["services"]
        self.permanent = {name for name, service in self.services.items() if "profiles" not in service}

    def limits(self, name: str) -> dict:
        return self.services[name]["deploy"]["resources"]["limits"]

    def memory(self, names: set[str]) -> int:
        return sum(_mebibytes(self.limits(name)["memory"]) for name in names)

    def test_every_service_declares_cpu_memory_and_weight(self) -> None:
        for name, service in self.services.items():
            with self.subTest(service=name):
                limits = self.limits(name)
                self.assertIn("cpus", limits)
                self.assertIn("memory", limits)
                # Sem peso explícito o serviço fica com o padrão (1024) e passa à frente do worker e do backup.
                self.assertIn("cpu_shares", service)
                # Reserva de CPU fora do Swarm é ignorada pelo Compose (medido: nem CpuShares nem cpu.weight mudam).
                self.assertNotIn("reservations", service["deploy"]["resources"])

    def test_no_cpu_limit_exceeds_the_host(self) -> None:
        for name in self.services:
            with self.subTest(service=name):
                self.assertLessEqual(float(self.limits(name)["cpus"]), HOST_VCPUS)

    def test_no_permanent_service_can_take_the_whole_host(self) -> None:
        # Pelo menos meio vCPU sempre livre para o sistema, o sshd e o Docker, mesmo com um serviço em laço.
        for name in self.permanent:
            with self.subTest(service=name):
                self.assertLessEqual(float(self.limits(name)["cpus"]), HOST_VCPUS - 0.5)

    def test_request_path_outweighs_background_work_under_contention(self) -> None:
        lightest_request = min(self.services[name]["cpu_shares"] for name in REQUEST_PATH)
        for name in BACKGROUND:
            with self.subTest(service=name):
                self.assertGreater(lightest_request, self.services[name]["cpu_shares"])
        self.assertGreater(self.services["worker"]["cpu_shares"], self.services["backup"]["cpu_shares"])
        self.assertLess(float(self.limits("worker")["cpus"]), float(self.limits("web")["cpus"]))
        self.assertLess(float(self.limits("backup")["cpus"]), float(self.limits("postgres")["cpus"]))

    def test_memory_of_what_runs_together_fits_the_budget(self) -> None:
        # O que roda junto em cada momento (docs/operations/deploy.md e backup-restore.md): no deploy o worker para
        # antes do migrador; no restore web, worker, edge, postgres e backup param antes do job.
        scenarios = {
            "permanentes": self.permanent,
            "migração": (self.permanent - {"worker"}) | {"migrator"},
            "restore": {"restore"},
        }
        for scenario, names in scenarios.items():
            with self.subTest(scenario=scenario):
                self.assertLessEqual(self.memory(names), COMPOSE_MEMORY_BUDGET_MIB)

    def test_every_profile_service_has_a_memory_scenario(self) -> None:
        # Serviço novo sob profile precisa entrar num cenário acima antes de ir ao ar.
        self.assertEqual({"migrator", "restore"}, set(self.services) - self.permanent)
        self.assertEqual({"postgres", "backup", "web", "worker", "edge"}, self.permanent)


if __name__ == "__main__":
    unittest.main()
