#!/bin/sh
# Build on an isolated workstation/runner, never on production. No workflow changes.
set -eu
[ "$#" = 3 ] || { echo 'Usage: build-image.sh OPENCLAW_SOURCE_DIR APPROVED_40_CHAR_SHA REGISTRY_IMAGE_TAG' >&2; exit 1; }
SRC=$1
SHA=$2
TAG=$3
printf '%s' "$SHA" | /usr/bin/grep -Eq '^[0-9a-f]{40}$'
[ "$(git -C "$SRC" rev-parse HEAD)" = "$SHA" ]
[ -z "$(git -C "$SRC" status --porcelain)" ]
docker buildx build --platform linux/amd64,linux/arm64 \
  --build-arg "GIT_COMMIT=$SHA" \
  --build-arg "OPENCLAW_BUILD_TIMESTAMP=$(date -u +%Y-%m-%dT%H:%M:%SZ)" \
  --build-arg OPENCLAW_EXTENSIONS=slack --build-arg OPENCLAW_INSTALL_BROWSER= \
  --provenance=mode=max --sbom=true --tag "$TAG" --push "$SRC"
docker buildx imagetools inspect "$TAG"
printf '%s\n' 'Review provenance, SBOM and vulnerability scan; deploy the resulting digest, never this tag.'
