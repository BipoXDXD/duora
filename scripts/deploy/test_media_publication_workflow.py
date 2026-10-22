"""Behavioral scheduling checks for the publication trust boundary; no job execution."""
from __future__ import annotations

import ast
from pathlib import Path
from typing import Any
import unittest

import yaml

WORKFLOW = Path(__file__).resolve().parents[2] / ".github/workflows/media-release.yml"


def expression(value: object, github: dict[str, str]) -> Any:
    """Evaluate only the boolean/context subset used in these job/checkout expressions."""
    if not isinstance(value, str) or not value.startswith("${{"):
        return value
    tree = ast.parse(value[3:-2].strip().replace("&&", " and ").replace("||", " or "), mode="eval")

    def evaluate(node: ast.AST) -> Any:
        if isinstance(node, ast.Expression):
            return evaluate(node.body)
        if isinstance(node, ast.Constant) and type(node.value) in (str, bool):
            return node.value
        if isinstance(node, ast.Attribute) and isinstance(node.value, ast.Name) and node.value.id == "github":
            return github[node.attr]
        if isinstance(node, ast.BoolOp):
            result = evaluate(node.values[0])
            for item in node.values[1:]:
                if isinstance(node.op, ast.And) and not result or isinstance(node.op, ast.Or) and result:
                    break
                result = evaluate(item)
            return result
        if isinstance(node, ast.Compare) and len(node.ops) == 1 and isinstance(node.ops[0], ast.Eq):
            return evaluate(node.left) == evaluate(node.comparators[0])
        raise AssertionError("Unsupported publication policy expression; review the trust model.")

    return evaluate(tree)


class PublicationWorkflowTests(unittest.TestCase):
    def setUp(self):
        self.workflow = yaml.safe_load(WORKFLOW.read_text())
        self.jobs = self.workflow["jobs"]
        self.context = {"event_name": "workflow_dispatch", "ref": "refs/heads/main",
                        "repository": "BipoXDXD/duora", "sha": "a" * 40}

    def eligible(self, name: str) -> bool:
        # GitHub's default success condition admits a job when its dependencies succeeded.
        return bool(expression(self.jobs[name].get("if", True), self.context))

    def checkout(self) -> str:
        step = next(step for step in self.jobs["verify"]["steps"]
                    if str(step.get("uses", "")).startswith("actions/checkout@"))
        return expression(step.get("with", {}).get("ref", self.context["sha"]), self.context)

    def test_main_dispatch_can_run_but_feature_dispatch_cannot_execute_any_tooling(self):
        self.assertTrue(all(self.eligible(name) for name in self.jobs))
        for ref in ("refs/heads/unreviewed", "refs/tags/unreviewed"):
            self.context["ref"] = ref
            with self.subTest(ref=ref):
                self.assertFalse(any(self.eligible(name) for name in self.jobs))

    def test_dispatch_from_another_repository_cannot_execute_or_publish(self):
        self.context["repository"] = "attacker/duora"
        self.assertFalse(any(self.eligible(name) for name in self.jobs))

    def test_dispatch_validation_selects_trusted_main_even_if_its_guard_regresses(self):
        self.context.update(ref="refs/heads/unreviewed", sha="b" * 40)
        self.assertEqual("refs/heads/main", self.checkout())

    def test_pr_tests_use_pr_code_and_have_no_secret_bearing_job_or_write_token(self):
        self.context.update(event_name="pull_request", ref="refs/pull/202/merge", sha="c" * 40)
        self.assertTrue(self.eligible("verify"))
        self.assertFalse(self.eligible("native"))
        self.assertFalse(self.eligible("indexes"))
        self.assertEqual(self.context["sha"], self.checkout())
        permissions = {**self.workflow["permissions"], **self.jobs["verify"].get("permissions", {})}
        self.assertTrue(all(value == "read" for value in permissions.values()))
