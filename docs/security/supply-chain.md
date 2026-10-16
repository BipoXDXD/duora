# Cadeia de dependências (supply chain)

Dependências, licenças, SBOM e exceções (spec 2.3, 17.1 e 21.2; fontes D-CI-01, D-CI-02 e D-CI-03). Atualize este
arquivo quando entrar pacote ou ferramenta nova, quando mudar uma versão fixada ou quando uma exceção for aceita.

## Controles ativos

| Controle | Onde | Bloqueia quando |
|---|---|---|
| Lockfiles e fontes | `dotnet restore --locked-mode`, `pnpm install --frozen-lockfile`, `pip install --require-hashes` no job `ops` (`scripts/deploy/requirements.txt` e `tools/visual/requirements.txt`), `nuget.config` só com nuget.org | lockfile diverge, hash de pacote Python diverge ou aparece fonte não mapeada |
| Auditoria do NuGet | `NuGetAudit` em `Directory.Build.props` (modo `all`, nível `low`) | o build encontra pacote com vulnerabilidade conhecida |
| Vulnerabilidades de dependências | job `supply-chain`, Trivy `fs` sobre o repositório (`packages.lock.json`, `Directory.Packages.props`, `pnpm-lock.yaml`, incluindo devDependencies, e os lockfiles pip `scripts/deploy/requirements.txt` e `tools/visual/requirements.txt`) | achado HIGH ou CRITICAL fora do `.trivyignore` |
| Misconfiguration | mesmo passo do Trivy (`--scanners vuln,misconfig`) | achado HIGH ou CRITICAL em Dockerfile e demais formatos de IaC que o Trivy reconhece |
| SBOM | job `supply-chain`, Trivy `--format cyclonedx` para `backend/` e `frontend/` | falha ao gerar; os arquivos saem no artifact `sbom-cyclonedx` (30 dias) |
| Licenças | `scripts/supply-chain/check_licenses.py` sobre os SBOMs | licença fora da allowlist, ou componente sem licença, sem exceção escrita |
| Segredos | job `secret-scan` (Gitleaks, histórico inteiro alcançável do commit testado, `--log-opts=HEAD`; branches de outros PRs ficam fora) | qualquer segredo, inclusive um removido em commit posterior; o Trivy roda com o scanner de segredos desligado para não duplicar |
| Actions | `.github/workflows/ci.yml` | action de terceiros sem SHA completo (revisão); o Dependabot atualiza SHA e comentário |
| Atualizações | `.github/dependabot.yml`: NuGet, npm, Actions, Compose e pip (`/scripts/deploy`, `/tools/visual`), com cooldown de 7 dias onde há registro de pacotes | não bloqueia; abre PRs semanais revisados por humano |

Os achados aparecem no log do job; o inventário de licenças e o changelog do contrato vão para o resumo do job.

## Ferramentas fixadas

| Ferramenta | Versão | Fixação | Verificação |
|---|---|---|---|
| Trivy | 0.75.0 | `ghcr.io/aquasecurity/trivy@sha256:af6acf9a6b85dfe389a1941505c0ce9efef52a4719635e1a962f022a3d855daa` (índice multi-arquitetura; mesmo digest em `aquasec/trivy:0.75.0`) | assinatura cosign keyless conferida em 2026-10-15 contra os workflows de `aquasecurity/trivy` (comando abaixo) |
| oasdiff | v1.33.0 | `tufin/oasdiff@sha256:6263a96dd2ef0726c54e21fea9b8e1607eac4841add0079324b424c1f52b819c` (índice multi-arquitetura) | a imagem não é assinada; o digest foi lido da tag `v1.33.0` em 2026-10-15 e só muda por PR |
| Gitleaks | v8.30.1 | `ghcr.io/gitleaks/gitleaks@sha256:c00b6bd0…` (ver `ci.yml`) | digest fixado |
| openapi-typescript | 7.13.0 | versão exata em `frontend/package.json` + `pnpm-lock.yaml` | lockfile com integridade; versão exata porque um bump muda a saída gerada |
| Microsoft.Extensions.ApiDescription.Server | 10.0.13 | `Directory.Packages.props` + `packages.lock.json` | lockfile com content hash |
| pyright, ansible-core, ansible-lint | 1.1.408, 2.21.5, 26.10.1 | `scripts/deploy/requirements.in` + `scripts/deploy/requirements.txt` com hashes (`uv pip compile --generate-hashes`) | `pip install --require-hashes` no job `ops`; o mesmo pyright checa `scripts/` e `tools/visual` |
| Pillow | 12.3.0 | `tools/visual/requirements.in` + `tools/visual/requirements.txt` com hashes (`uv pip compile --generate-hashes`) | `pip install --require-hashes`; só no tooling de ilustrações, nunca no app |
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
- `missingLicenseAllowed` aceita `version` (exata) quando a licença foi lida à mão daquela versão, porque o
  pacote a declara só como arquivo, que o Trivy não lê. Outra versão volta a reprovar: quem atualiza (inclusive o
  PR do Dependabot) relê o `LICENSE` da versão nova e troca o campo, no mesmo PR.
- Exceção que não casa com nenhum componente gera aviso no resumo; remova-a quando a dependência sair.

Exceções aceitas em 2026-10-15:

| Pacote | Licença | Motivo |
|---|---|---|
| `axe-core`, `@axe-core/playwright` | MPL-2.0 | só nos testes E2E de acessibilidade; não vai para o bundle; copyleft por arquivo sem modificação nossa |
| `lightningcss` e binários `lightningcss-*` | MPL-2.0 | ferramenta de build do CSS; não vai para o bundle e o CSS gerado não é obra derivada |
| `caniuse-lite` | CC-BY-4.0 | dados do Browserslist no build; licença permissiva que só exige atribuição |

Exceção aceita em 2026-10-16 para o pipeline de ilustrações (aprovada pelo responsável por delegação, issue #9):

| Pacote | Licença | Motivo |
|---|---|---|
| `pillow` 12.3.0 | MIT-CMU | só no tooling offline `tools/visual`; não vai para o bundle nem para as imagens. Enquanto o SBOM não cobrir `tools/visual`, a exceção não casa com componente e aparece como aviso |

Exceção aceita para o pipeline de fotos ([ADR-0025](../architecture/adr/0025-pipeline-de-fotos-imagesharp-s3-outbox.md)):

| Pacote | Licença | Motivo |
|---|---|---|
| `SixLabors.ImageSharp` 4.1.3 (`missingLicenseAllowed` com `version`) | Six Labors Split License 1.0, lida no `LICENSE` do nupkg | Apache-2.0 para dependência direta de quem tem receita bruta anual abaixo de US$ 1 milhão, que é o caso hoje; reavaliar (licença comercial ou outra biblioteca) se a receita passar disso. Só o `Duora.Worker` a referencia. Presa à versão: cada atualização relê a licença |

## Escopo do SBOM

Os SBOMs descrevem as dependências declaradas no repositório, com as de desenvolvimento e teste incluídas,
porque elas rodam no CI e entram no inventário de licenças. O SBOM da release, por imagem e por digest, entra
com o gate de Release (spec 17.2), quando houver imagens.

## Ferramentas do host

Instaladas pelo Ansible na VPS, fora dos SBOMs do repositório (não são dependência declarada no código nem entram em
imagem). A licença é conferida contra a mesma allowlist de `scripts/supply-chain/license-policy.json` quando a
ferramenta entra.

| Ferramenta | Licença | Origem e fixação | Uso |
|---|---|---|---|
| restic | BSD-2-Clause (na allowlist; sem exceção) | pacote `restic` do componente universe do Ubuntu 24.04 (assinado pelo apt, série 0.16 da distribuição). Correções de segurança do universe chegam pelo ESM (Ubuntu Pro), não pelo `noble-security` comum: conferir a versão contra os avisos do restic a cada trimestre | backup dos derivados das fotos para o B2 (adendo do ADR-0010; role `backup`, só com `duora_media_backup_enabled`) |

## Lacunas conhecidas

- O Trivy não tem checks de misconfig para Docker Compose; `infra/compose/compose.dev.yml` não é coberto por
  ele (há `ComposeFileTests` no backend). Dockerfiles ainda não existem; quando entrarem, o mesmo passo os varre.
- Varredura de imagens (`trivy image`) entra com o gate de Release.
- O Dependabot não atualiza as imagens fixadas em `ci.yml` (`TRIVY_IMAGE`, `OASDIFF_IMAGE`, Gitleaks). A
  atualização é manual, pelo procedimento abaixo, ao menos uma vez por mês ou quando sair correção de segurança.
- As ferramentas Python (`tools/visual` e `scripts/deploy`) ficam fora do SBOM e do gate de licenças, que só
  leem `backend/` e `frontend/`. A licença do Pillow foi conferida à mão (exceção acima). ansible-core e
  ansible-lint são GPL-3.0-or-later (fora da allowlist): só rodam no CI e na máquina de quem opera, nunca
  entram no app nem nas imagens; incluí-los no SBOM exige antes uma exceção escrita.
- O analisador pip do Trivy v0.75.0 só lê arquivos chamados exatamente `requirements.txt`
  (`pkg/fanal/analyzer/language/python/pip/pip.go`, conferido em 2026-10-16). Por isso os lockfiles Python se
  chamam `requirements.txt`, com o `requirements.in` ao lado. Lockfile pip com outro nome fica fora da
  varredura de vulnerabilidades sem aviso.
- O lockfile é gerado por `uv pip compile --no-header`. O PR do Dependabot pip pode regenerá-lo com outra
  ferramenta ou formatação; quem revisa confere o diff e, se preciso, regenera com o comando do `requirements.in`.
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
