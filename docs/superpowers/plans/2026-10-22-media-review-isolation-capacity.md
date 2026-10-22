# Revisão humana, isolamento de mídia e capacidade — plano de implementação

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task when implementation is separately authorized. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Entregar #173 e #174 com provas de capacidade da #86, mantendo uploads desligados até todos os gates e uma autorização operacional própria.

**Architecture:** Media mantém uploads, arquivos, leases/fencing e publicação; Safety possui caso, atribuição, decisão e recurso. O decoder passa a um executável descartável sem rede, mediado por launcher rootless dedicado e protocolo fechado. Leitores e schema compatíveis precedem escritores de estados novos; revisão humana não ocupa o lease de sanitização.

**Tech Stack:** .NET e PostgreSQL já fixados no repositório; frontend TypeScript existente; GitHub Actions para toda execução pesada; Docker rootless dedicado somente em runners/ambiente de homologação explicitamente autorizado.

**Spec:** ADR-0035, ADR-0036 e adendo do ADR-0025, consolidados pelo PR #178; docs/product/spec.md 12.3/12.5; docs/operations/capacity.md; issues #173/#174/#86/#69. O plano não modifica decisões aceitas nem escolhe host, equipe ou ativação.

**Estado:** plano preparado em 22/10/2026. A autorização deste turno cobre integrar #178 e preparar este documento. Nenhuma tarefa de implementação abaixo foi executada ou autorizada por este plano. Base consolidada: PR#178 integrado em e77ed54b65251921b8902139556d073e73382729, head804304e927fbb0a6f6ebd9ac61bcb6a73d2e7395; CI39724832098 nove jobs aprovados. Árvore completa integrada idêntica à testada.

## Global Constraints

- Sem Docker, VM, instalação, restore, build, suíte ou carga no Mac de 16 GB. Leitura, edição documental, Git/gh e validação estática são permitidos; os comandos de teste abaixo são para runners GitHub Actions.
- Upload permanece desligado: `duora_media_review: ""`, `duora_media_backup_enabled: false`, allowlist de produção vazia até implementação completa e revisão própria. Sem implantação, provisionamento, compras, dados reais ou envio de alertas neste plano.
- TechnicalOnly fica restrita a Development/Testing; HumanReview é arquitetura aceita para os quatro ambientes conhecidos, Staging somente sintético. Nome desconhecido nunca vira permissão.
- Entrada mantém 5 MiB e 20 MP; thumb/card/full e controles atuais não são ampliados. Concorrência do sandbox: 1; lease técnico existente: 2 min; decode atual: 30 s; alocador ImageSharp atual: 256 MB.
- P174-3/4 são candidatos a ensaio: metadados 4 KiB; variante 16 MiB; saída total 24 MiB; stderr 64 KiB sem conteúdo em logs; sandbox 512 MiB/1 CPU/64 PIDs/tmpfs16 MiB/sem swap; envelope externo 50 s. Não registrar esses valores como homologados de produção.
- Cotas atuais de uploads pendentes: 3 por conta/100 globais, incluindo futura AwaitingReview. Não aumentar cotas para compensar indisponibilidade de revisão.
- HumanReview: foto apresentada como do titular; sem terceiros identificáveis, nudez/sexo explícito, violência gráfica, ódio ou contato exposto. Dúvida exige segunda análise. Sem reconhecimento facial, inferência de idade ou atributos pessoais.
- Preservar ADR-0034: legado padrão e opt-in de códigos de validação por requisição/confirmação. Códigos de resultado editorial pertencem ao status, não à negociação de errors.
- PostgreSQL real nos testes de SQL/locks/constraints. Nove checks no SHA final: backend, frontend, e2e, contract, ops, secret-scan, supply-chain, images amd64 e arm64. Não reduzir ou pular gates; workflows/proteções exigem alteração separada quando necessária.
- Mudanças de autorização, migração/contrato e launcher exigem revisão humana pertinente antes de integração/instalação. Issues #173/#174/#86/#69/#108 permanecem abertas até seus critérios próprios.

## Review Focus

1. Revogação/exclusão/restrição vence uma aprovação concorrente: não publicar, restaurar tombstone ou vazar evidência; testes nas tarefas M3/M4/M5.
2. Mensagem success do decoder seguida de crash, EOF inválido ou término desconhecido nunca libera publicação nem outro job concorrente; testes S2/S3.
3. Recurso ainda aberto no teto de retenção e candidato perdido em restore nunca geram aprovação automática; especificação M0 e testes M6.
4. Cliente/binário antigo diante de AwaitingReview, replay e códigos novos conserva semântica e dados; readers-before-writers e teste de rollback M1/M5.
5. Rootless configurado sem enforcement efetivo, ou memória do daemon/launcher/buffers fora do cálculo, reprova capacidade; provas negativas e medição C1/C2.

## Ordem e dependências

| Entrega | Issue | Depende de | Resultado revisável |
|---|---|---|---|
| M0 — contratos e falhas da revisão | #173 | #178 integrado | Especificação de estados, retenção/recurso e contrato, sem código de produção |
| S0 — baseline e corpus | #174/#86 | #178 integrado | Medidas do sanitizador atual e perfil candidato versionado; componentes futuros não são medidos aqui |
| M1 — leitores/schema compatíveis | #173 | M0 | Expansão aditiva e leitores, novos escritores ainda desligados |
| S1/S2 — protocolo e sandbox | #174 | S0 baseline | Executável candidato, launcher mínimo e contenção real nos runners; Worker atual preservado até S3 |
| M2/M3 — candidato e decisão | #173 | M1 | Área privada e transação de espera/decisão, sem produção ativa |
| S3 — integração do Worker | #174 | S2 | Sem fallback decode no Worker; retry/fencing preservados |
| M4/M5/M6 — interfaces, exclusão e retenção | #173 | M2/M3 | Revisão humana utilizável, limpeza, recurso e compatibilidade completos |
| M7 — coerência de política/infra | #173/#174 | S3 e M6 | HumanReview implementada e allowlist coerente; inventário continua vazio |
| C1/C2 — orçamento e homologação | #86 | S0 inicia; S3, M6 e M7 completam | Limites recalculados, enforcement e carga/restore no ambiente autorizado |
| A — aceite e fechamento | #173/#174/#86/#69 | todas | Evidências completas por issue; ativação continua ação separada |

Executar alterações compartilhadas de contratos/Media de forma serial em branches isoladas. S0 pode ser preparado sem dependência de M0; não há dependência circular entre revisão humana e decoder.

## M0 — fixar contratos e semântica antes de escrever novos estados

**Arquivos:** ADR-0035; docs/api/README.md; docs/privacy/retention.md; docs/data/model.md; docs/architecture/modules.md; docs/testing/acceptance.md. Criar especificação complementar em docs/api/media-review.md.

**Interfaces:** entrada é a política aceita; saída é uma tabela versionada de transições, respostas e prazos que M1–M6 implementam. Não criar uma denúncia fictícia para representar um upload.

- [ ] Inventariar API-M01/M02 atuais e o que falta de API-M03/M04 e T-26; delimitar leitura privada, exclusão, limpeza e uso em perfil sem presumir rotas existentes. Registrar o contrato HTTP completo antes de gerar tipos ou mapear endpoints.
- [ ] Fixar AwaitingReview no status público e replay; Rejected + PHOTO_REVIEW_REJECTED/EXPIRED/UNAVAILABLE, separados de motivos internos e errors. Preparar textos acionáveis PT-BR/EN-US e política de cache/ETag/autorização por operação.
- [ ] Especificar recurso vencido/sem revisor e preservação excepcional: nunca aprovar automaticamente; respeitar teto de retenção e independência. Uma política que estenda o teto aprovado ou modifique resultado exige decisão própria, não implementação incidental.
- [ ] Mapear cada operação a INV-03/05/06/08/10/11 e T-01/03/05/06/08/10/13/16/26. Revisar segurança/contrato antes de M1; nenhum código de produção é necessário nesta entrega documental.

## S0 — caracterizar o sanitizador atual e ensaiar os candidatos

**Arquivos existentes:** backend/src/Duora.Worker/Media/ImageSharpPhotoSanitizer.cs; backend/tests/Duora.IntegrationTests/Media/PhotoSanitizerTests.cs; TestImages.cs; backend/tests/Duora.UnitTests/Media/PhotoDecodeCostTests.cs; docs/operations/capacity.md.
**Criar:** backend/tests/Duora.IntegrationTests/Media/SanitizerResourceEnvelopeTests.cs; docs/testing/reports/media-sanitizer-resource-envelope.md; scripts/media/sanitizer-profile-v1.json (artefato de ensaio, não instalado).

**Interfaces:** preservar `Task<SanitizeOutcome> IPhotoSanitizer.SanitizeAsync(PhotoFormat declared, ReadOnlyMemory<byte> original, CancellationToken cancellationToken)`. O corpus produz manifesto sanitizado com hash/seed, tamanhos de entrada/saída por variante, pico RSS/cgroup, duração por fase e resultado do sanitizador atual.

- [ ] Montar corpus somente sintético: formatos permitidos, dimensões extremas válidas, orientação/ICC/EXIF, animação, poliglotas/tipo trocado, truncamento e bombas já cobertas; incluir saídas válidas próximas aos limites candidatos.
- [ ] Executar baseline no CI e comparar variantes, dimensões e conteúdo reencodado com o mesmo algoritmo/configuração; não aprovar truncamento de saída ou alteração visual como forma de caber.
- [ ] Nesta fase medir apenas decode, bytes/variantes, buffers e pico do processo atual. Startup, transferência, término/cleanup, margem de storage/commit e overhead de sandbox/launcher/runtime são medidos depois de S2/S3, nas tarefas C1/C2. O perfil inicial usa os candidatos aceitos, sem alegar que os componentes futuros já foram medidos.
- [ ] Se candidato recusar saída legítima, ultrapassar RSS ou não couber no lease, registrar resultado reprovado e revisão de P174-3/4. Não elevar silenciosamente os limites de upload, apagar teste ou anunciar capacidade de produção.

## M1 — preparar leitores, constraints, cotas e rollback compatíveis

**Modificar:** backend/src/Duora.Core/Media/UploadLifecycle.cs; MediaReview.cs; backend/src/Duora.Infrastructure/Media/MediaModelConfiguration.cs; MediaRecords.cs; PhotoUploads.cs; backend/src/Duora.Web/Media/PhotoEndpoints.cs; docs/api/openapi.json e frontend/src/shared/api/schema.d.ts conforme contrato M0.
**Criar:** migração aditiva gerada em backend/src/Duora.Infrastructure/Persistence/Migrations/; testes MediaReviewMigrationTests.cs e MediaReviewCompatibilityTests.cs em backend/tests/Duora.IntegrationTests/Media/; testes unitários de transições existentes.

**Interfaces:** UploadState.AwaitingReview e códigos editoriais definidos por M0; nenhum escritor HumanReview habilitado nesta entrega. Projeção mantém AwaitingReview explícito, sem mascarar como Processing.

- [ ] Escrever casos PostgreSQL para leitura/status/replay de cada novo estado, CHECK positivo/negativo, migração sobre dados anteriores e contagem consistente nas consultas de cota e índices.
- [ ] Rodar os casos no CI para demonstrar a lacuna existente; introduzir somente expansão/leitores/artefatos de contrato. Testar o consumidor frontend diante de estado desconhecido e novo estado explícito.
- [ ] Demonstrar que configuração padrão continua desabilitada e nenhuma migração cria consentimento, caso editorial ou aprovação para dados antigos.
- [ ] Identificar uma release de rollback com leitores compatíveis e ensaiar execução real dessa versão contra o schema expandido em PostgreSQL sintético. Recusa de Down, sozinha, não aprova binário antigo. Sem versão compatível, parada segura/correção para frente.

## S1 — extrair decoder e implementar protocolo limitado

**Criar:** backend/src/Duora.MediaSanitizer/Duora.MediaSanitizer.csproj, Program.cs e Media/ImageSharpPhotoSanitizer.cs; backend/src/Duora.MediaProtocol/Duora.MediaProtocol.csproj, RequestReader.cs, ResponseWriter.cs, ResponseReader.cs e ProtocolLimits.cs. Registrar novos projetos em backend/Duora.slnx.
**Modificar nesta entrega:** testes de sanitização existentes e inventários de dependência/licença/SBOM pertinentes. A remoção da referência/decoder do Worker e a nova sentinela de DependencyRulesTests pertencem a S3, após existir o transporte.

**Interfaces:** protocolo binário v1 sobre streams; requisição contém versão, nonce, formato, tamanho/hash e orçamento que só restringe o perfil. Resposta é rejeição técnica conhecida ou exatamente thumb/card/full com framing, dimensões e término íntegro. Não transmitir ownerId/chaves/URL/credenciais. O parser compartilhado não referencia ImageSharp.

- [ ] Escrever testes de parser: comprimento negativo/excessivo/overflow, metadados grandes, frames extras/duplicados/ausentes, versão/tipo desconhecido, hash/nonce errado, EOF antecipado e stderr excessivo. Excesso deve falhar antes de alocar o tamanho declarado.
- [ ] Congelar layout byte a byte e vetores binários na especificação do protocolo e nos testes antes da implementação dos dois lados. Esse detalhamento técnico deve preservar a direção aceita de P174-3.
- [ ] Criar o executável usando temporariamente link de compilação ao arquivo ImageSharpPhotoSanitizer.cs existente, sem duplicar/reformular o algoritmo nem remover a composição atual do Worker. Manter as sentinelas atuais e testar o corpus no novo executável. A transferência definitiva do arquivo, retirada da dependência do Worker e nova sentinela ocorrem juntas em S3. Essa transição é somente para dados sintéticos e nunca habilita dados reais.
- [ ] Reutilizar dependência/licença já aprovada, atualizar lockfiles e SBOM corretamente. Se o pipeline de imagens precisar mudar workflow, propor mudança protegida separada, sem enfraquecer checks.

## S2 — launcher restrito e fronteira real de execução

**Criar:** backend/src/Duora.MediaLauncher/{Duora.MediaLauncher.csproj,Program.cs,LaunchProfile.cs,JobSupervisor.cs,UnixControlServer.cs}; infra/images/media-sanitizer/Dockerfile; infra/media/systemd/duora-media-launcher.service; scripts/media/test_sanitizer_isolation.py.
**Modificar:** somente a documentação e infraestrutura necessárias em entrega própria; sem instalação automática ou habilitação na configuração de produção.

**Interfaces:** socket Unix autenticado por identidade/permissões locais com iniciar/consultar/cancelar por id opaco; comando e imagem/digest fixados no perfil. O Worker não fornece argumentos de runtime, path, mount, imagem, ambiente ou comando livres. stdin/stdout mediados pelo launcher; stderr limitado e descartado.

- [ ] Escrever provas negativas contra pedidos que tentem ampliar CPU/RAM/prazo, escolher imagem/host path/comando, atravessar diretórios, usar identidade indevida ou iniciar segunda tentativa antes do término confirmado.
- [ ] Implementar container por tentativa: rootless dedicado sob usuário próprio, rede none, UID não root, rootfs RO, capabilities removidas, no-new-privileges e seccomp revisado; sem secrets/storage ativo/banco/Docker socket no sandbox.
- [ ] Impor deadline externo e matar/confirmar término da unidade inteira; journal só com id opaco/estado/prazo/digest. Reinício limpa órfãos antes de aceitar trabalho; falha de confirmação bloqueia novas tentativas.
- [ ] No CI amd64/arm64, comprovar proibição de rede/DNS/storage/segredos; OOM/SIGKILL/timeout, stdout/stderr excessivos, crash depois de escrever success, reinício do launcher e arquivos órfãos. Inspecionar cgroups efetivos, não apenas YAML.
- [ ] Revisão humana de segurança da nova autoridade antes de qualquer instalação. Se rootless não aplicar os controles, registrar falha e reabrir a decisão; não substituir por daemon privilegiado.

## M2 — armazenamento privado e conclusão da sanitização sem publicação

**Modificar:** backend/src/Duora.Core/Media/{ObjectKey.cs,IObjectStore.cs}; backend/src/Duora.Infrastructure/Media/{FileSystemObjectStore.cs,MediaProcessingDispatcher.cs,MediaProcessingOutbox.cs}; scripts/deploy/duora_deploy.py, duora_backup.py e testes de montagens/backup pertinentes.
**Criar:** ReviewCandidateStore.cs na infraestrutura Media e testes ReviewCandidateStorageTests.cs/ReviewCandidateCommitTests.cs em integração Media.

**Interfaces:** nova StorageArea review-candidates privada, com chaves geradas pelo Worker por geração; resultado sanitizado de HumanReview cria candidato e AwaitingReview, nunca MediaAsset.Approved. Caso Safety e transição compartilham commit curto, sem I/O externo sob locks.

- [ ] Testar falha entre escrita de cada derivado, gravação do caso, commit e exclusão do original. Sem commit válido o arquivo não fica público; reexecução e limpeza não apagam geração vencedora.
- [ ] Acrescentar a área e seus marcadores/ACLs/montagens à validação de deploy. Web não recebe acesso de leitura geral nem caminho público para candidatos; leitura administrativa ocorre pelo serviço autorizado da tarefa M4.
- [ ] Garantir que backup de mídia continua cobrindo somente derivados finais; metadados/casos continuam no banco. Testar restore com candidato ausente e tombstones preservados; medir ocupação por cotas, gerações parciais e arquivos órfãos para o orçamento de disco de C1/C2.
- [ ] Encerrar lease/outbox técnica após commit da espera. Eliminar original após derivação/commit com limpeza durável em falha; não retê-lo esperando humano.

## M3 — caso, atribuição, decisão e recurso no Safety

**Criar:** backend/src/Duora.Core/Safety/PhotoReviewCase.cs; backend/src/Duora.Infrastructure/Safety/PhotoReviewService.cs e PhotoReviewTablesConfiguration.cs; testes PhotoReviewDecisionTests.cs, PhotoReviewConcurrencyTests.cs e PhotoReviewAppealTests.cs em integração Safety.
**Modificar:** catálogo fechado de auditoria existente, guardas e configuração do DbContext; não reutilizar reports de participante como se fossem casos de upload.

**Interfaces:** serviços de aplicação consumirão os comandos definidos em M0: identidade administrativa/atribuição, id do caso, versão esperada, decisão/motivo e chave de idempotência. Resultado separa sucesso repetido, conflito e acesso negado; persistência Media/Safety/auditoria deve ser atômica.

- [ ] Testar autorização por atribuição, sessão/MFA/step-up; operador/product_admin não recebem acesso implícito. Recurso exige pessoa diferente da decisão anterior.
- [ ] Testar decisão repetida e concorrente, versão vencida, permissão revogada e exclusão/restrição simultâneas. Conferir SQL independente e ausência de outbox/asset duplicado.
- [ ] Preparar promoção fora da transação; no commit revalidar caso, geração, guardas, conta e tombstone. Copiar arquivo não constitui publicação. Tentativa obsoleta não sobrescreve/apaga a vencedora.
- [ ] Manter reprovação/expiração sem sanção implícita da conta. Registrar versão da política e motivo interno estruturado, sem imagens/texto sensível nos logs.

## S3 — adaptar o Worker sem fallback e provar recuperação

**Criar:** backend/src/Duora.Worker/Media/IsolatedPhotoSanitizer.cs e testes de integração IsolatedPhotoPipelineTests.cs/MediaLauncherCrashTests.cs.
**Modificar:** WorkerHost.cs, Duora.Worker.csproj, opções/DI de processamento, testes de startup, referências/alias `worker::` dos testes de PhotoSanitizerTests.cs e backend/tests/Duora.ArchitectureTests/DependencyRulesTests.cs; transferir definitivamente ImageSharpPhotoSanitizer.cs ao novo executável e remover o link temporário. Manter contrato IPhotoSanitizer existente.

- [ ] Na mesma entrega compilável, substituir a implementação do Worker por transporte restrito, transferir o arquivo, retirar a dependência e trocar a sentinela arquitetural para exigir decoder somente no novo executável. Não carregar ImageSharp no Worker nem fallback local quando launcher falhar. Validar formato/estrutura da saída com custo limitado, sem segundo decode.
- [ ] Só aceitar success após término confirmado do container, saída íntegra e checks de nonce/versão/hash. Injetar falhas de banco/storage antes/depois do commit e perda de lease durante envio/cleanup; nenhum ACK prematuro/publicação parcial.
- [ ] Validar em startup o envelope total e a margem de armazenamento/commit contra o lease de 2 min; incluir atraso de cleanup/transferência e não validar somente o timeout de decode de 30 s. Preservar rejeições técnicas e PROCESSING_LIMIT quando aplicável; indisponibilidade usa retry limitado, sem Approved. Shutdown propaga cancelamento e deixa recuperação durável.
- [ ] Upload desligado tolera launcher ausente; política configurada só declara processamento pronto com perfil/imagem/protocolo compatíveis. Testar fila cheia e perda de garantia de isolamento sem admissão infinita.

## M4/M5 — operações HTTP, interface e exclusão

**Modificar:** backend/src/Duora.Web/Admin/AdminEndpoints.cs e Media/PhotoEndpoints.cs; frontend/src/shared/api/schema.d.ts; rotas administrativas do frontend conforme composição atual.
**Criar:** backend/src/Duora.Web/Admin/PhotoReviewEndpoints.cs; frontend/src/features/media-review/PhotoReviewPage.tsx e PhotoReviewPage.test.tsx; frontend/e2e/media-review.spec.ts; testes HTTP PhotoReviewEndpointTests.cs/PhotoReviewPrivacyTests.cs em integração Media.

- [ ] Implementar o contrato congelado em M0: listagem paginada sem thumbnails, leitura de evidência, decisão e recurso. Não expor path/chave/original, motivos internos ou dados de terceiros.
- [ ] Auditar leitura antes do primeiro byte; falha de auditoria nega transmissão. Testar permissões negativas, CSRF, no-store, ETag/versão e revogação concorrente pelo HTTP real.
- [ ] UI PT-BR/EN-US com teclado/leitor de tela/zoom, estados pendente/erro/conflito e confirmação de efeito. Não incluir detalhes de runtime/infra na interface de moderação.
- [ ] Implementar e provar o recorte faltante de API-M03/M04/T-26 definido em M0: exclusão do titular, tombstone/limpeza e leitura apenas de derivados aprovados conforme autorização. Não declarar essas APIs já prontas por existir upload.
- [ ] Testar replay/status AwaitingReview e os três códigos editoriais, preservando validação legado/opt-in. Regenerar OpenAPI/tipos e revisar a evolução semântica de contrato separadamente.

## M6 — retenção, expiração, recursos e restauração

**Criar:** backend/src/Duora.Infrastructure/Media/ReviewCandidateRetentionService.cs; backend/src/Duora.Infrastructure/Safety/PhotoReviewRetentionService.cs; backend/tests/Duora.IntegrationTests/Media/PhotoReviewRetentionTests.cs e PhotoReviewRestoreTests.cs. **Modificar:** docs/privacy/retention.md, docs/privacy/data-map.md e runbook de mídia.

- [ ] Relógio controlado testa limites: primeira análise alvo24h; espera72h; recurso até7dias; alvo de decisão7dias. Alvos de serviço não são timers de aprovação.
- [ ] Candidato rejeitado7dias; com recurso até encerramento+24h, máximo30dias desde rejeição salvo preservação excepcional explícita. Expirado limpa em até24h; decisão/auditoria mínima1ano sem imagem/texto sensível. Implementar retention_until e limpeza em lotes com fencing/idempotência.
- [ ] Cobrir recurso aberto no teto, expiração concorrente com aprovação/recurso, falha entre tombstone e arquivo, reinício e limpeza de órfãos. A semântica definida em M0 não pode ampliar retenção ou aprovar por ausência de revisor.
- [ ] Restore de banco sem bytes candidatos termina como PHOTO_REVIEW_UNAVAILABLE e permite novo envio. Backup imutável não é editado para simular exclusão imediata; reconciliar tombstones/retention antes de admitir leitura/processamento no ambiente restaurado.

## M7 — coerência de política, configuração e montagens sem ativação

**Modificar:** backend/src/Duora.Core/Media/MediaReview.cs; opções/startup de Web e Worker; scripts/deploy/duora_deploy.py (`PRODUCTION_MEDIA_REVIEW_POLICIES`); scripts/deploy/test_media_compose.py; infra/ansible/roles/app_dirs/tasks/main.yml e infra/ansible/roles/app_dirs/templates/duora.env.j2; infra/compose/compose.prod.yml; inventário/template que já declara duora_media_review vazio.
**Testes:** backend/tests/Duora.UnitTests/Media/MediaReviewPolicyTests.cs; integração de startup por ambiente/política; testes dos scripts de deploy e montagens. Usar o template duora.env.j2 existente, sem criar fonte concorrente de configuração.

- [ ] Só após S3/M6 e revisão do fluxo HumanReview, tornar domínio, Web, Worker e allowlist do executor coerentes com a política implementada. Não acrescentar HumanReview à allowlist como entrega antecipada para contornar startup.
- [ ] Matriz explícita: HumanReview permitida em Development/Testing/Staging/Production com requisitos da política; Staging exclusivamente sintético; TechnicalOnly somente Development/Testing; política/ambiente desconhecidos recusados. Configuração vazia mantém API503/MEDIA_UPLOADS_DISABLED e Worker sem consumo.
- [ ] Declarar criação, proprietário, UID/GID1654 quando aplicável, permissões/marcadores e montagens review-candidates nos alvos reais de Ansible/Compose, testando acesso mínimo entre Worker e serviço de leitura administrativa. Web não recebe leitura pública irrestrita; conta/atribuição continuam exigidas antes de qualquer byte.
- [ ] Preservar duora_media_review vazio e duora_media_backup_enabled false no inventário padrão; sem executar Ansible/Compose, instalar launcher ou mudar ambiente real. A existência de configuração declarativa não comprova montagem efetiva.
- [ ] Revisar a mudança de segurança/infra separadamente. Se esta tarefa não estiver integrada e testada, registrar o critério de coerência da #173 como pendente e não fechar a issue.

## C1/C2 — orçamento e prova de capacidade da #86

**Modificar:** docs/operations/capacity.md; scripts/deploy/test_capacity_budget.py; scripts/deploy/test_telemetry_capacity.py; scripts/telemetry/capacity.py e test_capacity.py quando o perfil exigir novas constantes; configuração do launcher/sandbox em entrega própria.
**Criar:** docs/testing/reports/media-capacity-homologation.md com manifesto de ambiente, resultados e decisão revisável.

- [ ] Recalcular primeiro: perfil remoto atual8512MiB Duora e3456MiB operações é baseline, não inclui novo sandbox/launcher. Mantendo Worker atual e somando candidato512MiB, o subtotal Duora já seria9024MiB, ainda sem daemon rootless/launcher e custos adicionais medidos. Não apresentar9024MiB como piso final aprovado. tmpfs/buffers cobrados no mesmo cgroup não devem ser somados duas vezes.
- [ ] Medir RSS/cgroup atual/pico, overhead do daemon/launcher, buffers simultâneos no Worker/sandbox, tmpfs, disco temporário, cache de imagens e concorrência com restic/full/diff/WAL. Não reduzir o Worker por presumir que remover o decoder libera memória suficiente.
- [ ] Testes estáticos rejeitam inventário ausente, memória/CPU/PIDs inválidos, ausência de enforcement e perfil que elimina a margem de host. Provar limites efetivos e pesos por cgroups, distinguindo teto, peso e reserva.
- [ ] Host isolado escolhido e autorizado: inventariar MemTotal/CPU/arquitetura/kernel/cgroupv2/IOPS/steal; repetir corpus, rampa/pico/reconexão, mídia concorrente com backup, saturação/recuperação e restore conforme capacity.md. Gerador de carga fora do host; dados sintéticos; três repetições e cinco minutos de aquecimento conforme protocolo existente.
- [ ] Preservar critérios já documentados: leiturasp95<=250ms, comandosp95<=300ms, reconciliaçãop95<=1s, falhas5xx+timeout/transporte<1%; sem OOM/restart/deadline perdido ou fila não drenada. Interromper por MemAvailable<512MiB/60s, swapping contínuo, disco>=85%, atraso de archive>=4min, falha de backup ou invariante. Documentar RPO/RTO observado; não inferir aprovação de100participantes.
- [ ] Sem host autorizado ou valores viáveis: relatório reprovado/pendente e proposta concreta de capacidade. Escolha de host/gasto e janela de carga permanecem decisão operacional; não contratar, provisionar ou usar produção como laboratório.

## Execução e evidências no GitHub Actions

Cada entrega passa por casos direcionados em runner, demonstração da falha pertinente antes da correção de comportamento e depois os nove checks completos no SHA final. Testes novos de caracterização não exigem fabricar defeito no produto. Registrar comando, SHA, arquitetura, seed, resultado e skips condicionais existentes.

Comandos futuros, **somente no runner**:

```sh
# Diretório backend; usar o filtro suportado pelo runner/testhost do repositório para casos direcionados.
dotnet tool restore
dotnet restore --locked-mode
dotnet build Duora.slnx
dotnet format Duora.slnx --verify-no-changes
dotnet test --solution Duora.slnx
# Diretório frontend, conforme workflow existente.
pnpm install --frozen-lockfile
pnpm typecheck
pnpm lint
pnpm format:check
pnpm test
pnpm build
pnpm e2e
# Diretório raiz.
python3 -m unittest discover -s scripts/deploy -v
```

Não executar esses comandos no Mac. CI de sandbox deve exercitar container/processo real, não somente mocks/YAML. Benchmark sintético no CI não substitui capacidade/enforcement do host. Logs não contêm fotos, IPs, endereços pessoais, tokens ou cabeçalhos de autenticação.

## Aceite por issue e decisões realmente remanescentes

| Issue | Condição de fechamento | Não comprova |
|---|---|---|
| #173 | Fluxo completo aprovado de espera/decisão/recurso, autorização/auditoria, contratos, exclusão/limpeza/restore e testes; todos os critérios da issue, inclusive aceite responsável/equipe quando exigido, comprovados sem transferir pendências para obter fechamento | Equipe nomeada/cobertura real ou ativação automática |
| #174 | Decoder fora do Worker, sem fallback; launcher/protocolo/isolamento efetivos em ambas arquiteturas; falhas/recuperação/segurança e homologação no host/release selecionados | Aprovação humana ou capacidade de toda a aplicação |
| #86 | Inventário, orçamento completo, enforcement, carga/backup/restore e revisão dos resultados no ambiente autorizado | Autorização de gasto, deploy ou lançamento |
| #69 | #173/#174 e todos os gates próprios B2/COMPLIANCE/versionamento/credenciais/montagens/verify/backup/restic/restore/capacidade concluídos, mais autorização de ativação | Fechamento por configuração do Compose ou pelo aceite dos ADRs |

Ainda dependem de decisão/ação do responsável: nomes de P173-7 (inclusive responsável pelo serviço, revisor e substituto independente), cobertura real, host/orçamento e ambiente/janela de homologação, além de autorização própria de implementação e, depois, instalação/release/ativação. Não repetir como pendentes as escolhas arquiteturais aceitas. Limites candidatos serão confirmados por evidência e revisão, não por nova escolha arbitrária.

## Primeira execução recomendada após autorização de implementação

M0 e S0 primeiro: congelar o contrato de revisão e obter números do sanitizador atual no CI. Depois M1 e S1/S2, antes de escritores HumanReview e conexão do Worker. Encerrar com interfaces/retenção, coerência M7 e C1/C2. O modelo de equipe não bloqueia desenvolvimento sintético; bloqueia prontidão operacional e ativação.

## Autorrevisão do plano

Cobertura: 7 escolhasP173, 6 escolhasP174 e critérios da#86 mapeados às tarefas. Cinco condições do Review Focus têm testes atribuídos. Novos projetos/arquivos estão explicitamente propostos; nenhum é tratado como existente. Interfaces concretas futuras dependem do congelamento documental M0/S1, preservando a porta existente. Sem alteração executável, limite de produção, rollout ou teste realizado por este plano.
