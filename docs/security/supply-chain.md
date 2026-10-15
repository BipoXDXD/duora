# Cadeia de dependências (supply chain)

Dependências, licenças, SBOM e exceções (spec 2.3, 17.1 e 21.2; fontes D-CI-01, D-CI-02 e D-CI-03). Atualize este
arquivo quando entrar pacote ou ferramenta nova, quando mudar uma versão fixada ou quando uma exceção for aceita.

## Controles ativos

| Controle | Onde | Bloqueia quando |
|---|---|---|
| Lockfiles e fontes | `dotnet restore --locked-mode`, `pnpm install --frozen-lockfile`, `nuget.config` só com nuget.org | lockfile diverge ou aparece fonte não mapeada |
| Auditoria do NuGet | `NuGetAudit` em `Directory.Build.props` (modo `all`, nível `low`) | o build encontra pacote com vulnerabilidade conhecida |
| Vulnerabilidades de dependências | job `supply-chain`, Trivy `fs` sobre o repositório (`packages.lock.json`, `Directory.Packages.props`, `pnpm-lock.yaml`, incluindo devDependencies) | achado HIGH ou CRITICAL fora do `.trivyignore` |
| Misconfiguration | mesmo passo do Trivy (`--scanners vuln,misconfig`) | achado HIGH ou CRITICAL em Dockerfile e demais formatos de IaC que o Trivy reconhece |
| SBOM | job `supply-chain`, Trivy `--format cyclonedx` para `backend/` e `frontend/` | falha ao gerar; os arquivos saem no artifact `sbom-cyclonedx` (30 dias) |
| Licenças | `scripts/supply-chain/check_licenses.py` sobre os SBOMs | licença fora da allowlist, ou componente sem licença, sem exceção escrita |
| Segredos | job `secret-scan` (Gitleaks, histórico inteiro alcançável do commit testado, `--log-opts=HEAD`; branches de outros PRs ficam fora) | qualquer segredo, inclusive um removido em commit posterior; o Trivy roda com o scanner de segredos desligado para não duplicar |
| Actions | `.github/workflows/ci.yml` | action de terceiros sem SHA completo (revisão); o Dependabot atualiza SHA e comentário |

Os achados aparecem no log do job; o inventário de licenças e o changelog do contrato vão para o resumo do job.

## Ferramentas fixadas

| Ferramenta | Versão | Fixação | Verificação |
|---|---|---|---|
| Trivy | 0.75.0 | `ghcr.io/aquasecurity/trivy@sha256:af6acf9a6b85dfe389a1941505c0ce9efef52a4719635e1a962f022a3d855daa` (índice multi-arquitetura; mesmo digest em `aquasec/trivy:0.75.0`) | assinatura cosign keyless conferida em 2026-10-15 contra os workflows de `aquasecurity/trivy` (comando abaixo) |
| oasdiff | v1.33.0 | `tufin/oasdiff@sha256:6263a96dd2ef0726c54e21fea9b8e1607eac4841add0079324b424c1f52b819c` (índice multi-arquitetura) | a imagem não é assinada; o digest foi lido da tag `v1.33.0` em 2026-10-15 e só muda por PR |
| Gitleaks | v8.30.1 | `ghcr.io/gitleaks/gitleaks@sha256:c00b6bd0…` (ver `ci.yml`) | digest fixado |
| openapi-typescript | 7.13.0 | versão exata em `frontend/package.json` + `pnpm-lock.yaml` | lockfile com integridade; versão exata porque um bump muda a saída gerada |
| Microsoft.Extensions.ApiDescription.Server | 10.0.13 | `Directory.Packages.props` + `packages.lock.json` | lockfile com content hash |
| Verificadores próprios | — | `scripts/supply-chain/*.py`, só biblioteca padrão do Python do runner | testes unitários rodam no início do job |

As ferramentas em container rodam por `docker run` com o repositório montado somente leitura, no mesmo padrão do
Gitleaks: nenhuma action JavaScript de terceiros no job e nenhum token exposto. O job tem só `contents: read`.

## Política de exceções

Exceção é decisão de risco, não atalho para deixar o CI verde (AGENTS.md proíbe reduzir gate). Toda exceção passa
por revisão humana no PR que a cria.

### Vulnerabilidade ou misconfig: `.trivyignore`

- Uma entrada por ID, com comentário de justificativa na linha imediatamente acima: por que não se aplica ou qual
  mitigação existe, e quem aceitou o risco.
- Validade obrigatória `exp:AAAA-MM-DD`. Vencida, o Trivy volta a reportar e o achado é triado de novo.
- `scripts/supply-chain/check_trivyignore.py` reprova o job se alguma entrada não seguir a regra.
- Prefira corrigir: atualizar o pacote, fixar a transitiva em `Directory.Packages.props` ou usar `overrides` do
  pnpm, nessa ordem.

### Licenças: `scripts/supply-chain/license-policy.json`

- `allowed`: licenças permissivas aceitas sem discussão (MIT, MIT-0, Apache-2.0, BSD-2-Clause, BSD-3-Clause,
  0BSD, ISC, BlueOak-1.0.0, CC0-1.0, Unlicense, PostgreSQL, Python-2.0). Expressões SPDX são avaliadas: `OR`
  passa se uma alternativa está na lista, `AND` exige todas, `X WITH Y` só passa se estiver inteiro na lista.
- `exceptions`: pacote (aceita glob) + licença exata + justificativa. Cobre só aquela licença daquele pacote; se o
  pacote mudar de licença, o job volta a reprovar.
- `missingLicenseAllowed`: padrões de pacote que podem aparecer sem licença, com justificativa. Hoje são só os
  binários nativos opcionais por plataforma (rolldown, Tailwind oxide, lightningcss, fsevents): o lockfile lista
  todos, mas só o da plataforma do runner é instalado, e é dele que a licença é lida e conferida.
- Exceção que não casa com nenhum componente gera aviso no resumo; remova-a quando a dependência sair.

Exceções aceitas em 2026-10-15:

| Pacote | Licença | Motivo |
|---|---|---|
| `axe-core`, `@axe-core/playwright` | MPL-2.0 | só nos testes E2E de acessibilidade; não vai para o bundle; copyleft por arquivo sem modificação nossa |
| `lightningcss` e binários `lightningcss-*` | MPL-2.0 | ferramenta de build do CSS; não vai para o bundle e o CSS gerado não é obra derivada |
| `caniuse-lite` | CC-BY-4.0 | dados do Browserslist no build; licença permissiva que só exige atribuição |

## Escopo do SBOM

Os SBOMs descrevem as dependências declaradas no repositório, com as de desenvolvimento e teste incluídas,
porque elas rodam no CI e entram no inventário de licenças. O SBOM da release, por imagem e por digest, entra
com o gate de Release (spec 17.2), quando houver imagens.

## Lacunas conhecidas

- O Trivy não tem checks de misconfig para Docker Compose; `infra/compose/compose.dev.yml` não é coberto por
  ele (há `ComposeFileTests` no backend). Dockerfiles ainda não existem; quando entrarem, o mesmo passo os varre.
- Varredura de imagens (`trivy image`) entra com o gate de Release.
- O Dependabot não atualiza as imagens fixadas em `ci.yml` (`TRIVY_IMAGE`, `OASDIFF_IMAGE`, Gitleaks). A
  atualização é manual, pelo procedimento abaixo, ao menos uma vez por mês ou quando sair correção de segurança.
- `openapi-typescript` 7.13.0 declara peer `typescript ^5`; o projeto usa TypeScript 6 e a geração foi
  conferida. O `pnpm install` mostra o aviso de peer.

## Como atualizar

Escolha uma release publicada há pelo menos duas semanas, salvo correção de segurança.

Trivy:

```sh
docker buildx imagetools inspect ghcr.io/aquasecurity/trivy:<versão>        # anote o Digest do índice
docker run --rm gcr.io/projectsigstore/cosign:v2.6.1 verify aquasec/trivy:<versão> \
  --new-bundle-format \
  --certificate-identity-regexp 'https://github\.com/aquasecurity/trivy/\.github/workflows/.+' \
  --certificate-oidc-issuer https://token.actions.githubusercontent.com    # o digest verificado tem de ser o anotado
```

oasdiff:

```sh
docker buildx imagetools inspect tufin/oasdiff:v<versão>                    # anote o Digest do índice
```

Depois, troque a variável em `.github/workflows/ci.yml`, atualize a tabela de ferramentas acima e abra um PR só
com isso. Gitleaks segue o mesmo procedimento.

## Rodar localmente

Da raiz do repositório, depois de `dotnet restore` e `pnpm install`:

```sh
trivy_image=ghcr.io/aquasecurity/trivy:0.75.0@sha256:af6acf9a6b85dfe389a1941505c0ce9efef52a4719635e1a962f022a3d855daa
docker run --rm -v "$PWD:/repo:ro" -w /repo "$trivy_image" fs . \
  --scanners vuln,misconfig --severity HIGH,CRITICAL --include-dev-deps --ignorefile .trivyignore \
  --skip-dirs '**/bin' --skip-dirs '**/obj' --exit-code 1

mkdir -p sbom
for part in backend frontend; do
  docker run --rm -e NUGET_PACKAGES=/nuget -v "$HOME/.nuget/packages:/nuget:ro" -v "$PWD:/repo:ro" \
    -v "$PWD/sbom:/sbom" -w /repo "$trivy_image" fs "$part" --format cyclonedx --include-dev-deps --quiet \
    --skip-dirs '**/bin' --skip-dirs '**/obj' --output "/sbom/sbom-$part.cdx.json"
done
python3 scripts/supply-chain/check_licenses.py scripts/supply-chain/license-policy.json sbom/*.cdx.json
python3 -m unittest discover -s scripts/supply-chain
```

`sbom/` é saída local; não versione. No macOS a lista de binários opcionais sem licença muda (são os de Linux e
Windows), o que a política já prevê.
