#!/usr/bin/env bash
set -eo pipefail

reports=$(mktemp -d "$RUNNER_TEMP/supply-chain-report.XXXXXX")
cache=$(mktemp -d "$RUNNER_TEMP/supply-chain-cache.XXXXXX")
echo "SUPPLY_CHAIN_REPORTS=$reports" >> "$GITHUB_ENV"
trivy_fs() {
  local network=$1
  shift
  docker run --rm --network "$network" \
    -v "$GITHUB_WORKSPACE:/repo:ro" \
    -v "$reports:/reports" \
    -v "$cache:/trivy-cache" \
    -w /repo \
    -e TRIVY_DB_REPOSITORY "$TRIVY_IMAGE" \
    --config '' --cache-dir /trivy-cache fs . \
    --scanners vuln,misconfig --severity HIGH,CRITICAL --include-dev-deps \
    --format json --list-all-pkgs "$@"
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
