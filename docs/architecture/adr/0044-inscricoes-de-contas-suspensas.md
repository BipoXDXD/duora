# ADR-0044: Retirar inscrições de contas suspensas sem restauração

- **Status:** Accepted (2026-11-01).
- **Origem:** #305, follow-up de #299/PR #302; spec 4.2, 8.3, 10.3–10.5; INV-01/02/06/10; T-01/T-06/T-28.
- **Revisar quando:** houver jornada explícita de readmissão no mesmo evento, mudança da janela de check-in ou transporte de notificações de participação.

## Contexto e decisão de produto

Suspender já impedia o uso do app e encerrava as salas, mas deixava inscrições ocupando capacidade.
A existência de `Withdrawn` não decidia a política. Em 31/10/2026 o dono autorizou a retirada com esse estado,
sem retorno ao mesmo evento após revogação, promovendo somente pessoas elegíveis e antes do início.

Alternativas descartadas: congelar mantendo vaga prejudicaria a espera; congelar sem vaga exigiria outro estado e
uma nova regra de retorno. Restaurar automaticamente poderia ultrapassar capacidade ou desalojar uma promoção.
A proibição de nova inscrição no mesmo evento já é testada por `WithdrawnPersonCannotRegisterAgain`.

## Aceite e acompanhamento — 01/11/2026

O dono autorizou a formalização deste ADR como Accepted em 01/11/2026, após a implementação pelo
[PR #310](https://github.com/BipoXDXD/duora/pull/310), as revisões independentes e a confirmação da publicação.
A política permanece a autorizada em 31/10/2026; este aceite não altera comportamento, contratos ou ordem de locks.
A [release 41281627285](https://github.com/BipoXDXD/duora/actions/runs/41281627285) confirmou `healthy` para
[`b915b106`](https://github.com/BipoXDXD/duora/commit/b915b1062d479cfe705cd77723321c082c8b8629), que contém a implementação.

Os desdobramentos continuam separados do escopo aceito:

- [#320 — avisos de promoção](https://github.com/BipoXDXD/duora/issues/320): consumidor do fato já gravado na outbox,
  com decisões de canal, atraso e consumo de fatos anteriores ainda sujeitas a revisão antes da implementação.
- [#321 — reconciliação retroativa](https://github.com/BipoXDXD/duora/issues/321): operação específica para inscrições
  legadas, com prévia, revalidação da medida original, idempotência e autorização própria de aplicação.

O aceite não conclui essas issues nem autoriza envio de avisos, consulta a participantes, reconciliação real,
ativação de serviços ou alteração de configuração em produção. Permanecem os limites operacionais abaixo.

## Comportamento

- Em eventos não encerrados, `Waitlisted`, `Confirmed` e `CheckedIn` passam a `Withdrawn`. Só as duas últimas
  liberam uma vaga. A posição anterior da espera e o estado anterior ficam na proveniência da retirada.
- `registered_at` e `checked_in_at` permanecem intactos. A pessoa promovida fica `Confirmed`, nunca recebe check-in
  automático. A confirmação inicial e a promoção nova gravam `confirmed_at`.
- A promoção preenche vagas pela menor posição elegível quando o evento ainda aceita inscrições e o relógio do
  servidor, consultado após locks, é estritamente anterior a `starts_at`. No schema atual esse estado é `Published`;
  a abertura do check-in é derivada do horário. Atraso de job não estende o prazo.
- A partir do instante de início, inclusive em `Running`, a retirada libera capacidade sem promoção. Eventos
  `Completed`/`Cancelled` e inscrições terminais preservam seu histórico.
- Elegibilidade é a regra existente: conta `Active`, e-mail confirmado, selfie `Passed`. Pessoa temporariamente
  inelegível continua na espera, sem renumeração; novos comandos de inscrição tentam preencher vagas com a espera
  elegível antes de admitir quem chega. Não há novo worker de promoção nesta entrega.
- Revogar recalcula somente a elegibilidade da conta, como no ADR-0042/0043. Não restaura inscrição, posição ou
  check-in. Tentar inscrever novamente a linha `Withdrawn` retorna indisponibilidade, sem sucesso fictício.
- Reaceite antigo dos Termos não causa retirada: permanece válida a decisão do dono na #296.

## Coordenação e concorrência

Events é dono de `RegistrationLockPlan` e `RegistrationTransitions`. Safety os chama na conexão e transação
existentes; não abre uma transação aninhada nem faz HTTP interno. Inscrição usa a mesma promoção.

1. Fazer pré-seleção dos eventos com inscrições ativas do alvo e dos eventos de suas reservas ativas; ler os usuários
   na espera. A pré-seleção não tem autoridade para escrever.
2. Trancar todas as guardas de usuários em UUID crescente, incluindo moderador/alvo/candidatos, e as contas em ordem
   de UUID. `FOR SHARE` nas contas mantém elegibilidade estável também diante dos escritores legados sem guarda.
3. Trancar todos os eventos em ordem de id. Reler o plano. Um novo evento ou usuário necessário exige rollback e
   reinício integral, no máximo três tentativas; esgotamento responde Busy/503, sem efeito parcial.
4. Gravar medida, estado da conta, retiradas, contadores e promoções; depois trancar rodadas e salas para o aborto
   e a liberação existentes. Todos os eventos já estão trancados antes de qualquer rodada/sala.
5. Gravar auditoria e outbox e confirmar tudo junto. Nenhuma chamada externa sob lock.

Cada atualização de inscrição incrementa sua versão; retirar reduz capacidade uma vez e promover a incrementa
somente após atualizar uma inscrição `Waitlisted`. A PK `(event_id,user_id)`, o índice de posição e o CHECK de
capacidade permanecem. As posições nunca são reutilizadas. Replays não repetem retiradas, auditoria ou promoções.

## Histórico, auditoria e efeitos posteriores

Migração aditiva `20261031120000_SuspendedRegistrations`: `confirmed_at`, `withdrawn_at`,
`withdrawn_by_suspension_id` (FK RESTRICT), `withdrawn_from_state`, `withdrawn_waitlist_position` e `version`.
Timestamps e causa anteriores ficam nulos: não são inferidos. Um trigger impede reescrever ou apagar uma retirada
por suspensão. A revogação da medida continua possível e não altera a inscrição.

A auditoria append-only existente recebe `events.registration_withdrawn_by_suspension` e
`events.registration_promoted` como efeitos System, alvo evento/versão. A FK na inscrição identifica a medida,
cuja autoria e motivo permanecem na auditoria de moderação existente. O motivo interno não vai à outbox.
Cada promoção produz um fato versionado `events.waitlisted_registration_promoted`, com evento e usuário.
A entrega de notificação depende de consumidor futuro; esta issue não cria transporte nem anuncia envio ao usuário.

## Segurança, operação e reversão

Sem mudanças de autorização, papéis, MFA, sessão ou endpoints. Dados de participação não são listados a terceiros.
`lock_timeout` segue 2 s. Deadlock continua defeito a investigar; os testes exigem conclusão sem Busy nas corridas
controladas, de modo que a recuperação de contenção não esconda uma inversão.

A migração não retira inscrições de contas anteriormente suspensas. Essa reconciliação precisa de operação
específica, revisada, com a medida original; não se inventa uma suspensão nova nem se consulta produção nesta tarefa.
O Down recusa apagar histórico. Reversão exige binário compatível: um binário antigo não conhece os novos escritores
nem a política e pode responder sucesso fictício a uma inscrição retirada. Não reativar o comportamento anterior
como reversão silenciosa; corrigir para frente ou revisar uma release compatível. Merge exige revisão humana
(AGENTS.md); nenhuma ativação, deploy ou processamento real é autorizado por este ADR.

## Compliance

`AccountSuspensionRegistrationTests`, `AccountSuspensionRegistrationConcurrencyTests` e
`AccountSuspensionRegistrationHistoryTests`: confirmed/check-in/espera, limite temporal, inelegibilidade, revogação,
idempotência, upgrade sem inferência, histórico protegido, rollback e interleavings por `pg_blocking_pids`.
As suítes anteriores de suspensão/salas/autorização permanecem obrigatórias. PostgreSQL real, dados sintéticos,
conexões independentes; nenhum teste substituído por InMemory/SQLite.
