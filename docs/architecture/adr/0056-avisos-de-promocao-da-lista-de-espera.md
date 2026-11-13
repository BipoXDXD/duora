# ADR-0056: Avisos de promoção da lista de espera pela outbox, desligados por padrão

- **Status:** Proposed (2026-11-13). Canal, textos e ativação em produção dependem do dono.
- **Origem:** issue #320
- **Relaciona:** [ADR-0044](0044-inscricoes-de-contas-suspensas.md) (registra o fato
  `events.waitlisted_registration_promoted`, sem consumidor até aqui); a outbox de e-mail de conta
  (`TransactionalEmailDispatcher`, ADR-0021)
- **Revisar quando:** o dono aprovar canal e textos, entrar preferência de notificação, o fato ganhar versão 2, ou o
  volume de promoções pedir lote ou intervalo diferentes

## Contexto

Hoje a promoção da lista de espera grava o fato `events.waitlisted_registration_promoted` v1 (payload
`{eventId, userId}`) na mesma transação da promoção, mas ninguém o lê: a pessoa promovida só descobre ao abrir o evento.
A outbox de e-mail de conta já envia mensagens transacionais com prazo, retry e retenção.

Riscos que guiam a decisão: avisar quem já desistiu, quem perdeu a vaga ou cuja conta foi restringida; avisar depois
que o evento começou; avisar duas vezes num replay ou com dois workers; e, ao ligar, despejar avisos de fatos antigos.

Alternativas consideradas:

- **Enviar na transação da promoção.** Acopla o caso de uso de inscrição ao e-mail e põe I/O externo dentro da
  transação. Recusada.
- **Tabela própria de processamento.** Pede migração e mais uma limpeza. A entrega terminal na outbox já tem chave
  única, retenção e métricas. Recusada.
- **Reprocessar todo o histórico ao ligar.** Avisos velhos e, para eventos passados, errados. Recusada: marca d'água.

## Decisão

1. **Desligado por padrão.** `Notifications:WaitlistPromotion` (`Enabled`, `NotBefore`, `BatchSize`, `PollInterval`)
   é uma opção tipada com `ValidateOnStart`. `Enabled` vale `false` em todos os ambientes; ligado sem `NotBefore`, o
   Worker não sobe. Desligado, o serviço registra uma linha no início e não lê nem grava nada.
2. **Marca d'água.** Só fatos com `occurred_at >= NotBefore` e mais novos que 48 h (`MaxFactAge`) são lidos. As 48 h
   ficam abaixo dos 7 dias da retenção das entregas terminais, então um fato nunca volta a parecer novo depois que a
   marca é apagada.
3. **Idempotência por fato.** A marca de processamento é uma entrega terminal do consumidor `waitlist-promotion-notice`
   com `recipient_key` = id do fato, protegida por `ux_outbox_deliveries_effect`. Marca e pedido de e-mail
   (`identity.account-email-requested`, kind `WaitlistPromotionNotice`) são gravados na mesma transação, com
   `on conflict do nothing`: replay ou segundo worker não grava nada. O fato é travado com
   `for no key update skip locked`.
4. **Revalidação antes de enfileirar,** sob lock de leitura e `lock_timeout` de 2 s, na ordem dos casos de uso (conta,
   depois inscrição): conta `Active`, inscrição `Confirmed`, evento `Published` e ainda não iniciado. Senão a marca
   fecha como `Expired` com motivo fixo (`AccountNotActive`, `RegistrationNotConfirmed`, `EventNotOpen`,
   `EventStarted`). Contrato fora da v1 ou destinatário inválido fecham como `Failed` (`InvalidContract`,
   `InvalidRecipient`), para revisão. Trava além do teto deixa o fato sem marca para a próxima rodada. Um fato que
   falha de outro jeito (erro inesperado) também fica sem marca e não interrompe o lote; o log leva só o tipo da falha.
   A linha de `events.events` é lida sem lock: hoje não há caso de uso que cancele evento. Quando houver, a consulta
   passa a `for share of r, e`, na ordem evento → inscrição do `RegistrationLockPlan`.
5. **Prazo do e-mail** = o menor entre 24 h e o início do evento: o dispatcher expira o aviso que não saiu a tempo.
6. **Texto bilíngue (pt-BR e EN) sem dado sensível.** O corpo diz que a inscrição saiu da lista de espera e pede para
   conferir o estado atual em `/events`; não cita evento, horário, ids nem destinatário. O log leva só o id do fato e o
   desfecho.
7. **Fronteiras.** O consumidor só lê `identity.users` e `events.registrations`/`events.events` e escreve em `ops.*`
   (Operations consome a outbox, conforme modules.md). Não altera inscrição, capacidade, fila nem auditoria. Sem
   migração.

## Consequências

- Ativar é configuração, não deploy: `NotBefore` no instante da ativação e `Enabled=true`.
- Rollback: `Enabled=false`. Marcas e e-mails já enfileirados ficam; o dispatcher ainda envia os pendentes dentro do
  prazo, e mensagem aceita pelo provedor não se revoga.
- Fato que chegar com o consumidor desligado e passar de 48 h nunca será avisado. É intencional.
- A revalidação vale no enfileiramento, não no envio: o `TransactionalEmailDispatcher` não revalida. Uma retirada,
  suspensão ou exclusão de conta que aconteça depois (ou que commite logo após o lock de leitura da conta) não impede
  o envio, e com o provedor falhando o aviso pode sair até o fim do prazo da entrega (no máximo 24 h). Por isso o
  texto manda conferir o estado atual. Revalidar no envio pede o id do evento no payload e consultas a Identity e
  Events no dispatcher; fica como decisão pendente do dono, sobretudo para conta suspensa ou excluída.
- Um fato venenoso volta a cada rodada até sair da janela de 48 h; isolado por fato, não bloqueia os vizinhos, mas
  ocupa uma vaga do lote em cada rodada.
- Risco de rollout: com o consumidor ligado mas falhando (banco, trava, erro inesperado), um fato pode envelhecer
  48 h sem marca e sumir sem métrica, porque os gauges da outbox só veem marcas. Até haver métrica própria, o sinal
  é o log de `Warning`/`Error` do consumidor, que deve ser acompanhado na ativação.
- Fora deste ADR: preferências de notificação, outros canais, métrica própria por desfecho (hoje há log e os gauges da
  outbox por consumidor).

## Compliance

Testes de integração com PostgreSQL real (`WaitlistPromotionNoticeTests`):
`AnEligibleFactSchedulesOneNoticeThatExpiresWhenTheEventStarts`, `TheNoticePayloadCarriesNoEventDataAndNoToken`,
`RunningAgainSchedulesNothingNew`, `ConcurrentSchedulersScheduleOneNotice`,
`AWithdrawalBeforeSchedulingSchedulesNothingAndTheNextPromotionStillCounts`,
`AWithdrawalCommittedWhileTheSchedulerWaitsIsHonoured`, `AnInactiveAccountReceivesNothing`,
`AFactOfAnEventThatAlreadyStartedSchedulesNothing`, `ACancelledEventSchedulesNothing`,
`WhenDisabledNothingIsReadOrWritten`, `FactsRecordedBeforeTheWatermarkAreNeverProcessed`,
`FactsOlderThanTheMaximumAgeAreNeverProcessed`, `AFactOutsideTheV1ContractGoesToReviewWithoutANotice`,
`AFactLockedByAnotherTransactionIsSkippedWithoutWaitingAndTheOthersAreProcessed`,
`ARegistrationHeldPastTheLockTimeoutLeavesItsFactForTheNextRound`, `AFactThatFailsUnexpectedlyDoesNotStopTheBatch`.
Trava de ativação: `WorkerHostTests.WaitlistPromotionNoticesDoNotStartEnabledWithoutAWatermark`,
`WorkerHostTests.WaitlistPromotionNoticesNeedAWatermarkOnlyWhenEnabled` e `WaitlistPromotionNoticeOptionsTests`.
Envio (`WaitlistPromotionNoticeDispatchTests`): `TheNoticeIsDeliveredInBothLanguagesWithALinkToTheEvents`,
`ANoticeCarryingATokenIsRejectedWithoutSending`. Texto (`WaitlistPromotionNoticeEmailTests`, unitário).
O consumidor entra na lista fixa do monitoramento da outbox (`RetentionTests.Monitoring`).
