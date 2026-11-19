"""Keep approved DB sources inside every pinned Trivy container; no scan or network."""
from __future__ import annotations

import copy
from itertools import zip_longest
from pathlib import Path
import re
import shlex
from typing import Any
import unittest

import yaml

ROOT = Path(__file__).resolve().parents[2]
WORKFLOWS = {"ci.yml": 4, "publish-images.yml": 3, "qualify-postgres.yml": 3}
MODE_SPLIT_WORKFLOWS = {"qualify-postgres.yml"}
IMAGE = "ghcr.io/aquasecurity/trivy:0.75.0@sha256:af6acf9a6b85dfe389a1941505c0ce9efef52a4719635e1a962f022a3d855daa"
REPOSITORIES = ("ghcr.io/aquasecurity/trivy-db:2", "mirror.gcr.io/aquasec/trivy-db:2")
INVOCATION = re.compile(r'\bdocker\s+run\b(?P<options>[^;]*?)"\$TRIVY_IMAGE"\s+(?P<command>.+)')
OFFLINE_FLAGS = re.compile(
    r"--(?:skip-db-update|skip-update|skip-java-db-update|skip-check-update|offline-scan|"
    r"download-db-only|download-java-db-only)\b|--network(?:=|\s+)['\"]?none\b"
)
FORBIDDEN_SETTINGS = re.compile(
    r"\bTRIVY_(?:SKIP_DB_UPDATE|SKIP_UPDATE|SKIP_JAVA_DB_UPDATE|SKIP_CHECK_UPDATE|OFFLINE_SCAN|"
    r"DOWNLOAD_DB_ONLY|DOWNLOAD_JAVA_DB_ONLY)\b|--(?:db-repository|env-file)\b"
)
PAIR_STEP = "Vulnerabilidades e misconfig (HIGH e CRITICAL bloqueiam)"
PAIR_PATH = "scripts/supply-chain/run_trivy_gate.sh"

# Review this ordered shell sequence with any change to the offline exception. A missing or
# conditional guard, reassigned cache, or masked scanner status must fail the contract.
PAIR_SCRIPT = r'''
set -eo pipefail
reports=$(mktemp -d "$RUNNER_TEMP/supply-chain-report.XXXXXX")
cache=$(mktemp -d "$RUNNER_TEMP/supply-chain-cache.XXXXXX")
echo "SUPPLY_CHAIN_REPORTS=$reports" >> "$GITHUB_ENV"
trivy_fs() {
  local network=$1
  shift
  <scanner>
}
dataset_digests() (
  cd "$cache"
  sha256sum db/trivy.db db/metadata.json
  for dataset in java-db policy; do
    if [[ -d "$dataset" ]]; then
      find "$dataset" -type f -print0 | sort -z | xargs -0 -r sha256sum
    fi
  done
)
trivy_fs bridge --ignorefile /dev/null --exit-code 0 --output /reports/trivy-raw.json
dataset_digests > "$reports/dataset-digests.sha256"
evidence=("$GITHUB_WORKSPACE" "$reports/trivy-raw.json" \
          "$reports/picomatch.openvex.json" "$reports/picomatch-proof.json")
verifier=scripts/supply-chain/check_picomatch_backport.py
python3 "$verifier" emit "${evidence[@]}"
vex_args=()
if [[ -f "$reports/picomatch.openvex.json" ]]; then
  vex_args=(--vex /reports/picomatch.openvex.json)
fi
python3 "$verifier" guard "${evidence[@]}"
dataset_digests | cmp - "$reports/dataset-digests.sha256"
policy_status=0
trivy_fs none --offline-scan --skip-db-update --skip-java-db-update --skip-check-update \
  --ignorefile .trivyignore "${vex_args[@]}" --show-suppressed \
  --exit-code 1 --output /reports/trivy-policy.json || policy_status=$?
dataset_digests | cmp - "$reports/dataset-digests.sha256"
python3 "$verifier" audit "${evidence[@]}" "$reports/trivy-policy.json"
printf '%s\n' "$policy_status" > "$reports/policy-exit-code.txt"
printf 'Trivy policy exit code: %s\n' "$policy_status"
exit "$policy_status"
'''
PAIR_DOCKER_OPTIONS = shlex.split(
    '--rm --network "$network" -v "$GITHUB_WORKSPACE:/repo:ro" -v "$reports:/reports" '
    '-v "$cache:/trivy-cache" -w /repo -e TRIVY_DB_REPOSITORY'
)
PAIR_SCANNER_ARGS = [
    "--config", "", "--cache-dir", "/trivy-cache", "fs", ".", "--scanners", "vuln,misconfig",
    "--severity", "HIGH,CRITICAL", "--include-dev-deps", "--format", "json", "--list-all-pkgs", "$@",
]


def shell_lines(script: str) -> list[str]:
    joined = re.sub(r"\\[ \t]*\n[ \t]*", " ", script)
    return [line for text in joined.splitlines() if (line := text.strip()) and not line.startswith("#")]


def check_filesystem_pair(job: dict[str, Any], step: dict[str, Any], lines: list[str],
                          launch: str, options: list[str], command: list[str]) -> None:
    if (step.get("shell") != "bash" or step.get("if") != "${{ !cancelled() }}"
            or job.get("if") or step.get("continue-on-error") or job.get("continue-on-error")):
        raise AssertionError("filesystem scan pair must run and fail closed under Bash")
    if options != PAIR_DOCKER_OPTIONS or command != PAIR_SCANNER_ARGS:
        raise AssertionError("filesystem scan pair changed its shared scanner, cache, or network")
    actual = ["<scanner>" if line == launch else line for line in lines]
    for found, expected in zip_longest(actual, shell_lines(PAIR_SCRIPT)):
        if found != expected:
            raise AssertionError(f"filesystem scan pair changed: expected {expected!r}, found {found!r}")


def checked_invocations(name: str, workflow: dict[str, Any], expected_count: int,
                        *, pair_script: str | None = None) -> int:
    """Count Docker launch sites and verify the two executions of the shared filesystem site."""
    seen = 0
    pairs = 0
    for job_name, job in workflow["jobs"].items():
        for step in job.get("steps", []):
            script = step.get("run", "")
            pair = name == "ci.yml" and job_name == "supply-chain" and step.get("name") == PAIR_STEP
            if pair:
                if script != f"bash {PAIR_PATH}":
                    raise AssertionError("filesystem scan pair entry point changed")
                script = (ROOT / PAIR_PATH).read_text() if pair_script is None else pair_script
            if not isinstance(script, str):
                continue
            lines = shell_lines(script)
            launches = [(line, match) for line in lines if (match := INVOCATION.search(line))]
            if sum(line.count('"$TRIVY_IMAGE"') for line in lines) != len(launches):
                raise AssertionError("unrecognized Trivy container launch")
            if not launches:
                continue
            environment = {**workflow.get("env", {}), **job.get("env", {}), **step.get("env", {})}
            if environment.get("TRIVY_IMAGE") != IMAGE:
                raise AssertionError("pinned scanner changed")
            repositories = tuple(str(environment.get("TRIVY_DB_REPOSITORY", "")).split(","))
            if repositories != REPOSITORIES:
                raise AssertionError("official DB source priority/schema changed")
            if FORBIDDEN_SETTINGS.search("\n".join([*environment, *lines])):
                raise AssertionError("DB/scan bypass setting is forbidden")
            if not pair and any(OFFLINE_FLAGS.search(line) for line in lines):
                raise AssertionError("DB/scan bypass is forbidden outside the verified filesystem pair")
            for line, match in launches:
                if line[:match.start()] not in ("", "count=$(") or (pair and match.start() != 0):
                    raise AssertionError("unrecognized Trivy container launch")
                options = shlex.split(match["options"])
                sources = [value for flag, value in zip(options, options[1:])
                           if flag in ("-e", "--env") and value.split("=", 1)[0] == "TRIVY_DB_REPOSITORY"]
                if sources != ["TRIVY_DB_REPOSITORY"] or any(
                    flag.startswith(("--env=TRIVY_DB_REPOSITORY", "-e=TRIVY_DB_REPOSITORY"))
                    for flag in options
                ):
                    raise AssertionError("DB setting is not passed unchanged into the container")
                command = shlex.split(match["command"])
                if pair:
                    check_filesystem_pair(job, step, lines, line, options, command)
                    pairs += 1
                elif command[0] not in ("image", "fs"):
                    raise AssertionError("unrecognized Trivy container command")
                seen += 1
    if pairs != int(name == "ci.yml"):
        raise AssertionError("filesystem scan pair coverage changed")
    if seen != expected_count:
        raise AssertionError("scanner launch coverage changed; review every invocation")
    return seen


class TrivyDatabaseSourcesTests(unittest.TestCase):
    def workflows(self):
        for name, count in WORKFLOWS.items():
            yield name, count, yaml.safe_load((ROOT / ".github/workflows" / name).read_text())

    def test_all_ten_launch_sites_receive_primary_official_source_and_mirror(self):
        total = 0
        for name, count, workflow in self.workflows():
            with self.subTest(workflow=name):
                total += checked_invocations(name, workflow, count)
        self.assertEqual(10, total)

    def test_regression_unconfigured_default_policy_is_rejected(self):
        for name, count, workflow in self.workflows():
            old = copy.deepcopy(workflow)
            old["env"].pop("TRIVY_DB_REPOSITORY", None)
            for job in old["jobs"].values():
                for step in job.get("steps", []):
                    if isinstance(step.get("run"), str):
                        step["run"] = step["run"].replace("-e TRIVY_DB_REPOSITORY ", "")
            with self.subTest(workflow=name), self.assertRaisesRegex(AssertionError, "source priority"):
                checked_invocations(name, old, count)

    def test_host_environment_alone_cannot_fix_a_container_scan(self):
        for name, count, workflow in self.workflows():
            broken = copy.deepcopy(workflow)
            for job in broken["jobs"].values():
                for step in job.get("steps", []):
                    if isinstance(step.get("run"), str):
                        step["run"] = step["run"].replace("-e TRIVY_DB_REPOSITORY ", "")
            broken_pair = (ROOT / PAIR_PATH).read_text().replace("-e TRIVY_DB_REPOSITORY ", "")
            with self.subTest(workflow=name), self.assertRaisesRegex(AssertionError, "not passed"):
                checked_invocations(name, broken, count, pair_script=broken_pair)

    def test_mirror_first_unapproved_repository_and_step_override_are_refused(self):
        for name, count, workflow in self.workflows():
            for value in (",".join(reversed(REPOSITORIES)),
                          "ghcr.io/unreviewed/trivy-db:2",
                          "ghcr.io/aquasecurity/trivy-db:latest"):
                broken = copy.deepcopy(workflow)
                broken["env"]["TRIVY_DB_REPOSITORY"] = value
                with self.subTest(workflow=name, source=value), self.assertRaisesRegex(AssertionError, "source priority"):
                    checked_invocations(name, broken, count)
            broken = copy.deepcopy(workflow)
            step = next(step for job in broken["jobs"].values() for step in job.get("steps", [])
                        if '"$TRIVY_IMAGE"' in step.get("run", ""))
            step.setdefault("env", {})["TRIVY_DB_REPOSITORY"] = ",".join(reversed(REPOSITORIES))
            with self.subTest(workflow=name, scope="step override"), self.assertRaisesRegex(AssertionError, "source priority"):
                checked_invocations(name, broken, count)

    def test_filesystem_scan_pair_rejects_unsafe_changes(self):
        name = "ci.yml"
        workflow = yaml.safe_load((ROOT / ".github/workflows" / name).read_text())
        compare_digests = 'dataset_digests | cmp - "$reports/dataset-digests.sha256"'
        mutations = (
            ("raw removed", "trivy_fs bridge --ignorefile /dev/null --exit-code 0 --output /reports/trivy-raw.json", ""),
            ("raw database update disabled", "trivy_fs bridge --ignorefile", "trivy_fs bridge --skip-db-update --ignorefile"),
            ("raw only downloads", "trivy_fs bridge --ignorefile", "trivy_fs bridge --download-db-only --ignorefile"),
            ("raw network disabled", "trivy_fs bridge --ignorefile", "trivy_fs none --ignorefile"),
            ("old cache reused", 'cache=$(mktemp -d "$RUNNER_TEMP/supply-chain-cache.XXXXXX")',
             'cache="$RUNNER_TEMP/daily-trivy"'),
            ("cache not mounted", '-v "$cache:/trivy-cache"', '-v other:/trivy-cache'),
            ("DB environment removed", '-e TRIVY_DB_REPOSITORY "$TRIVY_IMAGE"', '"$TRIVY_IMAGE"'),
            ("DB hash omitted", "sha256sum db/trivy.db db/metadata.json", "sha256sum db/metadata.json"),
            ("pre-policy comparison removed", f"{compare_digests}\npolicy_status=0", "policy_status=0"),
            ("post-policy comparison removed", f"{compare_digests}\npython3", "python3"),
            ("guard removed", 'python3 "$verifier" guard "${evidence[@]}"', ""),
            ("audit removed", 'python3 "$verifier" audit', 'printf "ignored"'),
            ("policy network enabled", "trivy_fs none --offline-scan", "trivy_fs bridge --offline-scan"),
            ("docker network ignored", 'docker run --rm --network "$network"', "docker run --rm --network bridge"),
            ("scanner narrowed", "--scanners vuln,misconfig", "--scanners vuln"),
            ("scanner not executed", "docker run --rm --network", "echo docker run --rm --network"),
            ("policy exit swallowed", 'exit "$policy_status"', "exit 0"),
        )
        script = (ROOT / PAIR_PATH).read_text()
        for label, original, replacement in mutations:
            with self.subTest(mutation=label):
                self.assertIn(original, script)
                changed = script.replace(original, replacement, 1)
                with self.assertRaises(AssertionError):
                    checked_invocations(name, workflow, WORKFLOWS[name], pair_script=changed)

    def test_filesystem_pair_cannot_be_skipped_or_allowed_to_fail(self):
        name = "ci.yml"
        workflow = yaml.safe_load((ROOT / ".github/workflows" / name).read_text())
        for option, value in (("if", "${{ false }}"), ("continue-on-error", True), ("shell", "sh")):
            changed = copy.deepcopy(workflow)
            step = next(step for step in changed["jobs"]["supply-chain"]["steps"] if step.get("name") == PAIR_STEP)
            step[option] = value
            with self.subTest(option=option), self.assertRaisesRegex(AssertionError, "must run and fail closed"):
                checked_invocations(name, changed, WORKFLOWS[name])

    def test_openvex_critical_is_exercised_in_pull_request_ci(self):
        workflow = yaml.safe_load((ROOT / ".github/workflows/ci.yml").read_text())
        steps = workflow["jobs"]["supply-chain"]["steps"]
        scan, = (step for step in steps if step.get("name") == PAIR_STEP)
        regression, = (step for step in steps
                       if step.get("run") == "python3 scripts/supply-chain/verify_trivy_openvex_block.py")
        self.assertLess(steps.index(scan), steps.index(regression))
        self.assertEqual(regression.get("if"), "${{ success() && github.event_name == 'pull_request' }}")
        self.assertFalse(regression.get("continue-on-error", False))

    def test_other_launch_sites_cannot_use_offline_modes(self):
        name = "publish-images.yml"
        workflow = yaml.safe_load((ROOT / ".github/workflows" / name).read_text())
        for option, replacement in (
            ("offline flag", '"$TRIVY_IMAGE" image --offline-scan'),
            ("environment override", '-e TRIVY_SKIP_DB_UPDATE=true "$TRIVY_IMAGE" image'),
            ("disabled Docker network", '--network=none "$TRIVY_IMAGE" image'),
        ):
            changed = copy.deepcopy(workflow)
            step = next(step for job in changed["jobs"].values() for step in job.get("steps", [])
                        if '"$TRIVY_IMAGE" image' in step.get("run", ""))
            step["run"] = step["run"].replace('"$TRIVY_IMAGE" image', replacement, 1)
            with self.subTest(option=option), self.assertRaisesRegex(AssertionError, "bypass"):
                checked_invocations(name, changed, WORKFLOWS[name])

    def test_component_gates_fail_closed_and_only_renewal_reads_the_exception_file(self):
        for name, count, workflow in self.workflows():
            if name not in MODE_SPLIT_WORKFLOWS:
                continue
            gates = {step["if"]: step for job in workflow["jobs"].values() for step in job.get("steps", [])
                     if "--exit-code 1" in step.get("run", "")}
            self.assertEqual(set(gates), {"${{ inputs.mode == 'qualify' }}", "${{ inputs.mode == 'renew' }}"})
            for step in gates.values():
                self.assertFalse(step.get("continue-on-error", False))
            self.assertNotIn("ignorefile", gates["${{ inputs.mode == 'qualify' }}"]["run"])
            self.assertNotIn("trivyignore", gates["${{ inputs.mode == 'qualify' }}"]["run"])
            self.assertIn("--ignorefile /trivyignore", gates["${{ inputs.mode == 'renew' }}"]["run"])
            broken = copy.deepcopy(workflow)
            for job in broken["jobs"].values():
                for candidate in job.get("steps", []):
                    if "--exit-code 1" in candidate.get("run", ""):
                        candidate["run"] += "\n# disallowed mutation\n--skip-db-update\n"
            with self.assertRaisesRegex(AssertionError, "bypass"):
                checked_invocations(name, broken, count)

    def test_vulnerability_gate_still_fails_closed_without_skipping_database(self):
        for name, count, workflow in self.workflows():
            if name in MODE_SPLIT_WORKFLOWS:
                continue
            steps = [step for job in workflow["jobs"].values() for step in job.get("steps", [])
                     if step.get("name") == "Trivy nas imagens (HIGH e CRITICAL corrigíveis bloqueiam)"]
            with self.subTest(workflow=name):
                self.assertEqual(1, len(steps))
                step = steps[0]
                self.assertFalse(step.get("continue-on-error", False))
                for required in ("--severity HIGH,CRITICAL", "--ignore-unfixed",
                                 "--ignorefile /trivyignore", "--exit-code 1"):
                    self.assertIn(required, step["run"])
                broken = copy.deepcopy(workflow)
                for job in broken["jobs"].values():
                    for candidate in job.get("steps", []):
                        if candidate.get("name") == step["name"]:
                            candidate["run"] += "\n# disallowed mutation\n--skip-db-update\n"
                with self.assertRaisesRegex(AssertionError, "bypass"):
                    checked_invocations(name, broken, count)
