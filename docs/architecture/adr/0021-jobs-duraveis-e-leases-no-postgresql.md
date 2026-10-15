# ADR-0021: Jobs duráveis com lease e fencing token no PostgreSQL

- **Status:** Proposed
- **Origem:** spec 2.3, 3.4, 3.5 (cenário "worker pausa, lease vence e outro executor assume"), 5.2, 10.4, 10.6,
  14.4 e 16.4; INV-08; [ADR-0008](0008-postgresql-para-jobs-e-outbox.md); outbox de e-mail do PR de identidade
  (#14, `OutboxDeliveryQueue` e `ExponentialBackoff`)
- **Revisar quando:** a idade do job pendente mais antigo passar do alvo por falta de vazão do PostgreSQL, um tipo
  de job precisar de agendamento por calendário que este modelo não expresse, ou surgir um terceiro uso do mesmo
  protocolo de fila (unificar o código)

## Contexto

O [ADR-0008](0008-postgresql-para-jobs-e-outbox.md) decidiu jobs e outbox no PostgreSQL antes de um broker, e o
PR de identidade implementa a outbox de e-mail: claim com `FOR UPDATE SKIP LOCKED`, lease, token de fencing,
envio fora da transação e conclusão condicionada ao token e ao lease. Falta o padrão geral para os jobs da spec
14.4 que não são entrega de evento:

| Tipo | Gatilho | Efeito | Natureza |
|---|---|---|---|
| Expiração de convite | `invite_expires_at` da sala | Sala `Invited` → `Expired`, reserva liberada, outbox | Interno ao banco |
| Prazo de sala ativa | `active_deadline_at` | `HandleDeadline` (C6) | Interno ao banco |
| Transição de rodada | Horário da rodada (spec 5.2) | Abrir, parear, encerrar, retomar ou cancelar a rodada | Interno ao banco |
| Retenção | Diário | Exclusão em lotes com trilha (spec 10.7) | Interno; parte externa no storage |
| Mídia | Upload concluído | Validação, derivados, promoção ou rejeição | Externo (object storage) |

O [model.md](../../data/model.md#opsjobs) já esboça `ops.jobs` e o [concurrency.md](../../data/concurrency.md#outbox-lease-e-fencing)
descreve o claim, mas sem deadline, sem `Expired`, sem a diferença entre efeito interno e externo, sem recorrência
e sem a forma de testar o cenário de pausa da spec 3.5.

Base teórica: um lease é um lock que expira, e um processo pausado (GC, VM, rede) pode voltar achando que ainda o
detém; só o recurso protegido recusando o token antigo impede o efeito duplicado (*Designing Data-Intensive
Applications*, 2ª ed., cap. 9, "Distributed Locks and Leases"). O token é um Generation Clock: número monotônico,
persistido, incrementado a cada nova posse, que faz requisições da geração anterior serem rejeitadas (*Patterns of
Distributed Systems*, caps. 9 e 26). O relógio que decide expiração é o do servidor de aplicação
(`TimeProvider`), comparado com instantes persistidos; o lease só reduz trabalho duplicado, quem garante a
correção é o token e a transição condicional.

Alternativas consideradas:

- **Hangfire (Hangfire.Core 1.8 e Hangfire.PostgreSql 1.21).** Licença LGPL-3.0 nos dois pacotes, com opção
  comercial no Core (conferido nos `LICENSE.md` dos pacotes publicados no NuGet em 2026-10-15). LGPL não está na
  allowlist de `scripts/supply-chain/license-policy.json`, então exigiria exceção de política. Além disso: cria o
  próprio schema, serializa a chamada do método com nome de tipo (renomear a classe quebra jobs pendentes), tem
  retry próprio e não tem fencing por execução nem deadline com `Expired`; enfileirar na mesma transação do
  EF Core depende de enlist do storage que teríamos de provar; o dashboard é mais uma superfície autenticada. A
  spec 2.3 manda não adicioná-lo por reflexo.
- **Quartz.NET 4.5 (Apache-2.0, permitido).** Agendador maduro com `JobStoreTX` em PostgreSQL e cluster por
  check-in. Contras: cerca de onze tabelas próprias, conexão própria (agendar junto com a transação de negócio não
  é natural), semântica de misfire em vez de deadline e `Expired`, sem fencing por execução e modelado para
  gatilhos de calendário, não para um job por entidade com chave de deduplicação. Resolveria a recorrência
  diária, que aqui é um caso pequeno.
- **Broker com mensagem atrasada (RabbitMQ ou similar).** Rejeitado pelo
  [ADR-0008](0008-postgresql-para-jobs-e-outbox.md): componente a mais, sem transação com o banco, então
  continuaria precisando de outbox para não perder o agendamento.
- **`pg_cron`.** Executa SQL no banco; empurraria regra de negócio para funções do PostgreSQL (contra a regra de
  SQL do perfil, divergência 5 de `database.md`) e não cobre efeito externo como mídia.
- **Agendador genérico próprio** (cron expressions, registro dinâmico de handlers, painel). Rejeitado por
  generalidade especulativa: os tipos são poucos e conhecidos.

## Decisão

Vamos usar uma tabela `ops.jobs` com um conjunto fechado de tipos, cada um com handler próprio no worker, e o
mesmo protocolo de lease e fencing da outbox.

1. **Tabela.** `ops.jobs`: `id uuid`; `kind text` (`CHECK` com os tipos conhecidos); `dedupe_key text`
   (`UNIQUE (kind, dedupe_key)`, ex.: `expire-invite:<gameSessionId>`, `round-transition:<roundId>:<estado
   alvo>`, `retention:<data>`); `payload jsonb` (só IDs e versões, `CHECK` de tamanho); `status text`
   (`Pending`, `InFlight`, `Done`, `Failed`, `Expired`, `Cancelled`; `Pending`, `InFlight`, `Failed` e
   `Expired` com os nomes da outbox do PR #14, `Done` e `Cancelled` próprios de job);
   `available_at timestamptz`; `deadline_at timestamptz` anulável; `attempts int`; `max_attempts int` (padrão
   8); `lease_expires_at timestamptz`; `fencing_token bigint`; `last_error_code text` (causa sanitizada);
   `created_at`, `completed_at`. `CHECK ((status = 'InFlight') = (lease_expires_at IS NOT NULL))`,
   `CHECK (attempts BETWEEN 0 AND max_attempts)`. Índices parciais: `(available_at, id) WHERE status =
   'Pending'` e `(lease_expires_at) WHERE status = 'InFlight'`.
2. **Criação na transação de negócio.** O job nasce no mesmo commit do fato que o motiva (sala criada, rodada
   agendada, upload concluído), com `INSERT ... ON CONFLICT (kind, dedupe_key) DO NOTHING`. Nada de timer em
   memória como registro de prazo.
3. **Claim.** Transação curta, sem chamada externa: seleciona até 20 linhas (`Pending` com `available_at <=
   agora` ou `InFlight` com lease vencido) com `ORDER BY available_at, id LIMIT $lote FOR UPDATE SKIP LOCKED` e,
   no mesmo comando, passa a `InFlight`, incrementa `attempts` e `fencing_token`, e grava `lease_expires_at =
   agora + lease` (60 s; jobs longos renovam por heartbeat condicionado ao token). Linha com `deadline_at <=
   agora` não é executada: vai a `Expired` no próprio claim.
4. **Efeito interno (expiração, prazo, rodada, retenção no banco).** Uma transação: primeiro `UPDATE ops.jobs SET
   status = 'Done', completed_at = $agora, lease_expires_at = NULL WHERE id = $id AND status = 'InFlight' AND
   fencing_token = $t AND lease_expires_at > $agora`; zero linhas → rollback e descarte. Depois a ordem global de
   locks da spec 10.4 (a linha do job vem antes de tudo, como o registro de idempotência, e nunca espera: quem
   disputa a linha usa `SKIP LOCKED`), revalidação e a transição condicional de negócio (`WHERE version = $v AND
   state = ...`, C6). Efeito e conclusão comitam juntos: o efeito acontece uma vez, mesmo com dois executores.
5. **Efeito externo (mídia, exclusão no storage).** Efeito fora de qualquer transação, com timeout, cancelamento
   e chave de idempotência estável (`id` do job); conclusão em transação curta condicionada ao token e ao lease,
   como na outbox. Zero linhas → resultado descartado e métrica `stale_completion`; o efeito pode ter acontecido
   duas vezes, por isso ele precisa ser idempotente (sobrescrever o mesmo objeto, apagar o que já não existe é
   sucesso).
6. **Retry.** Falha transitória volta a `Pending` com `available_at = agora + atraso`, atraso do
   `ExponentialBackoff` (15 s dobrando até o teto de 15 min, sorteado na metade de cima do intervalo), condicionado
   ao token. `attempts` conta no claim, então um job que derruba o processo também esgota tentativas. Na oitava
   falha, ou em falha definitiva (entrada inválida, recurso inexistente com estado incoerente), vai a `Failed`.
7. **Deadline.** Cada tipo declara se tem deadline. Jobs de correção de estado (expiração, prazo, transição de
   rodada) não têm: precisam rodar mesmo atrasados, e a política de retomada da spec 5.2 decide o que fazer com a
   rodada vencida. Jobs cujo efeito perde sentido com o tempo têm, e viram `Expired` em vez de rodar tarde.
8. **Recorrência sem agendador.** Job recorrente (retenção diária, limpeza) usa `dedupe_key` com o período
   (`retention:2026-10-16`); ao concluir, o handler insere a próxima ocorrência na mesma transação, com jitter do
   `JitteredInterval`; no boot o worker garante a próxima ocorrência com `ON CONFLICT DO NOTHING`.
9. **Fila de revisão.** `Failed` é a fila de revisão: consulta por `status = 'Failed'`, causa sanitizada em
   `last_error_code`, nunca dado pessoal. Reprocessar é API-ADM20: permissão, chave de idempotência, nova
   verificação de elegibilidade, `attempts = 0` e auditoria.
10. **Métricas.** Por `kind`: idade do `Pending` vencido mais antigo (`agora - min(available_at)`), quantidade
    em `Failed` e `Expired`, `stale_completion`, leases vencidos retomados e duração do efeito. Alerta pela idade,
    não pelo tamanho da fila (spec 10.6); limiares iniciais são hipótese a medir no ensaio de carga.
11. **Limpeza.** Job recorrente apaga em lotes de até 1.000 linhas os jobs `Done`, `Expired` e `Cancelled` com
    `completed_at` acima de 7 dias, e os `Failed` revisados acima de 30 dias (prazos hipotéticos, a fixar em
    [retention.md](../../privacy/retention.md)).
12. **Código.** Mesma política e mesmos tipos de apoio da outbox (`ExponentialBackoff`, `JitteredInterval`,
    `TimeProvider`); o SQL de `ops.jobs` fica numa classe própria por enquanto. Se aparecer um terceiro uso do
    protocolo, extrair a fila comum.

Porquê técnico: atomicidade entre agendar e o fato de negócio, efeito interno exatamente uma vez pela mesma
transação, efeito externo protegido por fencing e idempotência, sem componente novo. Porquê de negócio: rodadas,
convites e retenção continuam corretos depois de reinício ou pausa do worker sem dependência paga, sem licença
fora da política e sem mais infraestrutura no piloto.

## Consequências

- Positivas: uma tabela e um protocolo, iguais aos da outbox; agendamento e fato comitam juntos; o cenário de
  pausa da spec 3.5 tem resposta mecânica (token) e não depende de relógio entre máquinas.
- Negativas: escrevemos e mantemos claim, retry, recorrência e limpeza; sem painel pronto (a fila de revisão
  depende da área administrativa); polling a cada segundo gera carga constante, pequena, no banco.
- Trade-off: vazão limitada pelo PostgreSQL, aceitável para a carga de referência; o gatilho de revisão mede a
  idade do item mais antigo.
- Follow-up se aceito: atualizar [model.md](../../data/model.md#opsjobs) (colunas `available_at`, `deadline_at`,
  `max_attempts`, status `InFlight` e `Expired`) e o diagrama de estados de
  [concurrency.md](../../data/concurrency.md#outbox-lease-e-fencing) para os nomes da outbox implementada.

## Segurança e operação

- Payload só com IDs e versões; nada de e-mail, texto de mensagem ou caminho do bucket. `last_error_code` é um
  código, não a mensagem da exceção.
- O worker usa o mesmo papel `duora_app`, sem DDL. Reprocessar e cancelar job exigem permissão administrativa e
  auditoria.
- Lease e polling usam o `TimeProvider`; nenhum predicado de índice usa `now()`.
- Modo de manutenção (`ops.operational_flags`) pausa o claim por `kind`, sem perder os jobs.

## Compliance

- **T-08, cenário da spec 3.5 (integração, M2):** `PausedWorkerLosesLeaseAndCannotCompleteJob`. Testcontainers
  com PostgreSQL 18, dois executores com `NpgsqlDataSource` próprios e um `FakeTimeProvider` comum. O executor 1
  faz o claim de um `expire-invite` (token t) e para num gancho de teste antes de concluir; o teste avança o
  relógio além do lease; o executor 2 faz o claim (token t + 1) e conclui; o executor 1 é liberado. Asserções,
  relendo por outra conexão: a conclusão do executor 1 afeta 0 linhas; a sala tem uma única transição para
  `Expired`, a reserva foi liberada uma vez e há um único evento na outbox; o job está `Done` com `fencing_token =
  t + 1`; `stale_completion` foi incrementado. Variante de efeito externo: o fornecedor falso recebe a mesma chave
  de idempotência nas duas chamadas. Variantes de queda: executor descartado antes do commit do efeito (o job
  volta a ser elegível depois do lease) e depois do efeito externo e antes da conclusão (efeito repetido,
  deduplicado).
- **Sabotagem:** o teste é conferido uma vez com `fencing_token = $t` removido da conclusão e precisa falhar
  (duas transições ou dois eventos).
- **Retry e deadline (integração, M2):** falha transitória reagenda dentro da faixa do backoff; a oitava falha vai
  a `Failed`; job com `deadline_at` vencido vira `Expired` sem executar; `SKIP LOCKED` faz dois executores
  simultâneos tomarem lotes disjuntos (`Barrier`, 50 repetições).
- **T-18 (unidade e integração, M2):** prazo de sala e rodada resolvido pelo relógio do servidor, com job
  atrasado e com retorno após longa queda.
- **Dependências:** o check `supply-chain` barra licença fora da allowlist; Hangfire ou Quartz só entram com ADR
  novo.
