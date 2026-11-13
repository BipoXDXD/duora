# ADR-0053: Retirada voluntária da inscrição com histórico

- **Status:** Proposed (2026-11-09). Muda uma constraint e um contrato: o aceite vem com a revisão humana no merge.
- **Origem:** API-E04 (`spec.md`, seção 8.3); INV-06; T-28; decisões de 24/10/2026, itens 9 e 10
- **Relaciona:** [ADR-0044](0044-inscricoes-de-contas-suspensas.md), que criou `ck_registrations_withdrawal` e a ordem de
  locks da retirada por suspensão; [ADR-0049](0049-execucao-da-reconciliacao-de-inscricoes-legadas.md), cuja
  reconciliação trata `Withdrawn` como terminal
- **Revisar quando:** a retirada passar a valer com o evento em andamento (reserva e sala), a retirada ganhar origem
  além de pessoa e suspensão, ou a promoção passar a avisar a pessoa promovida (#320)

## Contexto

A API-E04 deixa a pessoa retirar a própria inscrição. `docs/data/model.md` lista `withdrawn_at`, mas
`ck_registrations_withdrawal` (ADR-0044) só aceitava `withdrawn_at`, `withdrawn_from_state` e
`withdrawn_waitlist_position` com uma suspensão vinculada. Sem suspensão, as quatro colunas tinham de ser nulas, o que
hoje significa "histórico desconhecido" de linha legada.

Alternativas consideradas:

- **A. Sem migração.** Gravar só `state='Withdrawn'`. A retirada voluntária ficaria igual à legada e perderia para
  sempre quando e de qual estado a pessoa saiu.
- **B. Ampliar a constraint.** O ramo com histórico deixa de exigir suspensão; o ramo legado continua.
- **C. Coluna `withdrawal_origin` ou tabela própria.** Repete o que `withdrawn_by_suspension_id` já diz e acrescenta
  estado sem consumidor.

## Decisão

Vamos adotar a opção B, escolhida pelo dono em 09/11/2026. A migração `20261109120000_VoluntaryRegistrationWithdrawal`
troca `ck_registrations_withdrawal` por uma versão que aceita o mesmo histórico da suspensão com
`withdrawn_by_suspension_id` nulo. Retirada voluntária é, portanto, `withdrawn_at` preenchido sem suspensão. Nenhuma
coluna nova, nenhuma mudança em capacidade, ordem de locks, `PromoteAsync` ou fato da outbox.

A rota é `DELETE /api/v1/events/{eventId}/registration`, irmã do `PUT` real, no grupo `EligibleParticipant`. A spec dizia
`/registrations/me`; a spec foi emendada. Ela:

- usa a mesma ordem da inscrição: plano, guardas, contas, evento, plano relido, e só então lê o relógio;
- só retira antes de `starts_at`, com o evento `Published`; depois disso responde `404` (a retirada com o evento em
  andamento, que libera reserva e sala, fica para outra entrega);
- promove a fila na mesma transação, como a suspensão;
- é idempotente pelo estado: linha já `Withdrawn`, por qualquer origem, responde `204` sem escrever;
- responde `404` igual para evento inexistente, pessoa sem inscrição, inscrição `Completed` e evento fechado;
- não grava auditoria por pessoa: a própria linha guarda quando e de onde. A promoção segue auditada.

## Consequências

- Superconjunto: toda linha válida antes continua válida, e o binário anterior continua gravando o que gravava. A troca
  da constraint valida a tabela inteira sob `ACCESS EXCLUSIVE`, curta no volume atual.
- `Down` recusa, como as migrações vizinhas: voltar exigiria apagar histórico.
- Até a #320, a pessoa promovida não recebe aviso; a tela não promete aviso.
- Conta que deixou de ser elegível não retira a inscrição pela rota (como API-R10); a vaga fica presa até a conta
  voltar ou ser suspensa.

## Compliance

`VoluntaryWithdrawalConstraintTests` (constraint no PostgreSQL real), `VoluntaryWithdrawalTests` (serviço),
`VoluntaryWithdrawalHttpTests` (rota, dono, 404 neutro, repetição), `VoluntaryWithdrawalConcurrencyTests` (retiradas
simultâneas e corrida com suspensão) e `AccountSuspensionRegistrationHistoryTests`, que continua provando que a linha
legada não ganha histórico inventado.
