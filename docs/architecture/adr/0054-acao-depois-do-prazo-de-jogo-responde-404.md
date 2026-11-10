# ADR-0054: Ação depois do prazo de jogo responde 404

- **Status:** Accepted (2026-11-10), decisão da coordenação com delegação do dono.
- **Origem:** PR #446; INV-08 (prazo da sala); T-18 e TM-24 (`docs/testing/acceptance.md`, `docs/security/threat-model.md`)
- **Revisar quando:** a sala `Expired` deixar de responder `404` nas rotas de ação, ou o replay idempotente mudar de regra

## Contexto

Entre o prazo de jogo e a execução do job `game-deadline`, a sala segue `Active` no banco. Nessa janela, uma ação nova em
`POST /api/v1/rooms/{roomId}/actions` ou `/routes-actions` recebia `400 VALIDATION_FAILED`; depois do job, `404`, a
resposta da sala `Expired`. A mesma ação atrasada tinha duas respostas, conforme o job já tivesse rodado ou não.

O problema é de consistência de contrato, não de vazamento. Só o participante recebia esse `400`, e ele já vê que a sala
existe e qual é o estado dela pelo `GET /api/v1/rooms/{roomId}`. Terceiros já recebiam o `404` neutro, porque a leitura
da sala filtra pelo participante (`LoadRoomAsync`, em `ParticipationService.Rooms.cs`).

Alternativas consideradas:

- **Manter `400` antes do job e `404` depois.** Sem mudança, mas o status muda conforme o job, e o cliente precisa
  tratar duas respostas para o mesmo caso.
- **`404` sempre.** A ação atrasada recebe uma resposta só, igual à da sala `Expired` depois do job.
- **`409` ou `410`.** Uma resposta só também, mas diferente da que a sala `Expired` já dá; seriam duas respostas para
  "a sala não aceita mais ações".

## Decisão

Uma ação **nova** no prazo de jogo ou depois dele, em `actions` e `routes-actions`, recebe o mesmo `404 NOT_FOUND` de uma
sala inexistente, antes e depois do job `game-deadline`, mesmo com `expectedVersion` desatualizado (o prazo é checado
antes da versão). Antes do prazo, os outros motivos de recusa seguem `400 VALIDATION_FAILED`. Decisão da coordenação,
10/11: o motivo é consistência de contrato, a mesma resposta antes e depois do job, igual à da sala `Expired`.

A regra vale para a sala que ainda está `Active` no banco. Ela **não cobre a sala `Completed`**: uma ação nova numa sala
`Completed`, antes ou depois do prazo, continua recebendo `400 VALIDATION_FAILED`. O motor checa o fim da sessão antes do
prazo (`ExperienceEngine.ValidateAction` devolve `SessionFinished` antes de `DeadlineReached`), e as rotas só respondem
`404` direto para as salas `Aborted` e `Expired`.

## Consequências

- Cliente que tratava `400` depois do prazo passa a receber `404`. O frontend deve tratar `404` como sala encerrada ou
  indisponível. Hoje, em `frontend/src/features/participation/RoomPage.tsx`, o erro de ação mostra o
  `RequestFailureAlert` genérico e relê a sala; o aviso neutro `participation.unavailable` (`RoomFailure`) cobre só o
  `404` da leitura.
- Resíduo aceito: o reenvio idempotente e o `actionId` reutilizado seguem a regra de replay até o job (`204` sem gravar
  de novo e `400`) e dão `404` depois dele. Só o autor vê a diferença, e só sobre a própria ação; fechá-la quebraria a
  idempotência.
- Lacuna conhecida: a sala `Completed` fica fora da regra e responde `400 VALIDATION_FAILED` a uma ação nova, antes e
  depois do prazo. Este ADR não muda esse comportamento; trazê-la para o `404` pede nova decisão da coordenação.
- O OpenAPI ainda não declara `400` nem `404` nessas rotas. Fica para PR próprio: #449.

## Compliance

Testes que comparam status, corpo sem `traceId` e headers com os de uma sala inexistente, pelo helper
`NeutralResponse.DescribeAsync` (`backend/tests/Duora.IntegrationTests/Participation/NeutralResponse.cs`):

- `GameDeadlineTests.AnActionAtOrAfterTheDeadlineButBeforeTheJobIsRefusedWithoutEffect` (antes e depois do job) e
  `GameDeadlineTests.ANewActionWithAStaleVersionAtTheDeadlineGetsTheSame404`;
- `RoutesParticipationTests.AnActionAtOrAfterTheDeadlineGetsTheSame404BeforeAndAfterTheDeadlineJob` e
  `RoutesParticipationTests.ANewActionWithAStaleVersionAtTheDeadlineGetsTheSame404`.

O resíduo e o `400` antes do prazo ficam fixados em `GameDeadlineTests.AnActionAppliedInTimeIsReplayedAfterTheDeadlineUntilTheJobRuns`,
`AReusedActionIdBeforeTheDeadlineIsStillAValidationFailure` e `AnInvalidActionBeforeTheDeadlineIsStillAValidationFailure`,
e em `RoutesParticipationTests.AnActionAppliedInTimeIsReplayedAtTheDeadlineUntilTheJobRuns`.
