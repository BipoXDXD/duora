#!/usr/bin/env bash
# Hosted CI runner only; the VPS receives OCI data, never registry credentials.
set -euo pipefail
umask 077
script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
[[ ${RELEASE_COMMIT:?} =~ ^[0-9a-f]{40}$ ]]
case "${RELEASE_PROJECT:?}" in duora|goblinshift|stillon) ;; *) exit 2 ;; esac
[[ ${DEPLOY_TARGET:?} =~ ^cfrelease-${RELEASE_PROJECT}@[a-zA-Z0-9.-]+$ ]]
stage=$(mktemp -d)
trap 'rm -rf "$stage"' EXIT
mkdir "$stage/images" "$stage/source-manifests"
python3 "$script_dir/send.py" requested > "$stage/requested.tsv"
while IFS=$'\t' read -r role reference; do
  crane manifest "$reference" > "$stage/source-manifests/$role.json"
  # jq -e: 0 and 1 answer the question; any other status is a jq failure,
  # which includes anything other than exactly one JSON object.
  index_check=0
  jq -se 'if length == 1 and (.[0] | type) == "object" then .[0] | has("manifests") else error("expected exactly one JSON object") end' "$stage/source-manifests/$role.json" >/dev/null || index_check=$?
  case $index_check in
    0) digest=$(jq -er '[.manifests[] | select(.platform.os == "linux" and .platform.architecture == "amd64")] | if length == 1 then .[0].digest else error("ambiguous platform") end' "$stage/source-manifests/$role.json") ;;
    1) digest=$(crane digest "$reference") ;;
    *) exit "$index_check" ;;
  esac
  [[ $digest =~ ^sha256:[0-9a-f]{64}$ ]]
  repository=${reference%@*}
  repository=${repository%:*}
  resolved="$repository@$digest"
  printf '%s\t%s\n' "$role" "$resolved" >> "$stage/resolved.tsv"
  crane pull --platform linux/amd64 --format oci "$resolved" "$stage/images/$role"
  jq -c 'del(.manifests[]?.artifactType)' "$stage/images/$role/index.json" > "$stage/normalized.json"
  mv "$stage/normalized.json" "$stage/images/$role/index.json"
done < "$stage/requested.tsv"
SOURCE_MANIFESTS_DIR="$stage/source-manifests" \
  python3 "$script_dir/send.py" manifest "$stage/resolved.tsv" "$stage/images"
tar -C "$stage/images" -cf "$stage/release.tar" .
ssh -o BatchMode=yes -o IdentitiesOnly=yes -o StrictHostKeyChecking=yes \
  -i "${DEPLOY_KEY:?}" -o "UserKnownHostsFile=${DEPLOY_KNOWN_HOSTS:?}" \
  "$DEPLOY_TARGET" "release $RELEASE_COMMIT" < "$stage/release.tar"
