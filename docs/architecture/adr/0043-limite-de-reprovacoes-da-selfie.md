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
como `EXPIRED` ou `FAILED` não julgou a captura e passa a terminar `Expired`. A aprovação encerra a contagem e apaga as
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
`GET /me/selfie-verifications/current` e `selfieAttempts` em `GET /me` (só participante, e só até a aprovação). Nada
do fornecedor: nem score, nem motivo, nem instantes das reprovações.

**Bloqueio da conta (default a confirmar).** A décima reprovação grava, na mesma transação, o evento
`identity.selfie-attempts-exhausted` v1 (payload `userId`, `verificationId`) para o consumidor
`selfie-attempts-suspension`, sem teto de tentativas. O worker chama
`AccountSuspensions.SuspendAutomaticallyAsync(SuspensionOrder.Automated(userId, SelfieVerificationFailed, texto fixo),
ModerationCommandKey.ForSystem("selfie-verification", verificationId))` fora de transação. `Busy` reagenda em 5 s;
`Suspended` e `AlreadySuspended` concluem; conta apagada, em exclusão ou privilegiada conclui sem suspender (o limite
da selfie continua valendo); chave reusada falha para revisão. O consumidor roda mesmo com a selfie desligada. Até o
worker gravar a suspensão, o próprio limite já recusa novas tentativas.

**A exceção do ADR-0042.** É esta: a suspensão por `SelfieVerificationFailed` é automática, de autor `Automated`,
sem prazo (spec 4.2), e só a revogação por moderador a desfaz. Violação dos Termos continua exigindo pessoa (TM-34).
A automação é aceitável aqui porque a regra é objetiva, decidida pelo dono, conta só julgamentos do fornecedor sob a
política calibrada e tem revisão humana (a revogação) como saída.

**Revogação (default a confirmar).** Revogar uma suspensão por `SelfieVerificationFailed` apaga as reprovações na
mesma transação (operação do Identity chamada pelo Safety, como `RestoreAfterSuspension`): a conta volta ao primeiro
ciclo. A revogação de outra suspensão não mexe na contagem.

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

### STRIDE

| Ameaça | Mitigação | Teste |
|---|---|---|
| T: reentrega ou worker duplicado conta a mesma reprovação duas vezes | PK pela tentativa, fencing e estado terminal | `ARedeliveredResultAfterACrashAtCommitIsCountedOnce`, `TwoWorkersReadingTheSameResultInParallelCountItOnce`, `RecordingTheSameAttemptFromParallelTransactionsKeepsOneRowWithoutError`, `TheLedgerRefusesASecondRowForTheSameAttemptEvenWithoutTheService` |
| T: navegador burla a pausa | Pausa aplicada no servidor, sob a trava da conta | `FiveRejectionsLockTheSelfieUntilExactlyOneHourAfterTheFifth`, `TheLockedRequestAnswers429WithTheStableCodeAndTheSecondsUntilTheSecondCycle` |
| T: esperar a retenção de 30 dias zera o limite | Reprovação em tabela própria, sem FK para a tentativa | `TheTenthRejectionExhaustsTheAttemptsAndSuspendsTheAccountOnce` (contagem independe das tentativas) |
| D: falha do fornecedor ou abandono bloqueia a conta | Só julgamento final sob a mesma política conta | `AProviderOutageIsRetriedAndNeverCounted`, `ExpiryAndApprovalAreNotRejections`, `AResultUnderAnotherPolicyVersionEndsInconclusiveWithoutCounting`, `ASessionThatNeverProducedAJudgementEndsExpiredNotRejected` |
| D: bloqueio perdido por crash ou contenção | Outbox na transação, retry sem teto, lease | `ASuspensionBlockedByALockIsRetriedUntilItIsRecorded`, `ASuspensionLostWithTheWorkerIsTakenAgainAfterTheLease` |
| E: reentrega depois da revogação suspende de novo | Chave do comando pela tentativa (INV-06) | `ASuspensionRedeliveredAfterTheModeratorLiftedItDoesNotSuspendAgain`, `ASuspensionRedeliveredAfterItWasRecordedKeepsOneMeasure` |
| I: estado expõe dado do fornecedor | DTO com estado, contagem e instante de reabertura | `TheLockedRequestAnswers429WithTheStableCodeAndTheSecondsUntilTheSecondCycle` (chaves exatas) |
| R: suspensão automática sem trilha | Auditoria `safety.account_suspended` com autor `System` | `TheTenthRejectionExhaustsTheAttemptsAndSuspendsTheAccountOnce` |

## Compliance

- Unidade: `SelfieAttemptPolicyTests` (0, 1, 4, 5, 6, 9, 10 e 11 reprovações; fronteira de 1 h on/off point;
  estados que contam e que não contam).
- Integração (PostgreSQL real): `SelfieFailureLockoutTests` e `RekognitionSelfieProviderTests`.
- Frontend: `SelfieAttempts.test.tsx` (Vitest) e `e2e/selfie-attempts.spec.ts` (Playwright com axe, PT-BR e EN-US).
