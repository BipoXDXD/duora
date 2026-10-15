# ADR-0008: PostgreSQL para jobs e outbox antes de um broker

- **Status:** Accepted (2026-10-15)
- **Origem:** spec 2.3, 3.4, 10.6 e 23.1 (ADR-008)
- **Revisar quando:** métricas demonstrarem gargalo ou necessidade de fan-out

## Contexto

Decisões, bloqueios, convites e uploads produzem efeitos assíncronos (notificação, e-mail, processamento de
mídia). Gravar no banco e depois publicar fora da transação perde ou duplica efeitos. Rodadas precisam
expirar mesmo após reinício do processo.

Alternativas consideradas:

- **Broker de mensagens (Kafka ou similar).** Descartado no início: mais um componente a operar, sem
  transação com o banco (spec 2.3).
- **Agendador genérico de terceiros (Hangfire ou similar).** Não entra por reflexo (spec 2.3).
- **Timers em memória (`Task.Delay`) como registro de prazo.** Descartado: não sobrevivem a reinício
  (spec 10.6).

## Decisão

Vamos usar transactional outbox e jobs persistidos no PostgreSQL. Toda transação de negócio que produz efeito
assíncrono grava a outbox no mesmo commit. Consumidores selecionam lotes pequenos com `FOR UPDATE SKIP LOCKED`,
gravam lease com token de fencing, liberam a transação antes da chamada externa e concluem só se ainda detêm o
token. Política inicial proposta: até 8 tentativas, backoff exponencial com jitter e teto de 15 minutos;
notificação com deadline vencido vira `Expired`.

Porquê técnico: atomicidade entre efeito de negócio e intenção de notificar, sem 2PC. Porquê de negócio: menos
infraestrutura no piloto.

## Consequências

- Positivas: um banco, uma transação, retomada após reinício (INV-08).
- Negativas: entrega pode repetir; nunca há exactly-once. Consumidores e fornecedores precisam de
  idempotência ou reconciliação. A fila disputa recursos com o banco transacional.
- Trade-off: throughput limitado pelo PostgreSQL, aceitável na carga de referência a medir.

## Segurança e operação

Alerta pela idade do item mais antigo, não só pelo tamanho da fila. Falhas definitivas vão para revisão com
causa sanitizada. Reprocessar exige permissão e nova verificação de elegibilidade (spec 10.6, 8.5).

## Compliance

- T-08 (reinício antes/depois de commit, envio ou ACK) e T-17 (fornecedor lento, falho ou callback repetido):
  a implementar em M1/M2.
- Índice único de entrega `(outbox_event_id, consumer, recipient_key)` verificado em teste de integração (M1).
- Revisão rejeita chamada externa dentro de transação com locks de negócio (spec 10.4).
