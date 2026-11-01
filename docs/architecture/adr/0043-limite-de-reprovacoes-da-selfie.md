# ADR-0043: Limite de reprovações da selfie, com pausa de 1 hora e suspensão automática

- **Status:** Proposed (2026-10-30). A regra é decisão do dono (29/10/2026, issue #300); os defaults marcados abaixo
  aguardam a confirmação dele no PR, e o merge exige a revisão humana prevista para bloqueio (AGENTS.md).
- **Origem:** issue #300. Emenda o [ADR-0029](0029-confirmacao-email-presenca-sem-idade.md) (selfie) e registra a
  exceção que o [ADR-0042](0042-suspensao-de-conta-por-violacao-dos-termos.md) deixou condicionada a esta issue.
  Spec 12.2 (selfie), 8.2 (API-P05, P06, A08), 12.3 (automação e gate humano), INV-06 e INV-10.
- **Revisar quando:** existir o recurso de moderação (API-S06); a política da selfie (`PolicyVersion`) mudar de
  forma que reprovações antigas deixem de ser comparáveis; ou houver alternativa assistida à selfie.

## Contexto

Antes, a selfie só tinha o orçamento de 5 pedidos por hora, com 1 minuto entre eles (spec 12.2): quem reprovava
tentava de novo para sempre. O dono decidiu:

> Primeiro ciclo: 5 tentativas. Se todas forem reprovadas, a selfie fica bloqueada por 1 hora. Segundo ciclo, depois
> desse prazo: mais 5 tentativas. Se nenhuma for aprovada, a conta é bloqueada.

Ficaram abertas cinco questões: o que conta como reprovação; onde guardar a contagem sem perdê-la para a retenção de
30 dias das tentativas não aprovadas (ADR-0029); como aplicar a pausa no servidor sem corrida; como bloquear a conta
fora da transação do resultado sem perder o bloqueio; e o que a revogação do bloqueio devolve.

Alternativas consideradas:

- **Contar as linhas de `identity.selfie_verifications`.** Sem tabela nova, mas a tentativa não aprovada sai em 30
  dias: esperar o expurgo devolveria tentativas, e a contagem dependeria do job de retenção.
- **Contador por conta (`failures`, `locked_until`) com UPDATE.** Uma linha por conta, mas a idempotência por
  tentativa exigiria guardar o id da última contada, e `locked_until` duplicaria o que a quinta reprovação já diz.
- **Uma linha por reprovação, com a tentativa como chave primária (escolhida).** Reentrega não conta duas vezes por
  constraint, a contagem e a pausa são derivadas (nada de read-modify-write) e a retenção das tentativas não mexe nela.
- **Suspender dentro da transação do resultado.** A porta da suspensão abre a própria transação e recusa uma aberta
  (ADR-0042); juntar as duas também prenderia guardas e salas da Safety na transação do worker da selfie.
- **Suspender logo depois do commit, sem fila.** Um crash entre o commit e a chamada perderia o bloqueio.
- **Pedido de suspensão na outbox, na mesma transação (escolhida).** O worker chama a suspensão fora de transação,
  com a chave da tentativa; contenção reagenda e lease vencido devolve a entrega à fila.

## Decisão

**O que conta (default a confirmar).** Conta o resultado final do fornecedor que reprova a captura, sob a mesma
`PolicyVersion` da tentativa: `RecaptureRequired` (qualidade, pose, rosto) e `Inconclusive` (presença abaixo do
limiar). Não contam `Expired` (abandono ou prazo), a indisponibilidade do fornecedor (que só reagenda), o erro técnico
nem o resultado sob outra versão de política (que termina `Inconclusive`, sem contar). A sessão que a AWS encerra
como `EXPIRED` ou `FAILED` não julgou a captura e passa a terminar `Expired` (premissa: a documentação da AWS lista
`FAILED` sem dizer a causa; tratá-lo como falha técnica não está confirmado por ela). `SUCCEEDED` sem `Confidence`, sem
frame de referência, com frame vazio ou acima de 5 MB também termina `Expired`: a AWS recomenda repetir a verificação. A aprovação encerra a contagem e apaga as
reprovações. Regra pura em `SelfieAttemptPolicy` (Core).

**Contagem.** `identity.selfie_failures (verification_id PK, user_id FK ON DELETE CASCADE, failed_at)`, sem FK para a
tentativa (que sai em 30 dias). O worker grava com `ON CONFLICT (verification_id) DO NOTHING` na transação que fecha a
tentativa, já fencing-protegida e sob a trava da linha da conta (`identity.users FOR UPDATE`); a mesma trava serializa
o pedido de nova tentativa, então a pausa não tem janela de corrida. `failed_at` vem do `TimeProvider` da aplicação,
o mesmo relógio da fila e do pedido.

**Ciclos.** Até 4 reprovações: `Available` com o que resta de 5. Na quinta, `CoolingDown` até
`failed_at da quinta + 1 h`, intervalo semiaberto: no instante exato o segundo ciclo abre com 5. Da sexta à nona,
`Available` com `lastCycle`. Na décima, `Exhausted`. O orçamento por hora e o intervalo de 1 minuto continuam.

**Pedido (API-P05).** Sob a trava da conta, depois de devolver a tentativa aberta ou a aprovação (idempotente como
antes): `CoolingDown` → `429` `SELFIE_ATTEMPTS_LOCKED` com `Retry-After` em segundos até o segundo ciclo;
`Exhausted` → `403` `SELFIE_ATTEMPTS_EXHAUSTED`. Nenhuma tentativa nova é criada nesses casos.

**Estado (API-P06 e API-A08).** `attempts: { state, remaining, lastCycle, lockedUntil }` em
`GET /me/selfie-verifications/current` e `selfieAttempts` em `GET /me` (só participante, e `selfieAttempts` só até a
aprovação). Conta privilegiada não recebe nenhum dos dois; a regra mora no serviço. Nada do fornecedor: nem score, nem
motivo, nem instantes das reprovações. Enquanto o estado for `CoolingDown`, a tela revalida a consulta a cada
60 segundos, sem calcular a espera pelo relógio do dispositivo (issue #306). `lockedUntil` serve para exibir o
horário; `Retry-After` em segundos existe no POST recusado, não no GET de estado. Só uma resposta do servidor
liberando a tentativa devolve a captura, mesmo depois do horário exibido. Respostas idênticas mantêm a revalidação;
o Query cancela o intervalo na desmontagem ou na saída da pausa e aborta leituras pendentes na desmontagem.
Com a página ativa e online, a espera adicional é de até um intervalo, além da latência da consulta. Em segundo
plano permanece a política do Query de não consultar periodicamente e revalidar ao voltar ao foco.

**Permissões.** `duora_app` tem `SELECT`, `INSERT` e `DELETE` em `identity.selfie_failures`; `UPDATE` e `TRUNCATE` são
revogados na migração e de novo no `reconcile-privileges.sql`: uma reprovação gravada nunca é reescrita.

**Bloqueio da conta (default a confirmar).** A décima reprovação grava, na mesma transação, o evento
`identity.selfie-attempts-exhausted` v1 (payload `userId`, `verificationId`) para o consumidor
`selfie-attempts-suspension`. O worker, num loop próprio (`SelfieAttemptsSuspensionService`, separado do loop do
fornecedor e ativo mesmo com a selfie desligada), confere que a contagem ainda está esgotada e chama
`AccountSuspensions.SuspendAutomaticallyAsync(SuspensionOrder.Automated(userId, SelfieVerificationFailed, texto fixo),
ModerationCommandKey.ForSystem("selfie-verification", verificationId))` fora de transação:

- `Suspended` conclui. Conta apagada, em exclusão ou privilegiada conclui sem suspender (o limite da selfie continua).
- Contagem que acabou enquanto o pedido esperava (aprovação, revogação de suspensão por selfie) conclui sem suspender.
- **Outra medida ativa** (`AlreadySuspended` por motivo que não é a selfie, por exemplo violação dos Termos) **não
  conclui**: o pedido espera, porque se o moderador revogasse a outra medida a conta ficaria sem tentativas e sem
  suspensão por selfie para revogar. A espera cresce com a idade do pedido, entre 30 s e 1 h, e não gasta tentativa;
  quando a outra medida sai, a suspensão por selfie é gravada. Se a ativa já é por selfie, conclui.
- `Busy` (contenção de lock) adia 5 s, sem gastar tentativa.
- **Falha** (exceção em uma entrega) não trava o lote: a entrega volta à fila com o tipo do erro em `last_error` e recuo
  exponencial (5 s a 15 min). No teto de 10 falhas ela fica **morta**: `Failed` na outbox (estado terminal que a
  revisão de entregas falhas já cobre), log de erro só com o id da entrega e a métrica
  `duora.identity.selfie_suspension_requests.dead`. Chave reusada ou permissão negada também ficam mortas na hora.
  O pedido nunca some em silêncio; e o limite da selfie continua recusando tentativas enquanto isso.
- O prazo da entrega é longo de propósito (10 anos): esgotar depois do prazo viraria `Expired`, fora da revisão.

Para que esperar não conte como falha, a fila ganhou `OutboxDeliveryQueue.PostponeAsync`, que adia e devolve a
tentativa contada no claim.

**A exceção do ADR-0042.** É esta: a suspensão por `SelfieVerificationFailed` é automática, de autor `Automated`,
sem prazo (spec 4.2), e só a revogação por moderador a desfaz. Violação dos Termos continua exigindo pessoa (TM-34).
A automação é aceitável aqui porque a regra é objetiva, decidida pelo dono, conta só julgamentos do fornecedor sob a
política calibrada e tem revisão humana (a revogação) como saída.

**Revogação (default a confirmar).** Revogar uma suspensão por `SelfieVerificationFailed` apaga as reprovações na
mesma transação (operação do Identity chamada pelo Safety, como `RestoreAfterSuspension`): a conta volta ao primeiro
ciclo. A revogação de outra suspensão não mexe na contagem.

## Recuperação manual de entrega morta (#306)

Uma entrega `Failed` do consumidor `selfie-attempts-suspension` exige revisão do efeito de negócio. Quando não
resta entrega `Pending` ou `InFlight` para o evento, `OutboxDeliveryQueue.ScrubCompletedEventsAsync` substitui
`ops.outbox_events.payload` por `{}`. O evento conserva `resource_type = 'selfie_verification'` e `resource_id`
com o `verificationId`; `identity.selfie_failures.verification_id` permite localizar o `user_id` sem depender
do payload nem da tentativa original, que pode ter saído na retenção de 30 dias. Não há rota de reenfileiramento.

O operador autorizado faz o diagnóstico sob o procedimento de incidente; a decisão de moderação é humana.
IDs e evidências ficam no [registro privado do incidente](../../operations/support-incidents.md#registro-do-atendimento-e-do-incidente),
nunca numa issue pública, log de diagnóstico ou conversa com agente. Este procedimento não concede acesso de
produção ao agente nem autoriza uma suspensão por ele.

### Interface administrativa entregue (#319)

A interface é HTTP na mesma origem, sob `/api/v1/admin`, com cookies administrativos existentes. Não há novo
CLI, credencial de operador ou endpoint público. A rota de suspensão por Termos continua exclusiva de
`TermsViolation`. A decisão usa a sobrecarga de `AccountSuspensions.SuspendAsModeratorAsync` que recebe
`SelfieRecoveryRequest`; ela cria `SuspensionOrder.ByModerator` por `SelfieVerificationFailed` e compartilha os
efeitos de domínio da suspensão comum, sem transação externa em torno da porta pública.

1. **Diagnosticar:** `GET /api/v1/admin/selfie-suspension-recoveries/{deliveryId}`. Com sessão administrativa de
   moderador e prova privada do bootstrap `GET /api/v1/me` no header `X-XSRF-TOKEN`, devolve entrega, evento,
   tentativa, conta (nula se o vínculo foi removido), standing atual do ledger, revisão e medida ativa. Não
   devolve payload, dado do fornecedor ou motivo interno. Exige consumidor `selfie-attempts-suspension`, `Failed`
   com conclusão, tipo `identity.selfie-attempts-exhausted`, schema 1 e recurso `selfie_verification`.
   Sem correspondência: 404. `standing=LinkMissing` é diagnóstico, nunca autorização para inferir a identidade.
2. **Confirmar a intervenção humana:** `POST` na mesma rota, `Idempotency-Key` estável e corpo JSON:

   ```json
   { "incidentReference": "INC-EXEMPLO", "internalReason": "Justificativa privada da revisão humana." }
   ```

   `incidentReference` é opaca, de 1 a 64 letras ASCII/dígitos/`_`/`-`, sem nome ou identificador do participante;
   `internalReason` tem 1 a 2000 caracteres Unicode. Nenhum ID de conta, moderador ou sessão é aceito no corpo.
   A identidade vem da sessão autenticada. Exige conta ativa, e-mail confirmado, papel `moderator`, MFA habilitado,
   sessão `Administrative` não revogada/expirada, stamp atual e TOTP no intervalo fechado de agora menos 1 hora
   até agora. Código de recuperação não substitui TOTP. A operação não renova a prova nem a validade da sessão;
   conserva os controles normais de origem, CSRF e sessão. Permissão é revalidada dentro do domínio, inclusive
   antes de devolver um replay.
3. **Conferir o resultado:** 200 traz `deliveryId`, `disposition`, `suspensionId` (quando há medida), `recordedAt`
   UTC e `replayed`. `Suspended` cria decisão humana e seus efeitos; `ExistingSuspension` confirma a medida por
   selfie existente sem mudar sua autoria. `NoLongerNeeded` confirma que o ledger não está esgotado e
   `NotSuspendable` registra que o estado atual da conta não permite suspensão, sem alterá-la. Uma medida de
   outro motivo permanece pendente (`409 SELFIE_RECOVERY_AWAITING_OTHER_MEASURE`), sem revogação/substituição.
4. **Retomar sem novo efeito:** resposta perdida depois do commit pode ser repetida com a mesma chave, moderador
   e corpo. O registro privado imutável em `safety.selfie_suspension_recoveries` conserva entrega, evento,
   tentativa, conta, medida, moderador, incidente, motivo, instante e desfecho. Ele sobrevive ao expurgo da outbox,
   à limpeza do ledger e à revogação humana: replay retorna o resultado original, sem suspender de novo.
   Outra chave ou outro moderador para entrega já concluída recebe conflito, não uma nova decisão.
5. **Revisão atômica:** efeito de domínio, registro privado, `reviewed_at` e auditoria
   `ops.outbox_failure_resolved` com ator `Moderator` confirmam juntos. Uma falha em qualquer estágio reverte
   tudo; não há sucesso parcial a reconciliar manualmente. A entrega continua `Failed`, sem reset de tentativas,
   mudança de status, reconstrução do payload ou reenvio. Inscrições, salas, reservas e versão de segurança seguem
   a operação comum. O registro privado deve ser conferido com `safety.account_suspended` e a medida retornada.
   Não execute `retention review-delivery --resolution resolved` de novo para uma recuperação concluída por HTTP.
   `accepted-loss` continua separado, com aceitação humana explícita pelo procedimento operacional.

| Resultado | Conduta operacional |
| --- | --- |
| 400 / `IDEMPOTENCY_KEY_INVALID` | Corrigir o formato antes de enviar; corpo não aceita campos extras. |
| 401 / `REAUTHENTICATION_REQUIRED` | Reautenticar com TOTP pelo fluxo existente; não alongar a sessão por fora. |
| 403 | Rever a sessão, permissão ou prova CSRF/origem; não trocar identidade para forçar a decisão. |
| 404 / `NOT_FOUND` | Conferir entrega e diagnóstico autorizado; não reconstruir a ligação. |
| 409 / `SELFIE_RECOVERY_LINK_MISSING` | Vínculo removido (inclusive durante aprovação/revogação/exclusão): interromper e investigar. |
| 409 / `SELFIE_RECOVERY_ALREADY_REVIEWED` | Conferir o registro/auditoria anterior; não sobrescrever a revisão nem trocar a chave. |
| 409 / `SELFIE_RECOVERY_AWAITING_OTHER_MEASURE` | Manter o caso pendente para revisão humana posterior. |
| 409 / `IDEMPOTENCY_KEY_REUSED` | A chave já identifica outro pedido; investigar o registro original. |
| 503 / `TEMPORARILY_UNAVAILABLE`, `Retry-After` | Contenção (`Busy`): repetir controladamente a mesma chave e o mesmo corpo. |

A elegibilidade e o vínculo são relidos sob guardas ordenadas de usuários, trava da conta e da entrega;
planejamento de inscrições é refeito se o conjunto protegido mudar. Aprovação/revogação que apaga o ledger
antes da intervenção impede a recuperação. Diagnóstico é um retrato, não promessa de elegibilidade futura.
O registro não tem FK para a outbox ou a tentativa original; seus vínculos com contas/medida usam a mesma
preservação do histórico de moderação. É histórico privado de segurança, sem payload ou biometria, sem novo
expurgo automático. A migration aditiva é `20261101120000_SelfieSuspensionRecovery`; rollback mantém o registro
(e usa binário compatível), nunca aplica Down destrutivo. Merge exige revisão humana de bloqueio/autenticação
prevista no AGENTS.md. A feature não autoriza uso em produção nem altera as políticas aprovadas de retenção.

Fontes: [recuperação de domínio](../../../backend/src/Duora.Infrastructure/Safety/AccountSuspensions.SelfieRecovery.cs),
[fronteira HTTP](../../../backend/src/Duora.Web/Safety/SelfieSuspensionRecoveryEndpoints.cs),
[testes PostgreSQL](../../../backend/tests/Duora.IntegrationTests/Safety/SelfieSuspensionRecoveryTests.cs) e
[runbook operacional](../../operations/retention.md#recuperação-autenticada-por-selfie).

## Consequências

- Uma tabela a mais em `identity`, sem dado do fornecedor. Ela fica além dos 30 dias das tentativas não aprovadas
  enquanto a conta não aprovar: é o que impede zerar o limite esperando o expurgo. Isso muda o tratamento descrito no
  aviso de privacidade (abaixo).
- `GET /me` passa a ler `identity.selfie_failures`: o binário depende da migração `SelfieFailures`, que o `Down`
  recusa (apagar a tabela devolveria tentativas a quem as esgotou).
- Tentativas anteriores à migração não contam: a tabela nasce vazia, sem backfill.
- A selfie desligada (`SelfieVerification:Enabled=false`) é o rollback da captura; o limite e o consumidor seguem.
- Sem recurso no app (API-S06): até ele existir, quem esgota as tentativas só sai por pedido ao suporte e revogação
  do moderador.

### Aviso de privacidade

O tratamento muda em dois pontos, e o aviso publicado (`docs/legal/privacy/2026-10-25`) não os cobre: (1) uma decisão
automatizada com efeito sobre a conta (suspensão depois de dez reprovações), com revisão humana por pedido; (2) o
registro das reprovações (instante, sem imagem nem resultado do fornecedor) fica enquanto a conta não aprovar, além dos
30 dias hoje informados para as tentativas não aprovadas. Publicar nova versão do aviso é decisão do controlador e
passa pelo pipeline de documentos legais com fingerprints travados; este ADR não publica texto legal. O PR traz a
proposta de parágrafo em PT-BR e EN-US para o dono aprovar, e a ativação em produção depende dessa publicação.

As [minutas completas de 31/10/2026](../../privacy/proposals/2026-10-31-selfie-failures/README.md) detalham a
diferença entre tentativas e registros mínimos, a suspensão e o canal atual de revisão humana. Continuam não
aprovadas, fora do catálogo público; não alteram a versão vigente nem autorizam ativação.

### STRIDE

| Ameaça | Mitigação | Teste |
|---|---|---|
| T: reentrega ou worker duplicado conta a mesma reprovação duas vezes | PK pela tentativa, fencing e estado terminal | `ARedeliveredResultAfterACrashAtCommitIsCountedOnce`, `TwoWorkersReadingTheSameResultInParallelCountItOnce`, `RecordingTheSameAttemptFromParallelTransactionsKeepsOneRowWithoutError`, `TheLedgerRefusesASecondRowForTheSameAttemptEvenWithoutTheService` |
| T: navegador burla a pausa | Pausa aplicada no servidor, sob a trava da conta | `FiveRejectionsLockTheSelfieUntilExactlyOneHourAfterTheFifth`, `TheLockedRequestAnswers429WithTheStableCodeAndTheSecondsUntilTheSecondCycle` |
| T: esperar a retenção de 30 dias zera o limite | Reprovação em tabela própria, sem FK para a tentativa | `TheTenthRejectionExhaustsTheAttemptsAndSuspendsTheAccountOnce` (contagem independe das tentativas) |
| D: falha do fornecedor ou abandono bloqueia a conta | Só julgamento final sob a mesma política conta | `AProviderOutageIsRetriedAndNeverCounted`, `ExpiryAndApprovalAreNotRejections`, `AResultUnderAnotherPolicyVersionEndsInconclusiveWithoutCounting`, `ASessionThatNeverProducedAJudgementEndsExpiredNotRejected` |
| D: bloqueio perdido por crash ou contenção | Outbox na transação, espera sem gastar tentativa, lease | `ASuspensionBlockedByALockIsRetriedUntilItIsRecorded`, `ASuspensionLostWithTheWorkerIsTakenAgainAfterTheLease` |
| E: outra medida antes do consumidor deixa a conta sem saída depois da revogação | Pedido espera a outra medida sair | `AnotherSuspensionBeforeTheConsumerIsWaitedForAndTheSelfieSuspensionFollowsTheLift`, `WhileAnotherMeasureLastsTheWaitGrowsWithTheAgeOfTheRequestUpToOneHour`, `ARequestWhoseCountEndedWhileWaitingFinishesWithoutSuspending` |
| D: entrega com falha determinística trava o lote ou some | Teto de 10 falhas, estado `Failed`, log e métrica; loop próprio no worker | `ADeterministicFailureIsRetriedUpToTheCeilingThenMarkedDeadWithoutStoppingTheOthers`, `SelfieSuspensionQueueRunsInItsOwnLoopAndADatabaseFailureDoesNotStopTheWorker` |
| T: app reescreve a reprovação | `UPDATE`/`TRUNCATE` revogados (migração e reconcile) | `TheMigrationAloneKeepsTheAppFromRewritingARecordedRejection`, `AppRolePrivilegesTests`, `CanonicalReconciliationTests` |
| E: reentrega depois da revogação suspende de novo | Chave do comando pela tentativa (INV-06) | `ASuspensionRedeliveredAfterTheModeratorLiftedItDoesNotSuspendAgain`, `ASuspensionRedeliveredAfterItWasRecordedKeepsOneMeasure` |
| I: estado expõe dado do fornecedor | DTO com estado, contagem e instante de reabertura | `TheLockedRequestAnswers429WithTheStableCodeAndTheSecondsUntilTheSecondCycle` (chaves exatas) |
| R: suspensão automática sem trilha | Auditoria `safety.account_suspended` com autor `System` | `TheTenthRejectionExhaustsTheAttemptsAndSuspendsTheAccountOnce` |

## Compliance

- Unidade: `SelfieAttemptPolicyTests` (0, 1, 4, 5, 6, 9, 10 e 11 reprovações; fronteira de 1 h on/off point;
  estados que contam e que não contam).
- Integração (PostgreSQL real): `SelfieFailureLockoutTests`, `SelfieSuspensionRequestTests`,
  `RekognitionSelfieProviderTests` e `WorkerHostTests`.
- Frontend: `SelfieAttempts.test.tsx` (Vitest) e `e2e/selfie-attempts.spec.ts` (Playwright com axe, PT-BR e EN-US).
