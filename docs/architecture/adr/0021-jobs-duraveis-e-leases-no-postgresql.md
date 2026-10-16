# ADR-0021: Jobs duráveis com lease e fencing token no PostgreSQL

- **Status:** Accepted (2026-10-16)
- **Origem:** spec 2.3, 3.4, 3.5 (cenário "worker pausa, lease vence e outro executor assume"), 5.2, 10.4, 10.6,
  14.4, 16.4, 17.3 (passo 4) e 18.4; INV-08; [ADR-0003](0003-monolito-modular-web-worker.md),
  [ADR-0008](0008-postgresql-para-jobs-e-outbox.md), [ADR-0025](0025-pipeline-de-fotos-imagesharp-s3-outbox.md);
  outbox de e-mail do PR de identidade (#14, `OutboxDeliveryQueue` e `ExponentialBackoff`)
- **Revisar quando:** a idade do job pendente mais antigo passar do alvo por falta de vazão do PostgreSQL, um tipo
  de job precisar de agendamento por calendário que este modelo não expresse, houver mais de um worker em
  máquinas diferentes, ou surgir um terceiro uso do mesmo protocolo de fila (unificar o código)

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
| Limpeza de uploads abandonados | Diário (`abandon_after`, ADR-0025) | Apagar linha e objeto da quarentena | Externo (object storage) |
| Limpeza de filas | Diário | Apagar jobs, entregas e registros de idempotência vencidos | Interno ao banco |

O **processamento** de mídia não entra aqui: o [ADR-0025](0025-pipeline-de-fotos-imagesharp-s3-outbox.md),
aceito, o põe na outbox (`media-processing`), porque nasce de um evento ("upload concluído"). A regra de corte é
essa: reação a um fato vai pela outbox; trabalho marcado para um instante ou recorrente vai por `ops.jobs`.

O [model.md](../../data/model.md#opsjobs) esboçava `ops.jobs` e o [concurrency.md](../../data/concurrency.md#outbox-lease-e-fencing)
descreve o claim, mas sem deadline, sem `Expired`, sem a diferença entre efeito interno e externo, sem recorrência
e sem a forma de testar o cenário de pausa da spec 3.5.

Base teórica: um lease é um lock que expira, e um processo pausado (GC, VM, rede) pode voltar achando que ainda o
detém; só o recurso protegido recusando o token antigo impede o efeito duplicado (*Designing Data-Intensive
Applications*, 2ª ed., cap. 9, "Distributed Locks and Leases"). O token é um Generation Clock: número monotônico,
persistido, incrementado a cada nova posse, que faz requisições da geração anterior serem rejeitadas (*Patterns of
Distributed Systems*, caps. 9 e 26). O relógio que decide expiração é o do processo que executa
(`TimeProvider`), comparado com instantes persistidos; o lease só reduz trabalho duplicado, quem garante a
correção é o token, o lock da linha do job e a transição condicional de negócio.

Alternativas consideradas:

- **Hangfire (Hangfire.Core 1.8 e Hangfire.PostgreSql 1.21).** Licença LGPL-3.0 nos dois pacotes, com opção
  comercial no Core (conferido nos `LICENSE.md` dos pacotes publicados no NuGet em 2026-10-15). LGPL não está na
  allowlist de `scripts/supply-chain/license-policy.json`, então exigiria exceção de política. Prós: painel,
  retry e agendamento recorrente prontos, base de usuários grande. Contras: cria o próprio schema, serializa a
  chamada do método com nome de tipo (renomear a classe quebra jobs pendentes), tem retry próprio e não tem
  fencing por execução nem deadline com `Expired`; enfileirar na mesma transação do EF Core depende de enlist do
  storage que teríamos de provar; o dashboard é mais uma superfície autenticada. A spec 2.3 manda não adicioná-lo
  por reflexo.
- **Quartz.NET 4.5 (Apache-2.0, permitido).** Agendador maduro com `JobStoreTX` em PostgreSQL e cluster por
  check-in. Prós: licença permitida, recorrência por calendário completa, misfire configurável. Contras: cerca de
  onze tabelas próprias, conexão própria (agendar junto com a transação de negócio não é natural), semântica de
  misfire em vez de deadline e `Expired`, sem fencing por execução e modelado para gatilhos de calendário, não
  para um job por entidade com chave de deduplicação. Resolveria a recorrência diária, que aqui é um caso
  pequeno.
- **Broker com mensagem atrasada (RabbitMQ ou similar).** Rejeitado pelo
  [ADR-0008](0008-postgresql-para-jobs-e-outbox.md): componente a mais, sem transação com o banco, então
  continuaria precisando de outbox para não perder o agendamento.
- **Reusar `ops.outbox_deliveries` com `available_at` no futuro.** Prós: nenhuma tabela nova, o claim já existe.
  Contras: a entrega pertence a um evento (`outbox_event_id`, `UNIQUE (evento, consumidor, destinatário)`), e um
  prazo de sala não é evento; a deduplicação por chave de negócio (`expire-invite:<sala>`) e a recorrência não
  cabem nessa unicidade; misturaria a limpeza de 7 dias da outbox com jobs ainda futuros.
- **Varredura periódica das tabelas de negócio** (`WHERE state = 'Invited' AND invite_expires_at <= $agora`),
  sem tabela de jobs. Prós: nada a agendar, a fonte da verdade é a própria sala; nenhum job pode faltar. Contras:
  uma consulta e um índice parcial por tipo, sem lugar comum para tentativas, `Failed`, fila de revisão
  (API-ADM19/ADM20) e métrica de idade; retenção e limpeza de storage não têm linha de negócio a varrer.
  Continua útil como **reconciliação** opcional, não como mecanismo principal.
- **`pg_cron`.** Executa SQL no banco; empurraria regra de negócio para funções do PostgreSQL (contra a regra de
  SQL do perfil, divergência 5 de `database.md`) e não cobre efeito externo como o storage.
- **Agendador genérico próprio** (cron expressions, registro dinâmico de handlers, painel). Rejeitado por
  generalidade especulativa: os tipos são poucos e conhecidos.

## Decisão

Vamos usar uma tabela `ops.jobs` com um conjunto fechado de tipos, cada um com handler próprio no worker, e o
mesmo protocolo de lease e fencing da outbox. Só o processo worker executa jobs (ADR-0003); o web só os cria.

1. **Tabela.** `ops.jobs`: `id uuid` (UUIDv7 da aplicação); `kind text` (`CHECK` com os tipos conhecidos);
   `dedupe_key text` (`UNIQUE (kind, dedupe_key)`; formato no item 2); `payload jsonb` (só IDs, versões e o
   instante agendado do agregado alvo, nunca ID de pessoa quando o agregado basta; `CHECK (octet_length(payload::text) <= 4096)`;
   leitor tolerante conforme a convenção de evolução de payload do [model.md](../../data/model.md#convenções));
   `status text` (`Pending`, `InFlight`, `Done`, `Failed`, `Expired`, `Cancelled`; `Pending`, `InFlight`,
   `Failed` e `Expired` com os nomes da outbox implementada, `Done` e `Cancelled` próprios de job);
   `available_at timestamptz`; `deadline_at timestamptz` anulável; `attempts int not null default 0`;
   `max_attempts int not null default 8`; `lease_expires_at timestamptz`; `fencing_token bigint not null default
   0`; `last_error_code text` (`CHECK (char_length <= 64)`, causa sanitizada); `created_at`, `completed_at`.
   `CHECK ((status = 'InFlight') = (lease_expires_at IS NOT NULL))`,
   `CHECK (attempts >= 0 AND max_attempts BETWEEN 1 AND 20)`, `CHECK ((status IN ('Done', 'Failed', 'Expired',
   'Cancelled')) = (completed_at IS NOT NULL))`. Não há `CHECK (attempts <= max_attempts)`: a retomada de lease
   vencido incrementa `attempts` no mesmo comando do claim, e um `CHECK` violado ali abortaria o lote inteiro
   (item 6). Índices parciais: `ix_jobs_due (available_at, id) WHERE status = 'Pending'`,
   `ix_jobs_in_flight (lease_expires_at) WHERE status = 'InFlight'` e
   `ix_jobs_completed (completed_at) WHERE status IN ('Done', 'Expired', 'Cancelled')` (limpeza). Nenhum
   predicado usa `now()`.
2. **Criação na transação de negócio, uma chave por agendamento.** O job nasce no mesmo commit do fato que o
   motiva (sala criada, jogo iniciado, rodada agendada), com `INSERT ... ON CONFLICT (kind, dedupe_key) DO
   NOTHING`. Nada de timer em memória como registro de prazo. **Transação de negócio só insere em `ops.jobs`;
   nunca atualiza, cancela nem tranca um job existente.**
   - A `dedupe_key` identifica **o agendamento**, não só o agregado: `<kind>:<id do agregado>:<instante agendado
     em UTC, ISO 8601>`, ex.: `expire-invite:<gameSessionId>:2026-10-16T19:00:00Z`,
     `active-deadline:<gameSessionId>:<prazo>`, `round-transition:<roundId>:<estado alvo>:<horário>`. O payload
     repete o id e o instante. Usar a versão do agregado na chave foi descartado: a sala muda de versão a cada
     ação de jogo, e o job de prazo ficaria obsoleto na primeira jogada; o instante muda só quando o prazo muda.
   - Se o fato mudar (convite aceito, prazo antecipado ou adiado), a transação que o muda insere o job do novo
     agendamento, com chave nova; o antigo continua na fila e, na revalidação (item 4), vê que o agregado já não
     está no estado esperado ou que o instante persistido é outro, e conclui `Done` sem efeito. Não há
     reagendamento pelo handler nem chave antiga reaproveitada. O `DO NOTHING` só descartaria um agendamento
     real se a mesma chave já existisse em estado final, e isso exige reagendar para um instante cujo job já
     rodou, ou seja, para o passado, que o domínio recusa; voltar a um instante futuro já agendado encontra o job
     ainda `Pending`, que executa normalmente.
   - Por que isso importa: `INSERT ... ON CONFLICT DO NOTHING` **espera** quando a linha conflitante está sendo
     alterada por outra transação ainda aberta. Se a API reinserisse a mesma chave do job que um handler está
     executando (linha do job trancada, handler esperando a sala que a API tranca), haveria deadlock. Com uma
     chave por agendamento, a API só insere chaves que ainda não existem, e esse conflito não acontece no fluxo
     normal; se acontecer por bug, o PostgreSQL detecta o deadlock (`40P01`) e a unidade é repetida (política de
     retry do [concurrency.md](../../data/concurrency.md#transações-no-banco)).
   - `Cancelled` só vem da área administrativa (item 9).
3. **Claim.** Transação curta, sem chamada externa, um único comando. Toma só os tipos que a release sabe
   executar (`$tiposConhecidos`, o que torna o rollback seguro quando a release nova criou um tipo novo) e que não
   estão pausados. Cada linha tomada recebe um destino calculado no próprio comando: deadline vencido → `Expired`;
   tentativas esgotadas (o job derrubou o processo em todas elas) → `Failed`; senão → `InFlight`. Todas trocam o
   token, o que invalida o dono anterior; só as `InFlight` voltam como trabalho:

   ```sql
   WITH picked AS (
       SELECT id,
              CASE WHEN deadline_at <= $agora       THEN 'Expired'
                   WHEN attempts >= max_attempts    THEN 'Failed'
                   ELSE 'InFlight' END AS next_status
       FROM ops.jobs
       WHERE kind = ANY($tiposConhecidos) AND kind <> ALL($tiposPausados)
         AND ((status = 'Pending' AND available_at <= $agora)
           OR (status = 'InFlight' AND lease_expires_at <= $agora))
       ORDER BY available_at, id
       LIMIT $lote
       FOR UPDATE SKIP LOCKED
   ),
   updated AS (
       UPDATE ops.jobs AS j
       SET status           = p.next_status,
           attempts         = j.attempts + (p.next_status = 'InFlight')::int,
           fencing_token    = j.fencing_token + 1,
           lease_expires_at = CASE WHEN p.next_status = 'InFlight' THEN $agora + $lease END,
           completed_at     = CASE WHEN p.next_status = 'InFlight' THEN NULL ELSE $agora END,
           last_error_code  = CASE WHEN p.next_status = 'Failed' THEN 'LeaseExpiredExhausted'
                                   ELSE j.last_error_code END
       FROM picked AS p
       WHERE j.id = p.id
       RETURNING j.id, j.kind, j.payload, j.fencing_token, j.attempts, j.status
   )
   SELECT id, kind, payload, fencing_token, attempts FROM updated WHERE status = 'InFlight';
   ```

   Todas as linhas atualizadas respeitam os `CHECK` do item 1 (lease só em `InFlight`, `completed_at` só em estado
   final), e nenhuma conta pode ser violada pelo incremento de `attempts`.
   - **Lote, lease e prazo são uma conta só**, validada no boot (`ValidateOnStart`): `lote × prazo do item ≤
     lease / 2`, porque o lote é processado em sequência e o último item precisa terminar dentro do lease que
     ganhou junto com o primeiro. O **prazo do item** limita a unidade inteira, não só a espera por lock: um
     `CancellationToken` com prazo (`CancelAfter` pelo `TimeProvider`) passado a todos os comandos, que o Npgsql
     converte em cancelamento do comando em curso e rollback; dentro dele, `SET LOCAL lock_timeout` e o
     `statement_timeout` da conta continuam como tetos por comando. `transaction_timeout` (PostgreSQL 17+) foi
     descartado aqui porque encerra a sessão inteira, não só a transação. Valores iniciais (hipótese): jobs
     internos com lote 10, prazo do item 3 s (`lock_timeout` 2 s) e lease 60 s (10 × 3 s = 30 s); jobs de efeito
     externo com lote 1, prazo por chamada externa 30 s e lease 60 s, renovado por heartbeat condicionado ao token
     entre chamadas quando o job faz várias (retenção em lotes no storage).
   - **Antes de cada item do lote**, o executor confere o lease restante: se `lease_expires_at - $agora` for menor
     que o prazo do item, devolve o próprio item e o resto do lote a `Pending` como no shutdown (item 12) e
     encerra o lote. Assim um item lento nunca faz o seguinte começar com lease insuficiente.
   - Polling a cada 1 s com jitter (`JitteredInterval`) quando o lote anterior veio vazio; imediatamente quando
     veio cheio.
4. **Efeito interno (expiração, prazo, rodada, retenção no banco).** Uma transação, sob o prazo do item (item 3):
   primeiro `UPDATE ops.jobs SET status = 'Done', completed_at = $agora, lease_expires_at = NULL
   WHERE id = $id AND status = 'InFlight' AND fencing_token = $t AND lease_expires_at > $agora`; zero linhas →
   rollback e descarte (`stale_completion`). Job recorrente usa, no lugar do `Done`, o reagendamento do item 8,
   com o mesmo predicado. Esse `UPDATE` tranca a linha do job, que ocupa a posição 0 da ordem global de locks (no
   lugar do registro de idempotência, que um job não usa). Ele não espera por transação de negócio, que não
   tranca jobs (item 2), nem por outro executor, cujo claim usa `SKIP LOCKED`; pode esperar só por uma ação
   administrativa curta sobre o mesmo job (item 9), que não tranca negócio. Depois, a ordem global da spec 10.4
   (guardas de usuário, rodada, sala), a revalidação depois do lock (o agregado está no estado esperado e o
   instante persistido é o do payload; senão, `Done` sem efeito) e a transição condicional de negócio (`WHERE
   version = $v AND state = ...`, C6, com a `version` lida sob o lock). Operação de outro módulo (liberar
   reserva do Matching) passa pelo coordenador de caso de uso, nunca por escrita direta na tabela alheia. Efeito
   e conclusão comitam juntos: o efeito acontece uma vez, mesmo com dois executores; um executor pausado que volta depois do commit
   do outro encontra o token trocado ou o `status` já `Done`.
5. **Efeito externo (exclusão no storage).** O banco autoriza antes, o storage executa depois, nesta ordem:
   1. **Autorizar** (transação curta): renovar o lease do job com o predicado de posse (`UPDATE ops.jobs SET
      lease_expires_at = $agora + $lease WHERE id = $id AND status = 'InFlight' AND fencing_token = $t AND
      lease_expires_at > $agora`; zero linhas → descartar o lote, `stale_completion`) e, na mesma transação, a
      transição condicional das linhas de negócio que perdem o objeto, com predicado de estado e de corte (ex.:
      `UPDATE media.uploads SET state = 'Deleted' ... WHERE state = 'Pending' AND created_at < $corte`, pela API do
      módulo dono). Só as linhas que estão no estado final dessa transição, por esta execução ou por uma anterior
      que caiu no meio, entram no passo 2.
   2. **Executar** fora de qualquer transação, com prazo por chamada e chave estável: apagar os objetos dessas
      linhas; objeto ausente é sucesso.
   3. **Concluir** (transação curta, condicionada ao token e ao lease): registrar o fim (ex.: apagar as linhas
      `Deleted` cujo objeto saiu, ou marcar `object_deleted_at`) e passar o job a `Done` ou reagendá-lo.

   O fencing garante que um executor com token antigo não **autoriza** efeito novo: o passo 1 dele afeta zero
   linhas. Ele não impede que repita um efeito que já estava autorizado (um executor pausado entre os passos 1 e 2
   que volta depois de o outro assumir); por isso o passo 2 é idempotente, e a repetição apaga o que já não
   existe.
6. **Retry e veneno.** Falha transitória (`40P01`, `55P03`, prazo do item, `5xx` do storage) volta a `Pending`
   com `available_at = $agora + atraso`, `lease_expires_at = NULL` e `completed_at = NULL` (os `CHECK` do item 1),
   atraso do `ExponentialBackoff` (15 s dobrando até o teto de 15 min, sorteado na metade de cima do intervalo),
   condicionado ao token e ao lease. `attempts` conta no claim, então um job que derruba o processo também esgota
   tentativas: o claim do item 3 leva a `Failed` (`LeaseExpiredExhausted`) a linha cujo lease venceu com
   `attempts >= max_attempts`, em vez de tomá-la de novo. Falha na última tentativa, ou falha definitiva
   (payload inválido, alvo com estado incoerente), vai a `Failed`. Alvo inexistente não é falha: o agregado foi removido (exclusão de conta,
   retenção) e o job conclui como `Done` sem efeito.
7. **Deadline.** Cada tipo declara se tem deadline. Jobs de correção de estado (expiração, prazo, transição de
   rodada) não têm: precisam rodar mesmo atrasados, e a política de retomada da spec 5.2 decide o que fazer com a
   rodada vencida. Jobs cujo efeito perde sentido com o tempo têm, e viram `Expired` em vez de rodar tarde. Job
   atrasado não abre janela para o usuário: a ação de jogo e o aceite de convite comparam o prazo persistido com
   o relógio do servidor no próprio comando (C6), então o atraso do worker só atrasa a notificação do fim.
8. **Recorrência sem agendador: uma linha fixa por tipo.** Cada tipo recorrente (retenção, limpezas) tem uma
   única linha, `dedupe_key = '<kind>'` (ex.: `retention`), que nunca chega a `Done`: ao terminar, o handler a
   devolve a `Pending` na mesma transação do efeito, com o predicado de posse do item 4 (`SET status =
   'Pending', available_at = $proxima, attempts = 0, lease_expires_at = NULL, completed_at = NULL,
   last_error_code = NULL`). `$proxima` é calculada a partir de `$agora` (não da execução anterior), com jitter do
   `JitteredInterval`. No boot, o worker garante que a linha de cada tipo recorrente existe (`INSERT ... ON
   CONFLICT DO NOTHING`). Depois de dias parado, roda uma execução, não uma por dia perdido: os handlers
   recorrentes processam "tudo o que venceu até agora" e por isso são idempotentes. Uma linha recorrente em
   `Failed` para a recorrência daquele tipo até ser resolvida, e por isso alerta (item 10).
9. **Fila de revisão.** `Failed` é a fila de revisão (API-ADM19, só leitura, sem lock): consulta por `status =
   'Failed'`, causa sanitizada em `last_error_code`, nunca dado pessoal. Reprocessar é API-ADM20, na ordem de
   locks: registro de idempotência; depois, ainda na posição 0, `UPDATE ops.jobs SET status = 'Pending',
   available_at = $agora, attempts = 0, completed_at = NULL, last_error_code = NULL WHERE id = $id AND status =
   'Failed'` (zero linhas → o job não está mais em revisão, `409`); depois a nova verificação de elegibilidade
   lendo o negócio **sem** `FOR UPDATE` (quem tranca e revalida é o handler, quando executar); auditoria no mesmo
   commit. Descartar é o mesmo `UPDATE` para `Cancelled` (com `completed_at`), com auditoria. Um `Failed` não é
   apagado pela limpeza enquanto não for resolvido.
10. **Métricas.** Por `kind`: idade do `Pending` vencido mais antigo (`$agora - min(available_at)` entre os
    vencidos, pelo `ix_jobs_due`), quantidade em `Failed` e `Expired`, `stale_completion`, leases vencidos
    retomados, duração do efeito e tamanho da tabela (`n_live_tup`, `n_dead_tup` de `pg_stat_user_tables`).
    Alerta pela idade, não pelo tamanho da fila (spec 10.6), e por `Failed > 0` em **qualquer** `kind`: num tipo
    de correção de estado é sala ou rodada possivelmente parada; num recorrente, retenção ou limpeza parada (item
    8); em qualquer tipo, acúmulo que a limpeza não remove (item 11). Limiares iniciais são hipótese a medir no
    ensaio de carga.
11. **Limpeza e manutenção da tabela.** Job recorrente apaga em lotes de até 1.000 linhas, uma transação por
    lote, os jobs `Done`, `Expired` e `Cancelled` com `completed_at` acima de 7 dias (as linhas recorrentes do
    item 8 nunca ficam em estado final e não entram)
    ([retention.md](../../privacy/retention.md)). `ops.jobs` é tabela de fila (cada job é inserido, atualizado
    duas ou três vezes e apagado), e as colunas dos predicados dos índices parciais mudam a cada transição, então
    os `UPDATE` não são HOT e geram tuplas mortas nos índices que o polling percorre. A migração define
    autovacuum por tabela (`autovacuum_vacuum_scale_factor = 0.02`, `autovacuum_vacuum_threshold = 200`,
    **hipótese**) e o ensaio de carga confere `n_dead_tup` e o plano do claim com `EXPLAIN (ANALYZE, BUFFERS)`;
    ajuste além disso só com medição.
12. **Parada do worker (spec 17.3, passo 4).** No `SIGTERM` o worker para de fazer claim, termina o job em curso
    dentro do prazo de shutdown e devolve a `Pending` os jobs do lote que não começaram: `UPDATE ops.jobs SET
    status = 'Pending', attempts = attempts - 1, lease_expires_at = NULL, completed_at = NULL WHERE id = ANY($ids)
    AND status = 'InFlight' AND fencing_token = <token de cada um> AND lease_expires_at > $agora` (a tentativa é
    devolvida; `available_at` não muda). O que não for devolvido volta pelo lease vencido.
13. **Código.** Mesma política e mesmos tipos de apoio da outbox (`ExponentialBackoff`, `JitteredInterval`,
    `TimeProvider`); o SQL de `ops.jobs` fica numa classe própria por enquanto. Se aparecer um terceiro uso do
    protocolo, extrair a fila comum.

Porquê técnico: atomicidade entre agendar e o fato de negócio, efeito interno exatamente uma vez pela mesma
transação, efeito externo protegido por fencing e idempotência, sem componente novo. Porquê de negócio: rodadas,
convites e retenção continuam corretos depois de reinício ou pausa do worker sem dependência paga, sem licença
fora da política e sem mais infraestrutura no piloto.

## Consequências

- Positivas: uma tabela e um protocolo, iguais aos da outbox; agendamento e fato comitam juntos; o cenário de
  pausa da spec 3.5 tem resposta mecânica (token) e não depende de relógio entre máquinas; transação de API
  nunca tranca job e, com uma chave por agendamento, não espera por um no fluxo normal.
- Negativas: escrevemos e mantemos claim, retry, recorrência, devolução no shutdown e limpeza; sem painel pronto
  (a fila de revisão depende da área administrativa); polling a cada segundo gera carga constante, pequena, no
  banco; o handler de cada tipo precisa revalidar estado e instante, porque a API não cancela jobs, e jobs
  obsoletos ficam na fila até rodarem sem efeito.
- Trade-off: vazão limitada pelo PostgreSQL, aceitável para a carga de referência; o gatilho de revisão mede a
  idade do item mais antigo. O lease usa o relógio do executor: com um worker numa VPS não há desvio entre
  máquinas; com vários, o desvio só antecipa ou atrasa a retomada, e a correção continua no token e no lock da
  linha.
- Follow-up de documentação (feito no PR de aceite): [model.md](../../data/model.md#opsjobs) com as colunas,
  constraints e índices do item 1; ordem de locks com a linha do job na posição 0 em
  [concurrency.md](../../data/concurrency.md#ordem-global-de-locks) e [modules.md](../modules.md#ordem-global-de-locks-spec-104);
  diagrama de estados da outbox com os nomes implementados; `Failed` de job em
  [retention.md](../../privacy/retention.md). Pendente para a implementação: migração, `AppRoleTableAccess` e os
  testes da Compliance.

## Segurança e operação

- Payload só com IDs e versões do agregado alvo; nada de e-mail, texto de mensagem ou caminho do bucket.
  `last_error_code` é um código, não a mensagem da exceção.
- O worker usa o mesmo papel `duora_app`, sem DDL. Reprocessar e cancelar job exigem permissão administrativa e
  auditoria; nenhum endpoint edita payload (spec API-ADM19).
- Lease e polling usam o `TimeProvider`; nenhum predicado de índice usa `now()`.
- Modo de manutenção (`ops.operational_flags`) pausa o claim por `kind`, sem perder os jobs; o restore em modo de
  recuperação (spec 18.4, passo 4) sobe com os tipos destrutivos pausados.
- Exclusão de conta não varre payloads: os jobs referenciam o agregado, e um alvo removido conclui sem efeito
  (item 6).

## Compliance

- **T-08, cenário da spec 3.5 (integração, M2):** `PausedWorkerLosesLeaseAndCannotCompleteJob`. Testcontainers
  com PostgreSQL 18, dois executores com `NpgsqlDataSource` próprios e um `FakeTimeProvider` comum. O executor 1
  faz o claim de um `expire-invite` (token t) e para num gancho de teste antes de concluir; o teste avança o
  relógio além do lease; o executor 2 faz o claim (token t + 1) e conclui; o executor 1 é liberado. Asserções,
  relendo por outra conexão: a conclusão do executor 1 afeta 0 linhas; a sala tem uma única transição para
  `Expired`, a reserva foi liberada uma vez e há um único evento na outbox; o job está `Done` com `fencing_token =
  t + 1`; `stale_completion` foi incrementado. Variante de efeito externo: o executor 1 pausado antes do passo 1
  (item 5) não autoriza nada e o storage falso não recebe chamada dele; pausado entre os passos 1 e 2, a repetição
  apaga um objeto já ausente e conta como sucesso. Variantes de queda: executor descartado antes do commit do efeito (o job
  volta a ser elegível depois do lease) e depois do efeito externo e antes da conclusão (efeito repetido,
  deduplicado).
- **Sabotagem:** o teste é conferido uma vez com `fencing_token = $t` removido da conclusão e precisa falhar
  (duas transições ou dois eventos).
- **Retry, veneno e deadline (integração, M2):** falha transitória reagenda dentro da faixa do backoff; a oitava
  falha vai a `Failed`; job `InFlight` com lease vencido e `attempts = max_attempts` vai a `Failed` com
  `LeaseExpiredExhausted`, token incrementado e `completed_at` gravado, **sem** derrubar o claim nem ser devolvido
  como trabalho, enquanto os outros jobs do mesmo lote são tomados; job com `deadline_at` vencido
  vira `Expired` sem executar; `SKIP LOCKED` faz dois executores simultâneos tomarem lotes disjuntos (`Barrier`,
  50 repetições).
- **Revalidação e chave por agendamento (integração, M2):** convite aceito antes do prazo → o job
  `expire-invite` conclui `Done` sem transição; prazo antecipado e prazo adiado → o job do instante novo executa
  no instante novo e o do instante antigo conclui sem efeito; ações de jogo entre o início e o prazo não tornam o
  job de prazo obsoleto; alvo apagado → `Done` sem efeito.
- **Sem espera da API (integração, M2):** com um handler parado no gancho depois de trancar a linha do job, uma
  transação de API que muda o prazo da mesma sala comita sem esperar (`pg_blocking_pids` vazio para ela).
- **Prazo do item e lease restante (integração, M2):** item que excede o prazo é cancelado e reagendado; lote cujo
  lease restante não cobre o próximo item devolve esse item e o resto a `Pending` com a tentativa devolvida.
- **Revisão (integração, M2):** API-ADM20 sobre job `Failed` o leva a `Pending` com `attempts = 0`; sobre job que
  já saiu de `Failed`, `409` sem alteração; a linha de negócio não é trancada pela rota (`pg_locks`).
- **Rollback e pausa (integração, M2):** um job de `kind` fora de `$tiposConhecidos` nunca é tomado; um `kind`
  pausado em `ops.operational_flags` fica `Pending` e volta a ser tomado ao despausar.
- **Recorrência e limpeza (integração, M2):** existe exatamente uma linha por tipo recorrente depois de dois
  boots; worker parado por três dias simulados → uma execução de `retention`, não três, e a linha volta a
  `Pending` com a próxima data; a limpeza apaga `Done` com mais de 7 dias e preserva `Failed` não resolvido e as
  linhas recorrentes.
- **Shutdown (integração, M2):** cancelar o host com lote tomado devolve os jobs não iniciados a `Pending` com a
  tentativa devolvida, `lease_expires_at` e `completed_at` nulos (nenhum `CHECK` violado).
- **Boot (unidade, M2):** opções com `lote × prazo do item > lease / 2` impedem a subida (`ValidateOnStart`).
- **Fechamento dos tipos (arquitetura, M2):** todo valor do `CHECK` de `kind` tem exatamente um handler
  registrado no worker, e vice-versa (enumeração por reflexão contra a lista usada na migração).
- **T-18 (unidade e integração, M2):** prazo de sala e rodada resolvido pelo relógio do servidor, com job
  atrasado e com retorno após longa queda.
- **Privilégios (integração, M2):** `ops.jobs` entra em `AppRoleTableAccess` como `ReadWrite` no PR da
  migração.
- **Dependências:** o check `supply-chain` barra licença fora da allowlist; Hangfire ou Quartz só entram com ADR
  novo.
