# M1 — compatibilidade dos leitores da revisão de fotos

Recorte do plano #173/#174 após M0/S0 (#181). Referências: ADR-0035, contrato
[media-review.md](../../api/media-review.md) e #173. P174-3/4 continuam candidatos de homologação.

## Entrega e limites

Preparar `AwaitingReview` e os resultados `PHOTO_REVIEW_REJECTED`, `PHOTO_REVIEW_EXPIRED` e
`PHOTO_REVIEW_UNAVAILABLE` para leitura/status/replay. Os códigos editoriais são resultados do upload,
separados das rejeições técnicas do decoder e da negociação de `errors` do ADR-0034.

O estado novo ocupa vaga nas cotas existentes de 3 por conta/100 globais, mesmo se a espera estiver antiga.
Expiração editorial é uma transição durável futura; não liberar vaga apenas por comparar o relógio na consulta.
O índice parcial inclui a espera; os índices e prazos de abandono técnico não passam a executar retenção humana.

O schema mínimo `safety.photo_review_cases` prepara a raiz do caso que M2 gravará atomicamente com a espera:
id, referência única ao upload, geração do candidato, versão da política, início/prazo da revisão e versão
otimista. Não há seed de casos, consentimento, candidatos ou assets, nem serviço que escreva essa tabela.
O mapeamento/serviço do caso, atribuição, histórico de decisões/recurso, auditoria e retenção são posteriores.

Nenhum escritor entra em AwaitingReview nesta entrega. `HumanReview` ainda não é política configurável;
TechnicalOnly continua restrita a Development/Testing. A máquina de estados não libera aprovação/reprovação
da espera por simples adição do enum. Upload, backup de mídia e allowlist de produção mantêm seus padrões.

## HTTP, tipos e consumidores

API-M01 conserva 202, Location e photoId/status. API-M02 conserva photoId/status/rejectionCode e no-store.
Não adicionar assetId, ETag, rotas de decisão/recurso/exclusão ou leitura de imagens neste recorte: M4/M5
implementarão esses contratos. O resultado editorial não produz nova mensagem remota localizada na API.
As mensagens PT-BR/EN-US da M0 serão ligadas à interface nas entregas de UI, sem texto sensível do caso.

Status e rejectionCode já são strings no DTO, OpenAPI e tipos gerados; não fechá-los em enum de transporte.
O check contract regenera os artefatos e exige igualdade com o commit. Não há mudança estrutural a gerar.
A SPA ainda não possui consumidor de status/tela de fotos; o teste usa seu transporte real e os tipos
gerados para preservar o novo estado e um estado futuro sem reinterpretá-los como Approved nem repetir POST.
Isso não comprova uma interface de revisão pronta. Consumidores externos não foram identificados no projeto
pré-lançamento (#152); nenhuma compatibilidade com software externo desconhecido é presumida.

## Matriz de evidência no CI

| Prova | Teste |
|---|---|
| Status/replay após reinício de host, 409 para chave com outros bytes, isolamento por titular e nenhum efeito duplicado | MediaReviewCompatibilityTests.RestartedReadersPreserveStatusReplayAndOwnerIsolation |
| Espera antiga conta nas duas cotas; resultado final libera vaga | MediaReviewCompatibilityTests.OldReviewWaitsStillOccupyAccountAndGlobalSlots |
| Estados, códigos e combinações inválidos recusados por PostgreSQL | MediaReviewCompatibilityTests.InvalidStatesAndResultCombinationsStayClosed |
| Todos os seis estados anteriores preservados byte a byte na expansão; zero casos/assets/consentimentos criados | MediaReviewMigrationTests.ExpansionPreservesEveryLegacyStateAndCreatesNoCasesOrAssets |
| Índice parcial cobre espera; fingerprint continua obrigatório | MediaReviewMigrationTests.ExpandedPartialIndexCoversReviewWaitsAndStillRequiresAnOriginalFingerprint |
| Identidade única do caso, FK e checks de política/prazo/versão | MediaReviewMigrationTests.CaseIdentityIsUniqueAndDeadlineAndPolicyCannotBeInvalid |
| Preparação do leitor não habilita escritores/aprovação implícita | UploadLifecycleTests.ReaderPreparationDoesNotEnableAnyReviewWriterOrImplicitApproval |
| DTO aberto e transporte real da SPA diante de estado conhecido/desconhecido | photo-status-compatibility.test.ts |
| Padrão sem uploads e compatibilidade legado/opt-in | PhotoUploadsDisabledTests e PhotoValidationCompatibilityTests existentes |

Os testes de SQL usam PostgreSQL real e bancos sintéticos isolados. Nenhuma execução de build/teste/Docker
ou instalação ocorre no Mac. Resultados e SHAs efetivos são registrados no PR; esta matriz não presume sucesso.

## Sequência de release e reversão

1. Integrar e publicar uma release com estes leitores antes de integrar/habilitar escritores novos.
2. Antes de M2 escrever estados/casos, fixar commit e digests imutáveis dessa release como base compatível.
3. Ensaiar a execução do **binário dessa release**, sem recompilá-lo, contra PostgreSQL expandido com registros
   sintéticos de todos os estados/códigos e dados de casos/candidatos das etapas posteriores. Revalidar essa base
   a cada expansão posterior. Sem essa prova, bloquear escritores e registrar a lacuna.
4. Este teste reinicia dois hosts da mesma versão; comprova leitura persistida, não retorno a uma release
   histórica nem compatibilidade futura de um schema ainda inexistente. Não existe release antiga já homologada
   como rollback para AwaitingReview. O SHA final do PR é candidato a primeira base, sujeito a integração/release.
5. Se faltar versão de retorno comprovada, interromper admissões/consumidores e corrigir para frente. Não voltar
   a binário que desconheça o estado, apagar casos ou reclassificar pendências como Approved. Down destrutivo é
   recusado; essa recusa sozinha não prova compatibilidade.

M1 prepara leitores e persistência; não conclui #173, #174, #86 ou #69. A prova operacional de retorno continua
gate explícito antes dos escritores; M2–M7, S1–S4, capacidade/enforcement e nomes/cobertura P173-7 continuam
necessários. Nenhuma etapa deste relatório autoriza instalação, release operacional ou ativação do upload.
