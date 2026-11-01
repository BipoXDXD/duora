#!/usr/bin/env bash
# Synthetic process/volume experiments only. No production mounts or credentials.
set -euo pipefail
cd "$(dirname "$0")/../../.."
image=duora-closure-probe:local
docker build -f scripts/testing/private-generation-closure/Dockerfile -t "$image" .
for area in candidate promotion; do
  docker run --rm --network none --read-only --cap-drop ALL \
    --security-opt no-new-privileges --tmpfs /tmp:rw,nosuid,nodev,mode=1777 \
    --mount type=volume,destination=/scratch -e "DUORA_CLOSURE_AREA=$area" "$image"
done

# Different containers, same disk volume: this is restart, NOT power-loss qualification.
volume="duora-closure-restart-$$"
docker volume create "$volume" >/dev/null
trap 'docker volume rm "$volume" >/dev/null' EXIT
probe() {
  docker run --rm --network none --read-only --cap-drop ALL \
    --security-opt no-new-privileges --tmpfs /tmp:rw,nosuid,nodev,mode=1777 \
    --mount "type=volume,source=$volume,destination=/scratch" --entrypoint dotnet \
    "$image" /probe/Probe.dll "$1" /scratch/restart "$area" \
    aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb none 0
}
for area in candidate promotion; do
  probe reserve
  probe write
  probe close
  probe sweep
  if probe write; then
    echo 'ERROR: generation reopened after container restart' >&2
    exit 1
  else
    code=$?
    test "$code" -eq 2
  fi
  probe close
  probe sweep
done
echo 'PASS: process, counterexample and container-restart experiments'
