#!/usr/bin/env bash
# Diagnóstico do ambiente local (spec 2.5). Só lê o ambiente: não instala nem altera nada.
# Sai com código 1 se faltar algum pré-requisito obrigatório.
# Compatível com o bash 3.2 do macOS e com bash 5 no Ubuntu.
set -uo pipefail

repo_root="$(cd "$(dirname "$0")/.." && pwd)"
failures=0

ok() { printf '  [ok]    %s\n' "$1"; }
warn() { printf '  [aviso] %s\n' "$1"; }
fail() {
  printf '  [falha] %s\n          -> %s\n' "$1" "$2"
  failures=$((failures + 1))
}
has() { command -v "$1" >/dev/null 2>&1; }

echo "Sistema"
case "$(uname -s)" in
  Darwin)
    ok "macOS $(sw_vers -productVersion), $(uname -m)"
    if [ "$(sysctl -n sysctl.proc_translated 2>/dev/null || echo 0)" = "1" ]; then
      fail "terminal rodando sob Rosetta (x86_64 traduzido)" \
        "abra o terminal nativo arm64; ferramentas traduzidas não valem como evidência de paridade"
    fi
    ;;
  Linux)
    ok "Linux $(uname -r), $(uname -m)"
    ;;
  *)
    warn "sistema não previsto: $(uname -s)"
    ;;
esac

echo ".NET"
if ! has dotnet; then
  fail "dotnet não encontrado" "instale o SDK da linha 10 indicado em global.json (https://dotnet.microsoft.com/download)"
else
  required_sdk="$(sed -n 's/.*"version": *"\([^"]*\)".*/\1/p' "$repo_root/global.json" | head -n 1)"
  if sdk="$(cd "$repo_root" && dotnet --version 2>/dev/null)"; then
    ok "SDK $sdk (global.json pede $required_sdk com rollForward latestPatch)"
  else
    fail "nenhum SDK compatível com global.json ($required_sdk)" \
      "instale o SDK $required_sdk ou um patch mais novo da mesma feature band"
  fi
  if [ ! -f "$repo_root/.config/dotnet-tools.json" ]; then
    fail "manifesto .config/dotnet-tools.json ausente" "restaure o arquivo do repositório"
  elif ef_version="$(cd "$repo_root" && dotnet tool run dotnet-ef --version 2>/dev/null | tail -n 1)"; then
    ok "dotnet-ef $ef_version (ferramenta local)"
  else
    fail "dotnet-ef local indisponível" "rode 'dotnet tool restore' na raiz do repositório"
  fi
fi

echo "Frontend"
if [ -f "$repo_root/frontend/package.json" ]; then
  if has node; then
    node_major="$(node --version | sed 's/^v\([0-9]*\).*/\1/')"
    if [ "$node_major" = "24" ]; then
      ok "Node.js $(node --version)"
    else
      fail "Node.js $(node --version), esperado 24.x" "instale o Node.js 24 LTS"
    fi
  else
    fail "node não encontrado" "instale o Node.js 24 LTS"
  fi
  # O pnpm vem do Corepack, na versão do campo packageManager; não se procura pnpm global no PATH.
  expected_pnpm="$(sed -n 's/.*"packageManager": *"pnpm@\([^"+]*\).*/\1/p' "$repo_root/frontend/package.json" | head -n 1)"
  if [ -z "$expected_pnpm" ]; then
    fail "frontend/package.json sem packageManager pnpm@<versão>" "declare o campo packageManager no frontend"
  elif ! has corepack; then
    fail "corepack não encontrado" "use o Node.js 24 oficial (traz o Corepack) e rode 'corepack enable pnpm'"
  elif pnpm_version="$(cd "$repo_root/frontend" && COREPACK_ENABLE_DOWNLOAD_PROMPT=0 corepack pnpm --version 2>/dev/null | tail -n 1)" \
    && [ "$pnpm_version" = "$expected_pnpm" ]; then
    ok "pnpm $pnpm_version via Corepack (packageManager pede $expected_pnpm)"
  else
    fail "pnpm via Corepack indisponível ou diferente de $expected_pnpm (obtido: ${pnpm_version:-nada})" \
      "rode 'corepack enable pnpm' e, em frontend/, 'corepack pnpm --version'"
  fi
else
  warn "frontend/ ainda não existe; Node.js e pnpm não verificados"
fi

echo "Containers"
if ! has docker; then
  fail "docker não encontrado" "instale o Docker Desktop (macOS) ou o Docker Engine (Ubuntu)"
elif ! server_os="$(docker version --format '{{.Server.Os}}' 2>/dev/null)"; then
  fail "daemon do Docker inacessível" "abra o Docker Desktop (ou 'sudo systemctl start docker') e tente de novo"
else
  if [ "$server_os" = "linux" ]; then
    ok "Docker $(docker version --format '{{.Server.Version}}') ($server_os/$(docker version --format '{{.Server.Arch}}'))"
  else
    fail "Docker com runtime $server_os" "use o runtime Linux; Testcontainers e o Compose dependem dele"
  fi
  ok "contexto: $(docker context show 2>/dev/null)"
  if compose_version="$(docker compose version --short 2>/dev/null)"; then
    ok "Docker Compose $compose_version"
  else
    fail "plugin docker compose ausente" "instale o plugin Compose v2 (vem no Docker Desktop)"
  fi
fi

echo
if [ "$failures" -gt 0 ]; then
  echo "Ambiente incompleto: $failures pré-requisito(s) com falha."
  exit 1
fi
echo "Ambiente pronto."
