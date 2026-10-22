#!/bin/sh
# Local = arquitetura do daemon; release = AMD64 + ARM64 sem importação local.
set -eu

fail() { printf '%s\n' "docker-build: $*" >&2; exit 2; }
usage() {
  cat >&2 <<'USAGE'
Uso:
  scripts/docker-build.sh local -t IMAGEM [opções de buildx] CONTEXTO
  scripts/docker-build.sh oci ARQUIVO.tar -t IMAGEM [opções de buildx] CONTEXTO
  scripts/docker-build.sh publish -t REGISTRY/IMAGEM [opções de buildx] CONTEXTO
  scripts/docker-build.sh compose [argumentos de compose]
  scripts/docker-build.sh check IMAGEM...
  scripts/docker-build.sh published REGISTRY/IMAGEM...
local/compose seguem o daemon Docker ativo, mesmo com DOCKER_DEFAULT_PLATFORM definido.
oci/publish incluem linux/amd64 e linux/arm64. Plataforma/exportador não são sobrescrevíveis.
USAGE
  exit 2
}

[ "$#" -gt 0 ] || usage
mode=$1
shift
case "$mode" in
  local|compose|check)
    host=$(docker info --format '{{.OSType}}/{{.Architecture}}')
    case "$host" in
      linux/arm64|linux/aarch64) platform=linux/arm64 ;;
      linux/amd64|linux/x86_64) platform=linux/amd64 ;;
      *) fail "daemon sem suporte: $host (esperado Linux ARM64 ou AMD64)" ;;
    esac
    # Só o processo filho recebe a escolha; não altera configuração global nem outra sessão.
    DOCKER_DEFAULT_PLATFORM=$platform
    export DOCKER_DEFAULT_PLATFORM
    ;;
  oci|publish|published) ;;
  *) usage ;;
esac

check_native() {
  [ "$#" -gt 0 ] || fail 'informe pelo menos uma imagem'
  for image do
    actual=$(docker image inspect --format '{{.Os}}/{{.Architecture}}' "$image")
    [ "$actual" = "$platform" ] || fail "$image: $actual, esperado $platform"
    printf '%s\n' "$image: $actual (nativo)"
  done
}

check_published() {
  [ "$#" -gt 0 ] || fail 'informe pelo menos uma imagem publicada'
  for image do
    platforms=$(docker buildx imagetools inspect "$image" --format '{{range .Manifest.Manifests}}{{.Platform.OS}}/{{.Platform.Architecture}}{{"\n"}}{{end}}')
    for required in linux/amd64 linux/arm64; do
      count=$(printf '%s\n' "$platforms" | awk -v wanted="$required" '$0 == wanted {n++} END {print n+0}')
      [ "$count" -eq 1 ] || fail "$image: precisa de exatamente um manifesto $required; encontrados $count"
    done
    printf '%s\n' "$image: linux/amd64 + linux/arm64"
  done
}

case "$mode" in
  compose) exec docker compose "$@" ;;
  check) check_native "$@"; exit 0 ;;
  published) check_published "$@"; exit 0 ;;
  oci)
    [ "$#" -gt 0 ] || usage
    destination=$1
    shift
    case "$destination" in
      ''|*','*|*'
'*) fail 'caminho OCI vazio ou com vírgula/quebra de linha' ;;
    esac
    ;;
esac

tags=
awaiting_tag=false
for argument do
  if "$awaiting_tag"; then
    tags="${tags}${argument}
"
    awaiting_tag=false
    continue
  fi
  case "$argument" in
    --platform|--platform=*|--push|--push=*|--load|--load=*|--output|--output=*|-o|-o?*)
      fail "opção controlada pelo modo $mode: $argument" ;;
    -t|--tag) awaiting_tag=true ;;
    --tag=*) tags="${tags}${argument#--tag=}
" ;;
    -t?*) tags="${tags}${argument#-t}
" ;;
    -f|-f?*|-q|-D|-h|--*) ;;
    -*) fail "use opções curtas separadas, sem agrupamento: $argument" ;;
  esac
done
"$awaiting_tag" && fail 'tag sem valor'
[ -n "$tags" ] || fail 'use -t/--tag para verificar a imagem resultante'

case "$mode" in
  local)
    docker buildx build --platform "$platform" --load "$@"
    printf '%s' "$tags" | while IFS= read -r image; do check_native "$image"; done
    ;;
  oci)
    # O exporter OCI não registra tags no Docker Desktop: um build para a VPS não substitui a imagem local.
    docker buildx build --platform linux/amd64,linux/arm64 --output "type=oci,dest=$destination" "$@"
    ;;
  publish)
    docker buildx build --platform linux/amd64,linux/arm64 --push "$@"
    printf '%s' "$tags" | while IFS= read -r image; do check_published "$image"; done
    ;;
esac
