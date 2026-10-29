# Matriz de testes de aceite

> **Decisão vigente — 19/10/2026, issue #93:** verificação etária foi retirada. Participação exige conta Active, e-mail confirmado e selfie de qualidade/presença Passed, além dos demais controles. Referências etárias remanescentes descrevem o sistema anterior ou questões jurídicas históricas; não são requisitos de implementação. Ver [ADR-0029](../architecture/adr/0029-confirmacao-email-presenca-sem-idade.md).


Matriz T-01 a T-28 da spec 20.2, com invariantes ([scope.md](../product/scope.md)), rotas da API (spec 8),
camada de teste prevista (spec 20.1) e marco (spec 22). O aceite é orientado por risco, não por percentual de
cobertura (spec 20.3).

## Como ler

- **Marco:** os testes listados para o M1 vêm da tabela da spec 22. Os demais marcos foram atribuídos pela
  dependência funcional (o marco em que a funcionalidade nasce) e estão marcados como *inferido*.
- **Camadas:** Unidade (xUnit v3, `TimeProvider` e semente injetados), Integração (Testcontainers +
  PostgreSQL 18 real, conexões concorrentes com barreiras), HTTP (`WebApplicationFactory` com clientes e
  cookies distintos), Frontend (Vitest, Testing Library, MSW), E2E (Playwright com contextos A, B e C),
  Carga (k6 e harness .NET SignalR), Resiliência (falhas injetadas), Recuperação (restore e VPS limpa),
  Processo (ensaio de permissões e revisão de configuração).
- **Status:** `não implementado` → `parcial` (cobre parte do cenário; diga qual) → `passando` (com link para a
  evidência do runner). Nenhum status muda sem artefato do CI ou relatório de ensaio (spec 25.19).

**Base da última conferência:** `main` em `cc92912`, com o CI verde (run 38837068022, jobs `backend`, `frontend`, `e2e`, `images`, `ops`, `secret-scan`, `contract` e `supply-chain`). Teste de unidade que cobre só a regra de domínio conta como `parcial` e diz que falta o resto da camada prevista; nenhuma linha está `passando`, porque nenhum cenário tem ainda todas as camadas previstas. Os PRs abertos citados na coluna de status ainda não estão na `main`.

## Matriz

| ID | Cenário | Resultado obrigatório | Invariante / API | Camada prevista | Marco | Status |
|---|---|---|---|---|---|---|
| T-01 | Conta pendente, suspensa ou sem confirmação de e-mail ou presença tenta participar | API nega, hub não concede contexto e inscrição não avança | INV-01; API-E03, API-E05, API-R01, hub | HTTP, Integração | M1 | parcial: policy `EligibleParticipant` por HTTP numa rota sentinela do ambiente `Testing` (403 para pendente e restrita; 200 para elegível, inclusive verificada há anos, porque a verificação adulta não vence; 503 sem banco) e enumeração das rotas de `/api/v1` fora da lista do titular (`EligibleParticipantPolicyTests`); faltam as rotas de evento e sala e o hub, que ainda não existem |
| T-02 | Dois coordenadores tentam reservar a mesma pessoa | Uma reserva; conflito tratado sem participante duplicado | INV-02; `BuildRoundPairings` | Integração (concorrência) | M2 (inferido) | parcial, só domínio: `PairSelectorPropertyTests.NobodyAppearsInTwoPairs` (ninguém aparece em dois pares, em qualquer ordem de entrada). Falta a reserva concorrente de dois coordenadores sobre o banco |
| T-03 | Bloqueio disputa com mensagem, pareamento e leitura | Operações ordenadas pelo commit; novas autorizações negadas após o bloqueio | INV-03; API-S01, API-C03, API-C04, API-P04, `BuildRoundPairings` | Integração (concorrência), HTTP | M1 (¹) | parcial: S01 e `CheckInteraction` (`BlockEndpointTests`, `BlockConcurrencyTests`, `InteractionCheckTests`: bloqueio idempotente, 2 bloqueios simultâneos → 1 registro, bilateral a partir do commit); no domínio, `InteractionPolicyTests` (bloqueio em qualquer direção barra a interação, veredito neutro) e `PairSelectorPropertyTests.NoPairIsBlocked` (par bloqueado nunca é formado). Faltam o ramo P04 (Profiles) e, no M2, mensagem e pareamento sobre o banco |
| T-04 | Duas decisões positivas simultâneas e seus retries | Uma conexão e uma conversa; escolhas privadas preservadas | INV-04, INV-06; API-R06 | Integração (concorrência) | M2 (inferido) | não implementado |
| T-05 | Participante ou terceiro tenta ler pista, decisão ou campo alheio | Payload não contém informação proibida, inclusive em erro e notificação | INV-05; API-R02, API-R05, API-P04, hub | HTTP, E2E (contexto C) | M2 (inferido) | parcial, só domínio: `ProjectionPrivacyTests` e `ConstellationPrivacyTests` (a projeção só traz os campos permitidos; pistas e escolha do par não aparecem antes da hora; quem está fora da dupla não recebe projeção; sessão expirada continua escondendo). Faltam HTTP, hub, erro e notificação |
| T-06 | Mesmo comando é reenviado e a chave é reutilizada com outro corpo | Efeito único; resultado seguro repetível; payload divergente rejeitado (`409 IDEMPOTENCY_KEY_REUSED`) | INV-06; `Idempotency-Key`, API-E03, API-R03, API-C04 | Integração, HTTP | M2 (inferido) | parcial, só domínio: `ActionLogTests.ResendingAnEqualCommandBuiltAgainReturnsTheOriginalReceipt` (reenvio devolve o recibo original), `ReusingAnActionIdWithAnotherOptionForTheSameStopIsRejected` (mesmo id com outro conteúdo é recusado), `ActionLogDoesNotKeepTheChosenOption` e `ActionFingerprintTests`. Faltam a idempotência HTTP (`Idempotency-Key`, `409 IDEMPOTENCY_KEY_REUSED`) e `ops.idempotency_records` |
| T-07 | Evento SignalR perde-se, duplica ou chega fora de ordem | Snapshot converge ao estado correto sem duplicar efeito | INV-07; hub `/hubs/updates`, API-R02, API-C03 | Frontend, E2E, harness SignalR | M2 (inferido) | não implementado |
| T-08 | Web ou worker reinicia antes/depois do commit, envio ou ACK | Job retoma com fencing; prazos e reservas consistentes | INV-08; outbox e jobs | Resiliência, Integração | M2 (inferido) | parcial, só a outbox de e-mail: `TransactionalEmailDispatchTests` (`TwoConcurrentWorkersDeliverEachEmailExactlyOnce`, `WorkerThatLostTheLeaseToAnotherCannotSettleTheDelivery`, `WorkerWhoseLeaseExpiredCannotSettleEvenIfNobodyTookOver`, `FailedSendIsRetriedAfterBackoffAndDeliveredOnce`, `DeliveryPastItsDeadlineExpiresWithoutSending`) e `ExponentialBackoffTests`. Faltam o reinício forçado antes/depois do commit, do envio e do ACK, e os jobs de prazo e reserva (ADR-0021, M2) |
| T-09 | Catálogo recebe versão nova durante a partida | Partida usa a versão antiga imutável até terminar | INV-09; API-ADM09, API-R03 | Integração | M2 (inferido) | parcial, só domínio: `ContentVersioningTests` (a sessão guarda a versão de conteúdo com que nasceu e projeta esse conteúdo; versões diferentes projetam o próprio conteúdo). Falta publicar versão nova no catálogo persistido durante a partida |
| T-10 | Moderador lê caso, aplica medida e outro revisa recurso | Acesso mínimo e auditoria completa de autor, política e resultado | INV-10; API-ADM11 a API-ADM15, API-S06 | HTTP, Integração | M1 (²) | parcial, domínio e trilha: `ModerationActionTests.ADecisionRecordsEverythingNeededToTraceAndReviewIt`, `AppealTests` (`TheAuthorOfTheOriginalDecisionCannotReviewTheAppeal`, `APartyToTheOriginalReportCannotReviewTheAppeal`), `ModerationActionTests.TheInternalReasonNeverReachesTheSanctionedPerson`; `AuditTrailTests` e `AuditPrivilegesTests` (`ops.audit_events`: o `duora_app` só faz INSERT e SELECT). Faltam as rotas de moderação (API-ADM11 a API-ADM15, API-S06), o acesso mínimo por HTTP e a auditoria de autor, política e resultado ponta a ponta |
| T-11 | Restore anterior a exclusão/revogação | Tombstones aplicados, sessões invalidadas e efeitos externos reconciliados | INV-11; procedimento de restore (spec 18.4) | Recuperação | M3 (inferido) | não implementado |
| T-12 | Conteúdo de issue tenta induzir agente a revelar segredo ou fazer deploy | Ambiente e permissões bloqueiam a ação; tentativa fica rastreável | INV-12; [permissions.md](../automation/permissions.md) | Processo | M0 (inferido) (³) | não implementado |
| T-13 | Login, logout ou escrita sem CSRF ou com origem indevida | Rejeição; endpoint público não significa mutação sem proteção | API-A01 a API-A05 e toda mutação cookie | HTTP | M1 | parcial: HTTP coberto por `CsrfProtectionTests` (token ausente, forjado ou sem o cookie; `Origin` e `Sec-Fetch-Site` estranhos; Vite só em Development; login, logout, cadastro e revogação → `403` `CSRF_VALIDATION_FAILED`, sem efeito) e por `CsrfEveryRouteTests` (toda rota registrada que não é GET, sem token e com sessão → `403`, enumerada pelo framework). Falta a evidência do runner no release e o hub (`/hubs/updates`) |
| T-14 | Cookie expira ou sessão é revogada com hub ativo | Novas ações negadas, conexão encerrada e cache privado limpo | API-A05, API-A10, hub | HTTP, E2E | M1 | parcial: parte HTTP em `SessionTests` e `PasswordResetTests` (logout, revogação por outro dispositivo, expiração ociosa e absoluta com `FakeTimeProvider` e reset de senha → `401` na requisição seguinte). E2E `session-privacy.spec.ts` (bloco T-14): sessão expirada no servidor faz a próxima leitura e a próxima escrita levarem à entrada com o aviso de sessão expirada, e quem entra depois não vê nada de antes; todo `401` em escrita reconsulta a identidade (`query-client.test.ts`), e `401 REAUTHENTICATION_REQUIRED` com `/me` válido mantém a pessoa na conta (`AccountPage.test.tsx`). Falta o fechamento da conexão do hub |
| T-15 | Container recriado e chave de Data Protection rotacionada | Sessões legítimas seguem a política; a chave não some por efemeridade | Data Protection (spec 11.2) | Integração (container), Recuperação | M1 | parcial: `KeyRingRecoveryTests` (host recriado com o mesmo key ring mantém a sessão; rotação mantém a chave antiga; key ring vazio ou revogado → `401`, nunca `500`; ticket ilegível → `401`), `KeyRingLossTests` (antiforgery → `403`, links → `400`), boot recusa key ring fora de volume montado (`StartupConfigurationTests`, `KeyRingMountTests`) e `ComposeFileTests` (volume só no web). A recriação do container real roda só no smoke local, fora do CI; falta a evidência do runner. A proteção em repouso (spec 11.2) está em `KeyRingCertificateTests` |
| T-16 | Upload inválido, enorme, animado ou com metadados de localização | Rejeição ou normalização segura e consumo limitado | API-M01, API-M02 | Integração, Unidade | M1 | parcial: recepção, idempotência, teto de pendentes e respostas sem chave em `PhotoUploadTests`; sanitização (SVG/HTML/executável, poliglotas JPEG+HTML, PNG+HTML e JPEG+ZIP, tipo trocado, bombas declaradas em PNG, JPEG e WebP, JPEG progressivo com scans demais e PNG com `zTXt` demais, APNG, WebP animado, EXIF/GPS, XMP, IPTC, texto PNG e ICC conferidos nos bytes dos derivados, conversão do ICC para sRGB, orientação, truncado inclusive com miniatura EXIF, teto de bytes, limites de memória e prazo cooperativo) em `PhotoSanitizerTests`; falha terminal (decoder que lança, storage que recusa derivados, original trocado ou ausente, entrega esgotada no claim) termina em `Rejected PROCESSING_LIMIT` sem original em `MediaPipelineTests`; teto global da fila sob concorrência em `UploadQueueLimitTests`; regras puras em `Duora.UnitTests/Media`. Fronteira `media-sanitizer` sem rede e transporte externo ao Worker implementados; a composição de provas usa harness standalone sem fixture de banco, matriz nativa e publicação licenciada. Exigir evidência do SHA final e homologação do host/release com #174/#86 antes de concluir este gate; P174-3/4 permanecem candidatos. Ver [composição](reports/media-proof-composition.md) e [roteiro operacional](reports/media-host-qualification.md) |
| T-17 | Provedor responde lentamente, falha ou repete callback | Timeout, dedupe, fila segura e nenhuma liberação indevida | INV-01, INV-06; `/webhooks/{provider}`, API-P05 | Resiliência, Integração | M1 | parcial: fornecedor lento ou falhando mantém a conta pendente (`AgeVerificationProviderDispatchTests`); callback repetido e concorrente com efeito único (`AgeWebhookTests`); falta o circuit breaker do adaptador real |
| T-18 | Relógio avança além do prazo ou usuário volta após longa queda | Sala expira corretamente; a rodada não se estende pelo relógio do cliente | INV-08; deadlines de sala e rodada | Unidade (`TimeProvider`), Integração | M2 (inferido) | parcial, só domínio: `DeadlineTests` (prazo de 8 min de jogo limitado a início da rodada + 9 min; um tick antes aceita, no prazo recusa; `HandleDeadline` determinístico, com `ManualTimeProvider`) e `ConstellationDeadlineTests`. Faltam a sala, a rodada do evento no servidor, o relógio do cliente e o job de expiração |
| T-19 | Aba A salva perfil e aba B tenta sobrescrever versão antiga | `412` ou reconciliação; sem perda silenciosa da primeira alteração | API-P02, API-P03 (`If-Match`) | HTTP, Frontend | M1 (inferido) | parcial: API-P02 coberto por `OwnProfileTests` (ETag desatualizado de outra aba → `412` e a primeira gravação fica; duas gravações concorrentes com o mesmo ETag, no INSERT e no UPDATE, têm um só vencedor; sem `If-Match` → `428`), por `ProfilePage.test.tsx` (conflito mostra a divergência, preserva o rascunho e reaplica sobre a versão atual; releitura em segundo plano não troca a versão-base) e por `e2e/profile.spec.ts`. Falta API-P03 (preferências, issue #9 item 4) e a evidência do runner no release |
| T-20 | Pessoa encerra conexão e uma ação antiga chega depois | Conexão não reabre; mensagem nova não é aceita | INV-03, INV-06; API-C02, API-C04, API-R06 | Integração, HTTP | M3 (inferido) | não implementado |
| T-21 | Identidade A faz logout e identidade B entra no mesmo navegador | Nenhum cache, foto, histórico ou socket de A aparece para B | API-A04, API-A05, hub | E2E, Frontend | M1 | parcial: E2E `session-privacy.spec.ts` (A sai e B entra sem rastro de A no DOM, no formulário nem no storage, que só guarda as chaves permitidas; saída e entrada em outra aba avisadas por `BroadcastChannel`; voltar no histórico com sessão expirada ou sem rede; restauração do bfcache simulada, porque o Playwright desliga o bfcache: página privada recarrega, pública mantém o rascunho), Vitest (`query-client.test.ts`: `/me` com outra pessoa descarta as leituras e as escritas da anterior; `session-hooks.test.tsx`: dado privado sai da memória quando a área desmonta; `identity-broadcast.test.ts`; `back-forward-cache.test.ts`) e HTTP `ApiResponseCachingTests` (`no-store` em toda rota da API, com e sem sessão). Faltam foto e socket, que ainda não existem no frontend |
| T-22 | Migração da versão anterior e rollback de aplicação | Nova release funciona; a antiga só volta com compatibilidade comprovada | Migrações (spec 17.4) | Integração (CI), Homologação | M0 (inferido) | parcial: `MigrationsTests` (`ModelHasNoChangesMissingAMigration`, `WebDoesNotMigrateOnStartup`) e `scripts/deploy/test_duora_deploy.py` (deploy sobre banco em migração anterior roda o migrator; rollback elegível não roda o migrator e mantém o esquema novo; rollback para release não declarada ou com banco além do esquema é recusado). Falta provar a compatibilidade da versão anterior com o esquema novo em release real (Homologação) |
| T-23 | Jornada com teclado, zoom, leitor de tela e movimento reduzido | Tarefa essencial concluída; A11Y-01 a A11Y-08 avaliados; saída e denúncia acessíveis | A11Y-01 a A11Y-08 | E2E (axe), avaliação manual | M3 | parcial: E2E com axe-core (WCAG 2.2 A/AA) em `smoke.spec.ts` (página inicial, rota inexistente, link de pular conteúdo), `auth.spec.ts` (estados de erro das telas de entrada; entrada só com teclado) e `i18n.spec.ts` (jornada em inglês, seletor de idioma pelo teclado). Faltam zoom, leitor de tela, movimento reduzido na jornada, saída e denúncia acessíveis, e a avaliação manual de A11Y-01 a A11Y-08 |
| T-24 | Carga de referência com imagens, jobs e perda parcial de rede | Metas medidas, limites respeitados e nenhuma invariante quebrada | Todas; metas da spec 19.1 | Carga | M3 | não implementado |
| T-25 | VPS inteira perdida e reconstruída das cópias externas | Serviço seguro recuperado; RPO/RTO medidos e lacunas registradas | INV-11; spec 18 | Recuperação | M3 | não implementado |
| T-26 | Asset apagado, reprovado ou perfil bloqueado acessado por URL antiga | Autorização atual nega; cache não reexpõe conteúdo | INV-03; API-M04, API-P04 | HTTP | M1 (inferido) | parcial: `MediaAssetReadTests` (API-M04 só do titular, `no-store`, 404 uniforme para alheio/inexistente/não aprovado/excluído, URL antiga negada logo após a exclusão), `PhotoDeletionTests` e `PhotoDeletionCleanupTests` (API-M03 lógica e física), `PhotoDeletionRecordPurgeTests` (tombstone e recibo saem só após 1 ano no relógio do banco e sem obrigação física pendente). Faltam perfil bloqueado (API-P04, sem seleção de foto de perfil ainda) e o ledger externo de tombstones para restore |
| T-27 | Replay de resposta idempotente depois de perder autorização | Autorização atual prevalece; não retorna payload privado antigo | INV-03, INV-06; `Idempotency-Key` | HTTP, Integração | M2 (inferido) | não implementado |
| T-28 | Lista de espera, cancelamento e check-in concorrem com lotação | Capacidade preservada e promoção de vaga determinística | API-E03, API-E04, API-E05 | Integração (concorrência) | M2 (inferido) | parcial, só domínio: `RegistrationTests` (última vaga vai ao primeiro e o seguinte espera; retiradas promovem em ordem; sem promoção a partir do instante de início), `CheckInTests` e `RegistrationPropertyTests` (CsCheck, 500 iterações: confirmados nunca passam da capacidade, fila em ordem de chegada). Falta a concorrência real sobre o banco |

Notas:

1. A spec põe T-03 no M1, mas pareamento e mensagem só existem no M2. No M1 o teste cobre bloqueio contra
   leitura de perfil; os ramos de mensagem e pareamento entram no M2.
2. A spec põe T-10 no M1, mas a administração completa é entrega do M3. No M1 o teste cobre denúncia,
   decisão e recurso pelas rotas mínimas de moderação.
3. T-12 é um ensaio de processo: verifica que o agente não tem caminho até segredos ou deploy, não um teste
   automatizado de código. Deve passar antes de qualquer dado real (gate de automação, spec 23.3).

## Verificações derivadas de ADRs

Não são cenários T-NN da spec, mas entram no aceite do marco indicado.

| Origem | Verificação | Camada | Marco | Status |
|---|---|---|---|---|
| [ADR-0016](../architecture/adr/0016-sala-e-sessao-de-jogo.md) | Rotas `/rooms/{roomId}` resolvem a `game_session` pelo mesmo ID; decisão única por `(game_session_id, user_id)` (coberto junto com T-02, T-04, T-05 e T-09) | Integração, HTTP | M2 | não implementado |
| [ADR-0017](../architecture/adr/0017-mfa-por-papel.md) | Moderador/administrador sem MFA confirmado é negado em `/admin/*`; API-A15 não remove o único método de conta privilegiada; participante ativa e desativa TOTP | HTTP | M1 | implementado (`AdminRouteTests`, `AdminMfaTests.APrivilegedAccountCannotDisableItsMfa`, `MfaLoginTests.AParticipantCanDisableMfaAndLogInWithThePasswordAgain`; feat/m1-admin-mfa) |
| [ADR-0018](../architecture/adr/0018-contrato-openapi-code-first.md) | OpenAPI versionado igual ao gerado; `oasdiff breaking` sem aprovação falha; tipos do frontend sem diff após regenerar | CI | M0/M1 | parcial: o job `contract` (`.github/workflows/ci.yml`) regenera o OpenAPI e os tipos e falha se divergirem do commit; passou na `main` em `cc92912` (run 38837068022). O `oasdiff breaking` só roda em PR e nenhum teste o força a falhar sem o label `breaking-change-approved` |

## Evidência por release

Cada release registra, por teste: commit, ambiente, dataset sintético, resultado e link para o artefato do
runner (template de relatório de teste, spec 21.3). Testes de carga e recuperação têm relatório próprio em
`docs/testing/performance.md` e `docs/operations/backup-restore.md` (a escrever).

## Fatia operacional de Rotas Improváveis — issue #100

Esta fatia acrescenta cobertura a T-05, T-09 e T-27 sem declarar os demais fluxos do M2 concluídos:

- `UnlikelyRoutesSnapshotTests`: formato JSON v1, versões, atribuição de pistas, restauração e recibos.
- `RoutesCatalogPublicationTests`: publicação incompleta recusada, equivalência bilíngue, áudio público,
  MFA e imutabilidade do tipo e do conteúdo; catálogo anterior preservado como Constelação.
- `RoutesParticipationTests`: PostgreSQL real, pistas individuais, reinício, alternância, reenvio,
  concorrência, revisão limitada, conclusão e liberação; bloqueio, inelegibilidade e prazo.
- `RoutesRoom.test.tsx` e `e2e-real/unlikely-routes.spec.ts`: etapas acessíveis e fluxo com duas pessoas,
  interface EN-US/PT-BR, áudio localizado e conclusão sem solicitar microfone.

Os resultados executados pertencem ao relatório da entrega; estes nomes são rastreabilidade, não evidência
por si só. Conteúdo editorial de produção e a matriz manual completa de acessibilidade permanecem pendentes.

## Compatibilidade de rotas da SPA (#105)

`routes.spec.ts` cobre caminhos portugueses e canônicos em inglês em PT-BR/EN-US, com Chromium, WebKit
e mobile Chromium: confirmação por código de oito dígitos e token legado, reset, retorno protegido,
rejeição de open redirect e remoção de credenciais da URL/histórico/storage. `legacy-routes.test.tsx`
e `login-state.test.ts` cobrem aliases e validação do estado do navegador. A suíte existente de
privacidade de sessão mantém T-14/T-21; os testes HTTP mantêm T-13 e autorização no servidor.
Inventário, contrato de e-mail e rollback: [spa-routes.md](../architecture/spa-routes.md).


## Evidências regionais integradas — #108 / ADR-0033 / etapa 10

As linhas históricas T-01 a T-28 não são reclassificadas por esta entrega. Para o recorte regional, a
[matriz atualizada](regional-matching.md#homologação-integrada--etapa-10) relaciona INV-01/02/03/05/06 a testes
concretos de domínio, PostgreSQL, HTTP, frontend/E2E e operação. O
[relatório de homologação](reports/2026-10-22-regional-homologation.md) separa execução remota, limites de volume,
gates técnicos e aceite operacional. Testes sintéticos não aprovam ativação nem encerram #108.


## Contrato editorial M0 e baseline S0 — #173/#174

A [matriz do contrato](../api/media-review.md#matriz-de-aceite-futuro) relaciona estados/replay, autorização,
transações e exclusão/restore a INV-03/05/06/08/11 e T-01/03/05/06/08/13/16/26 (INV-10 e T-10 saíram da
matriz de fotos em 28/10/2026: são do recurso de moderação). São provas exigidas
das próximas entregas, não aceite de runtime novo. O ensaio SanitizerResourceEnvelopeTests caracteriza
a implementação atual em processos novos, com corpus sintético, resultados repetidos, bytes/hashes, tempo
e RSS. Não impõe limites candidatos nem comprova sandbox, segurança da futura autoridade ou capacidade86.

**Decisões de 28/10/2026 (revisão de fotos sem recurso, revisor único).** T-10 continua sendo o recurso de
**moderação** (API-S06, API-ADM15) e não cobre fotos. Para fotos, o aceite passa a ser: rotas de recurso
inexistentes (`AppealRoutesNoLongerExistForOwnersOrModerators`), reprovação seguida de novo envio que abre
revisão nova decidida pelo mesmo moderador
(`SingleModeratorRejectsThenANewUploadOpensANewReviewThatTheSameModeratorApproves`), 404 uniforme no
histórico (`OwnerHistoryMasksOtherOwnersWithTheSameNotFound`), HumanReview admitida em Production e recusada
em Staging (`HumanReviewIngressPolicyTests`), allowlist de deploy `{"HumanReview"}` com inventário vazio
(`test_duora_deploy.py`) e a migração expand que limpa rejeições antigas e recusa recurso existente
(`PhotoReviewWithoutAppealsMigrationTests`). A matriz de autorização das rotas que restaram está em
[media-review.md](../api/media-review.md#matriz-de-autorização-28102026); os resultados pertencem ao CI do PR.

**Decisões de 29/10/2026 (espera de 7 dias).** A espera da fila vence exatamente em 7 dias: 7 dias menos 1 tick
não expira e 7 dias expira só como `PHOTO_REVIEW_EXPIRED`
(`ReviewExpirationUsesTheExactDeadlineAndCommitsOnlyASystemResultAndCleanupObligation`); um caso novo continua
pendente nas antigas 72 h (`ANewCaseIsStillAwaitingReviewAtTheFormerSeventyTwoHourDeadline`); um caso aberto pela
release anterior mantém as 72 h (`ACaseOpenedByThePreviousReleaseKeepsItsSeventyTwoHourDeadline`); e a constraint
aceita só 168 h ou os 72 h legados (`CaseDeadlineIsSevenDaysOrTheSeventyTwoHoursOfThePreviousRelease`). Ver o
[ADR-0035](../architecture/adr/0035-revisao-humana-de-fotos.md#decisões-de-29102026).

## Transporte Resend — conta e recuperação

`ResendEmailSenderTests` e `ResendEmailFlowTests` complementam INV-01/06/08/12: mensagens PT-BR/EN-US
via HTTP local, confirmação/reset com o conteúdo capturado, concorrência, cancelamento/retomada, backoff,
configuração inválida e falhas do provedor sem diagnóstico sensível. Ver [reprodução e limites](../operations/resend.md).
MFA: `LoginPage.test.tsx` aguarda o foco efetivo sem enfraquecer a exigência de acessibilidade.
