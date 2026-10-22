# S4 — Plano de distribuição do media-sanitizer

> Execução: superpowers:executing-plans, inline, com uma revisão independente da branch ao final de cada PR. O usuário autorizou concluir #174, depois #173/#108; este plano detalha P174-6 do ADR-0036 já aceito.

**Objetivo:** produzir e conferir a imagem licenciada e o conjunto imutável launcher/perfil, sem instalar ou ativar componentes.
**Arquitetura:** publicação complementar separada do transporte Coolify atual de cinco imagens. Manifesto vincula imagem, arquitetura, commit, launcher, protocolo e perfil específico do host. O pin confiável do manifesto é externo ao pacote. Prova sintética no CI não homologa o host da aplicação.
**Tecnologias:** Python3.12 padrão; .NET10/ImageSharp4.1.3; Docker/BuildKit/rootless; Actions/GHCR; Trivy/CycloneDX existentes. Nenhuma dependência nova da aplicação.
**Spec:** ADR-0036 P174-2/3/4/6; docs/api/media-launcher-control.md; docs/operations/capacity.md; issue174 e plano geral media-review-isolation-capacity.

## Restrições globais

- Mac16GB: somente leitura/edição, Git/gh e análise estática; nenhum Docker, VM, install, restore, build, suíte ou carga. Verificação executável só nos runners.
- Sem instalação, provisionamento, contratação, dados reais, ativação de serviços/upload/filtro regional, leitura de segredos ou comandos manuais em produção. Não cancelar nem manipular execuções/filas/gates para impor esse escopo.
- Preservar o manifesto e o transporte Coolify de cinco imagens, assets e publicação aprovados. Workflow protegido em PR separado.
- Manter candidatos P174-3/4: entrada5242880bytes/20MP; metadados4096; variante16777216; saída25165824; stderr65536; sandbox512MiB/1CPU/64PIDs/tmpfs16MiB/sem swap; alocador256MB/decode30s; envelope50s/lease120s. Não declarar homologação de produção.
- Perfil contém UID/GID/socket/paths do host. Perfil do runner não é universal de produção. O pedido de foto nunca faz pull.
- Nove checks gerais e os dois nativos pertinentes no SHA final. Licença somente por secret mount BuildKit; verificador existente confere histórico/camadas. Nenhum segredo/token de publicação em checkout de PR não confiável.
- #174 fica aberta até limpeza durável M2, distribuição completa e homologação de host/release, além dos demais critérios próprios.

## Foco da revisão

1. Reescrever manifesto e hashes do pacote não pode superar o pin confiável externo.
2. Arquitetura, imagem, commit, launcher/protocolo/perfil e prova devem ser do mesmo conjunto; digest de outra imagem não qualifica.
3. Symlink/hardlink/path extra, JSON com chaves duplicadas, tamanhos excessivos ou SBOM ilimitado devem ser recusados antes de uso.
4. Build Release não pode revelar a licença, publicar rebuild diferente do varrido ou consumir segredos em código de PR não confiável.
5. Prova do perfil sintético não aprova capacidade conjunta/backup/instalação; reversão nunca repõe decode no Worker.

## A — Contrato estrito do pacote (PR de código)

Arquivos: criar scripts/media/release_contract.py, scripts/deploy/test_media_release.py e docs/api/media-sanitizer-release.md.

Interface: verify_bundle(directory: Path, expected_manifest_sha256: str, expected_commit: str, expected_architecture: str, expected_scope: str) -> dict. Somente leitura; ValueError em divergência. Sem subprocess, rede, runtime, extração ou instalação.

Arquivos fixos: manifest.json, profile.json, launcher.py, docker_runtime.py, run_launcher.py, duora-media-launcher.service, sbom.cdx.json e isolation.json. Só basenames exatos; recusar links/não regulares/arquivos extras. Tetos: manifesto16KiB; perfil4096; prova65536; launcher256KiB por arquivo; SBOM8MiB.

Manifesto v1: schema1; scope synthetic-candidate ou host-candidate; commit40hex; arquitetura amd64/arm64; image ghcr.io/bipoxdxd/duora-media-sanitizer@sha256:64hex; imageConfig sha256:64hex; launcherVersion s2-control-v1; protocol {control:1,stream:1}; launcherUid positivo; profileSha256; files com sete hashes SHA256; ci/isolation/publication cada {run,attempt} positivos. Nada de campos/chaves duplicadas/booleanos como inteiros ou scope production-approved.

Perfil passa pelo Profile.from_dict existente; imagem e Profile.digest devem coincidir com o manifesto e pin S3. Prova contém complete=true/candidateOnly=true, arquitetura nativa, os14casos reais únicos aprovados, controles efetivos/runtime, configID e os novos vínculos imageReference/profileSha256. O produtor B deve registrar o perfil efetivamente ensaiado; não preencher vínculos num relatório antigo por suposição. Hashes só asseguram integridade sob um pin externo confiável; proveniência vem do workflow validado.

- [x] Enviar testes positivos/negativos primeiro e observar RED no Actions. O teste ausente falha por asserção explícita, sem erro acidental de importação.
- [x] Implementar leitura limitada, hash externo, JSON fechado, vínculo exato e retorno verificado. Usar os testes completos já escritos em scripts/deploy/test_media_release.py como contrato executável.
- [ ] Casos: pacote válido/ARM64; adulteração conjunta de payload+manifesto; digest/tag/repo/commit/scope/protocolo/UID incorretos; arquivo ausente/extra/adulterado/link; limite; perfil canônico errado; prova incompleta/falha/duplicada/não nativa/configID/imagem/perfil divergentes; SBOM e referências inválidos.
- [ ] Observar GREEN no ops e nove checks + matriz pertinente, revisão independente única, merge da árvore final testada. A não afirma construir/publicar a imagem licenciada.

## B — Publicação nativa licenciada (PR protegido)

Arquivos: infra/images/media-sanitizer/Dockerfile; extensão restrita de scripts/media/test_sanitizer_isolation.py; helpers/testes de release; novo .github/workflows/media-release.yml; docs/operations/media-sanitizer-release.md.

- [ ] Novo target Release explícito, preservando targets sintéticos. Mesmas bases fixadas/restore locked. Usar RUN --mount=type=secret,id=sixlabors_license,required=true dotnet publish ... -c Release -p:SixLaborsLicenseFile=/run/secrets/sixlabors_license. Copiar apenas publish, UID1654, nenhuma licença em ARG/ENV/camada.
- [ ] PR valida código sem credenciais de publicação. Dispatch só tooling confiável da main e SHA completo. Conferir os nove jobs completed/success no push main, repo/workflow/run/attempt exatos. A própria publicação exige duas provas nativas novas da imagem Release antes de aprovar o manifesto; não substituir por uma prova antiga de outro commit.
- [ ] Runners nativos: build único; check_image_secret; Trivy/política atual; SBOM. Preparar rootless só no runner descartável. Publicar candidato nativo varrido com identidade commit/run/attempt/arquitetura; pré-carregar digest imutável fora do launcher; nenhum segredo herdado pelo decoder.
- [ ] Executar os14casos reais S2: falhas com probe separado e sucesso do sanitizador/template com esse digest Release sem rebuild. Registrar referência/configID/runtime/perfil real. Gerar pacote e hashes com scope synthetic-candidate; nada de installer ou comando no host da aplicação.
- [ ] Agregar apenas digests/provas das duas arquiteturas deste mesmo workflow/attempt/commit. Inspecionar e verificar índice multiarquitetura. Só aprovar manifesto final com ambas as provas verdes. Não alterar o conjunto de imagens do Coolify.
- [ ] Testes recusam substituição de commit/arquitetura/run/attempt e gates ausentes. Provas Docker/registry reais só no Actions.
- [ ] Nove checks e matriz pertinentes no SHA final, revisão independente, merge. Após gates da main, executar dispatch e ler evidência real de licença/camadas/SBOM/imagem/prova/pacote. Falha mantém release reprovada, sem waiver.

## C — Homologação e fechamento

- [ ] Relatório concreto de digests/pins e requisitos restantes. Antes de instalação/carga, obter host isolado selecionado, orçamento e janela autorizada; não inferir produção da ordem de fechar issues.
- [ ] Conferir com o dono da M2 a limpeza durável de originais; não fechar174 enquanto best effort.
- [ ] Procedimento sintético aprovado no host/release: medir daemon/launcher/buffers do Worker, startup/transferência/cleanup/storage/commit e backup concorrente; recursos efetivos/limiares de parada da86; registrar arquitetura/kernel/runtime/imagens/perfil sem dados/segredos.
- [ ] Atualizar T16/ADR/runbook/issue com provas reais. Fechar apenas todos os critérios; falta de host autorizado é bloqueio real, não motivo para transferir aceite ou chamar CI de homologação operacional.

## Verificação (somente no runner)

python3 -m unittest discover -s scripts/deploy -v; Pyright da CI; nove checks gerais; Media isolation nativo quando pertinente; dispatch protegido para Release após merge/gates. Não executar suítes no Mac.

## Coordenação

Esta entrega cuida da174/S4. M2-M7 da173 e sua limpeza durável são de outra implementação autorizada; alinhar WorkerHost/Compose/Ansible antes de M7. A confirmação pós197 é acompanhada separadamente. Não duplicar writers nem pushes concorrentes. Instalação/ativação/gasto continuam decisões próprias.
