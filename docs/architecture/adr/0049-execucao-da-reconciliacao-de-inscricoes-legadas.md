# ADR-0049: Execução da reconciliação de inscrições legadas

- **Status:** Accepted (05/11/2026). O dono aprovou as decisões D1 a D5. As fatias de código que as implementam
  (migração, autorização e contratos) continuam exigindo revisão humana antes do merge (AGENTS.md).
- **Origem:** [#321](https://github.com/BipoXDXD/duora/issues/321). Simplifica as seções 6 a 11 da proposta de
  reconciliação e segue o [ADR-0044](0044-inscricoes-de-contas-suspensas.md) e o
  [ADR-0048](0048-leitura-de-sessao-sem-toque-na-previa-de-reconciliacao.md).
- **Revisar quando:**
  - uma prévia real autorizada mostrar volume que peça lotes ou tela;
  - a [#320](https://github.com/BipoXDXD/duora/issues/320) implementar um consumidor de
    `events.waitlisted_registration_promoted`;
  - a retenção da outbox mudar;
  - a política de suspensão mudar.

## Contexto

A main já tem três peças:

- o seletor conservador (`LegacyRegistrationSelector` v1);
- a prévia administrativa sem escrita (API-ADM25, ADR-0048);
- o corte L/U, que vem só da configuração e está ausente por padrão.

Ainda falta transformar uma prévia revisada em algo aplicável e aplicá-lo com as garantias do ADR-0044:

- revalidar sob locks;
- usar só a medida original;
- não deixar efeito parcial;
- ser idempotente após resposta incerta;
- não deixar as promoções da reconciliação virarem avisos sem decisão própria.

A proposta original tinha envelope assinado, lotes com pausa e um contexto de notificação em tabela própria. Não
se sabe se existe algum caso real, e a #321 não autoriza consulta. O desenho abaixo mantém as garantias com menos
estado novo.

Fatos do código em `de2e0ba2`:

- **Escritas por evento do plano.** `RegistrationTransitions.WithdrawForSuspensionAsync` percorre só `plan.Events`.
  - A PK das inscrições é `(event_id, user_id)`. Com o plano completo já trancado, um plano de escrita de um único
    evento aprovado retira só aquela linha do sujeito, sem SQL duplicado e sem parâmetro opcional no escritor normal.
  - A retirada incrementa `events.version` uma vez.
  - Cada promoção incrementa a versão de novo e grava o fato `events.waitlisted_registration_promoted` v1 na outbox,
    com a versão nova.
- **Consumidor e retenção do fato.** Nenhum consumidor desse fato existe. `PromoteAsync` não agenda entrega, e a
  retenção (`RetentionService`) apaga depois de 7 dias os fatos da outbox sem entrega.

## Decisão

### D1. Manifesto por digest recalculado, sem envelope assinado

**Na prévia.** A prévia passa a devolver, por unidade da página, um `unitDigest`: SHA-256 de uma serialização
canônica com:

- a versão do seletor;
- o descritor de corte;
- o sujeito, a medida e a versão da medida;
- os itens, cada um com evento, **versão da inscrição** (`registrationVersion`), estado e tetos.

**Na criação do run.** `POST /runs` recebe só pares `(subjectUserId, originalMeasureId)` com o digest visto, mais
justificativa e referência.

- Numa transação `REPEATABLE READ`, o servidor reclassifica com o corte configurado e recalcula os digests.
- Se todos baterem, grava o run e as unidades `Pending` na mesma transação.
- Qualquer divergência responde `412` e não grava nada.

**Alternativa descartada: envelope assinado com Data Protection.** A assinatura só protege quando o servidor
confia em conteúdo trazido pelo cliente; aqui ele recalcula tudo. O envelope traria key ring compartilhado,
rotação e mais um artefato sensível. Perde-se a validade de 15 min e o vínculo à sessão, que a #321 não exige,
porque toda aplicação revalida sob locks.

### D2. Uma unidade por pedido, sem lotes

A unidade é uma conta com sua medida original. Cada `POST /runs/{runId}/units/{unitId}:apply` aplica uma unidade
numa transação, e o recibo da unidade, gravado nela, é o checkpoint.

`POST /runs/{runId}:close` exige `If-Match` com a versão do run e fecha o run. Depois disso nenhuma unidade nova
começa. Uma unidade que já tinha o run trancado termina (ver o protocolo).

**Simplificações.** Só pode haver **um run `Open` por vez** (índice único parcial em `runs where state='Open'`).
Criar um segundo responde `409 RUN_ALREADY_OPEN`. Isso
impede que duas execuções disputem a mesma conta. Não há tabela de lotes, pausa, orçamento de admissão nem resultado
parcial.

**Alternativa descartada: lotes de até 10 unidades com pausa e resumo reparável.** O primeiro lote da proposta já
era de uma unidade, e o volume é desconhecido. Lotes ou runs paralelos entram por ADR novo se uma prévia real
autorizada justificar.

### D3. Promoções vinculadas à unidade, com gate para a #320

**O vínculo.** Na transação da unidade, para cada evento aprovado, os fatos de promoção gerados entram em
`ops.registration_reconciliation_unit_facts`.

- A faixa é `resource_version` maior que a versão lida antes do helper e menor ou igual à versão lida depois. O
  evento está trancado `FOR UPDATE`, então nenhuma versão de outra transação se intercala.
- A quantidade vinculada tem de ser igual às promoções efetivas; se não for, é rollback.
- Nada vincula por horário, TraceId ou consulta posterior ao commit.
- Os IDs também ficam no recibo imutável da unidade, como evidência.
- O fato v1 e seu payload não mudam.

**Retenção.** A FK `unit_facts.fact_id → ops.outbox_events` é `ON DELETE CASCADE`. Quando a retenção apaga um fato
antigo sem entrega, o vínculo vai junto: nunca existe fato vivo sem vínculo, e a retenção não trava. A FK para a
unidade é `RESTRICT`.

**Gate.** Um consumidor desse fato, inclusive o da #320, trata como suprimido todo fato vinculado a uma unidade e
não cria entrega. Não se aplica reconciliação em ambiente com consumidor que ignore o vínculo.

**Alternativa descartada: `ops.notification_fact_context` agora.** Anteciparia o schema de uma feature com decisões
de produto ainda abertas.

### D4. Um moderador, com justificativa

**Barreira.** As rotas de escrita usam a mesma barreira da prévia: sessão administrativa, papel `moderator` lido ao
vivo, MFA e TOTP da última hora. Acrescentam CSRF e `Idempotency-Key` na criação e na aplicação.

**Sessão.** Escritas e `GET /runs/{runId}` usam a sessão normal, **com toque**. O regime sem toque do ADR-0048
continua restrito à prévia.

**Justificativa.** A criação exige `justification` e `authorizationReference`: texto limitado, sem caracteres de
controle. Não trazer dado de participante nesses campos é regra do runbook, não garantia do código.

**Pontos de autorização** dentro da transação de cada unidade (protocolo, passos 3 e 6):

- antes de qualquer replay ou trabalho;
- de novo depois das guardas e do `FOR SHARE` nas contas, já antes das transições.

**Linearização.** Uma revogação de papel, sessão ou MFA posterior ao segundo ponto deixa a unidade em curso
confirmar e nega a próxima.

**Alternativa descartada: dois moderadores**, um que cria e outro que aplica. Exige uma segunda conta moderadora real
com MFA, que não existe, e mais estado. A autorização operacional específica (ambiente, escopo e janela) continua
fora do código.

### D5. Só API

A operação é por API, com runbook. A tela administrativa entra só se uma prévia real autorizada mostrar casos.

## Persistência

Migração aditiva no schema `ops`. Operations é dono das tabelas; Events continua dono das transições e Safety da
medida.

**`registration_reconciliation_runs`:**
- id (UUIDv7) e versão do seletor;
- id, `lower` e `upper` do descritor de corte;
- criador real, justificativa e referência;
- estado `Open|Closed`, versão e instante de criação;
- chave de idempotência e hash do intento, únicos por ator;
- índice único parcial que garante um só run `Open`.

**`registration_reconciliation_units`:**
- run e `unique(run, sujeito, medida)`;
- medida original e sua versão;
- itens aprovados: evento, `registrationVersion` observada, estado, `maxReleasedSeats` e `maxPromotions`;
- estado `Pending|Applied|SkippedChanged|NeedsReview`;
- recibo: resultado e motivo, IDs dos fatos, executor, instante real, correlação, chave e hash do pedido de aplicação.

Um trigger torna o manifesto imutável sempre e o recibo imutável quando terminal. Um estado terminal só nasce de
`Pending`.

**`registration_reconciliation_unit_facts`:** `fact_id` (PK, FK `CASCADE` para a outbox) e `unit_id` (FK `RESTRICT`).

Nenhuma tabela guarda cookie, ticket, TOTP, nome, e-mail ou motivo de moderação.

## Protocolo de uma unidade

READ COMMITTED, `lock_timeout` de 2 s e `statement_timeout` de 10 s. A ordem de locks do ADR-0044 fica intacta; o
run e a unidade vêm antes das guardas e só a reconciliação os toca.

1. Trancar o run `FOR SHARE`. Run inexistente ou unidade de outro run: `404`. O apply nunca escreve no run, então
   applies não disputam esse lock. O `:close` usa `FOR UPDATE` no run: espera as unidades em curso, e as unidades
   seguintes veem `Closed`. Se o close estourar o `lock_timeout`, o runbook manda repetir.
2. Trancar a unidade `FOR UPDATE NOWAIT`. Ocupada: `503` com `Retry-After`.
3. **Primeiro ponto de autorização.** Falhou: rollback e `403`, sem efeito.
   - Unidade já terminal: devolve o recibo, que é a idempotência, sem chamar escritor. Vale também com o run
     `Closed`, para recuperar a resposta perdida.
   - Unidade `Pending` com o run `Closed`: `409 RUN_CLOSED`.
   - A `Idempotency-Key` é única por (ator, chave) entre os recibos e só detecta intento divergente.
     A mesma chave já usada em outra unidade dá `409`. Uma chave diferente numa unidade já terminal devolve o
     recibo, porque a idempotência é a do domínio.
4. Conferir o corte. O descritor configurado tem de ser idêntico ao do run (id, `lower` e `upper`). Ausente ou
   diferente: `409 CUTOVER_CHANGED`, sem efeito, com a unidade ainda `Pending`.
5. Montar o plano completo com `RegistrationLockPlan.ForSuspensionAsync(subject, moderador)`, que inclui o moderador,
   o alvo e os usuários na espera.
   - Tetos: 50 eventos e 1.000 usuários.
   - Teto excedido: recibo `NeedsReview` (`PlanTooLarge`) e commit, antes das guardas. É intencional: grava só o
     recibo e a auditoria, sem efeito de negócio, e a unidade sai da execução. Revê-la exige um run novo.
6. Trancar as guardas (UUID crescente) e as contas `FOR SHARE`. **Segundo ponto de autorização.**
7. Trancar a medida original `FOR SHARE` e depois todos os eventos em id crescente, pelo `LockEventsAsync` do plano
   completo.
8. Reler o plano e aplicar `RequireCovered` sobre o plano completo. Recurso novo: rollback integral e recomeço,
   até três tentativas; esgotado, `503`.
9. Reclassificar com o seletor e o corte do run e comparar com o manifesto: medida (id, versão, ativa) e versão de
   cada inscrição. Mudanças de capacidade ou da fila de terceiros não mudam essas versões; quem as pega são os tetos.
   Desfechos, cada um com recibo e commit, sem retirada:
   - qualquer divergência: `SkippedChanged` com o motivo;
   - teto de liberação ou de promoção excedido sob lock: `NeedsReview`;
   - reserva ativa inesperada: `NeedsReview`.
10. Para cada evento aprovado, em ordem:
    - ler a versão do evento;
    - `now = TimeProvider`;
    - `WithdrawForSuspensionAsync(conn, new RegistrationLockPlan([evento], plano.Users), subject, medidaOriginal, now)`;
    - ler a versão de novo e vincular os fatos (D3).

    O plano restrito nunca passa por `LockEventsAsync` nem por `RequireCovered`.
11. Conferir os efeitos contra os tetos e a contagem de vínculos. Divergir é defeito: exceção, rollback, `500` sem
    detalhes e correlação no log. A unidade fica `Pending`, e o operador investiga e fecha o run.
12. Gravar a auditoria e o recibo `Applied`. Commit único.

**Fora do alcance da unidade:** `identity.users`, `safety.moderation_actions`, salas e rodadas, e outras inscrições do
sujeito fora do manifesto.

**Deadlock (40P01):** rollback, `503`, defeito a investigar, sem retry.

## Auditoria

Ações fechadas novas, com ator Moderator, ID real e correlação. Nenhuma reatribui a medida histórica.

- `ops.registration_reconciliation_run_created` e `ops.registration_reconciliation_run_closed`: alvo run.
- `events.legacy_registration_reconciliation_applied`: alvo unidade, na transação dos efeitos.
- `events.legacy_registration_reconciliation_skipped`: alvo unidade, com motivo fechado. Cobre `SkippedChanged` e
  `NeedsReview`.

As auditorias System existentes de retirada e promoção não mudam.

## Consequências

- **Menos estado que a proposta.** Duas tabelas de operação e uma de vínculo, nenhuma chave criptográfica, nenhum
  estado parcial e um run aberto por vez.
- **Contrato da prévia.** Ela ganha um campo aditivo (`unitDigest`). É mudança de contrato e tem revisão humana.
- **Prévia refeita.** Uma mudança entre a prévia e o run obriga a refazer a prévia, e trocar o corte com um run
  aberto bloqueia esse run (`409`). Os dois comportamentos são desejados.
- **Volume.** Volume grande exige um pedido por unidade. O custo é aceito até haver evidência.
- **Gate para a #320.** A #320 herda o gate D3, que precisa constar no seu desenho e num teste de contrato.
- **Limites deste ADR.** Sem `RegistrationReconciliation:Cutover`, nenhuma unidade é candidata e nenhum run tem
  unidades. Este ADR não cadastra corte, não consulta dados reais e não autoriza aplicação.

## Fatiamento

1. **Este ADR**, só documentação.
2. **Manifesto:**
   - `unitDigest` na prévia;
   - migração com triggers e índices;
   - `POST /runs` e `GET /runs/{runId}`;
   - auditoria de criação;
   - OpenAPI, catálogo e tipos gerados.
3. **Aplicação:**
   - `:apply` e `:close`;
   - coordenador em Operations;
   - vínculo dos fatos;
   - auditorias de aplicação, skip e close;
   - **runbook** `docs/operations/registration-reconciliation.md`, com critérios e autorização de aplicação real,
     evidência por unidade, verificação posterior, interrupção segura (close), release compatível e reversão sem
     apagar histórico.

## Compliance

As fatias 2 e 3 só entram com PostgreSQL real, conexões independentes e dados sintéticos. Sem InMemory nem SQLite.

- **Manifesto:**
  - criar run não altera Events nem Safety;
  - digest divergente: `412`, nada gravado;
  - segundo run `Open` recusado, inclusive com duas criações concorrentes;
  - triggers recusam alterar manifesto e recibo terminal, e recusam transição fora de `Pending`.
- **Corridas, com barreiras por `pg_blocking_pids`, sem deadlock e sem Busy fora dos casos previstos:**
  - duas aplicações da mesma unidade: uma fica `Applied`, a outra recebe `503` ou o recibo;
  - revogação da medida antes e depois da guarda do sujeito;
  - perda do papel ou da sessão do moderador antes e depois do segundo ponto de autorização;
  - nova suspensão;
  - inscrição, check-in e promoção concorrentes;
  - início do evento exatamente em `starts_at`;
  - encerramento e cancelamento;
  - `:close` contra `:apply` em curso.
- **Recuperação:**
  - crash antes do commit: sem efeito, unidade `Pending`;
  - replay após resposta perdida: recibo, sem escritor, inclusive com o run já `Closed`;
  - rollback quando a contagem de vínculos ou os tetos divergem;
  - corte trocado com run aberto: `409`.
- **Retenção:** apagar um fato vinculado leva o vínculo junto e não interrompe a retenção.
- **HTTP:**
  - `401` e `403`;
  - `400` para justificativa ou formato inválidos;
  - `404`, `409`, `412` e `428` (`If-Match` ausente no close);
  - `503` com `Retry-After`;
  - allowlist dos corpos.
- **Gate D3:** um teste de contrato o fixa quando houver consumidor.

## Adendo de 05/11/2026: decisões da implementação da fatia 3

Registrado no mesmo PR da aplicação. Fecha lacunas do texto acima, sem mudar D1 a D5.

- **Motivo do skip.** `ops.audit_events` não tem coluna para motivo, e um sufixo na ação estouraria os 64 caracteres. A
  auditoria `events.legacy_registration_reconciliation_skipped` aponta para a unidade, e o motivo fechado fica no recibo
  imutável dela (`receipt_reason`).
- **Run de outro seletor.** O protocolo manda reclassificar com o seletor do run, mas o binário só conhece o seletor atual.
  Run com `selector_version` diferente responde `409 CUTOVER_CHANGED`, sem efeito, com a unidade ainda `Pending`.
- **Chave de outra unidade.** O `409 IDEMPOTENCY_KEY_REUSED` tem prioridade sobre o recibo: uma chave que já gravou outra
  unidade é recusada mesmo com a unidade pedida terminal. Chave inédita ou a própria chave da unidade devolve o recibo.
- **Fechamento repetido.** `If-Match` na versão atual de um run já fechado responde `409 RUN_CLOSED`, sem escrever.
- **`:apply` e `:close` sem cota.** Não gastam a cota da prévia. Ela protege a classificação de toda a população; a aplicação
  relê uma conta por vez, um run tem no máximo 10 unidades e, com 6 pedidos por minuto, a cota atrasaria a própria
  operação, inclusive o `:close` de uma interrupção.
- **Liberação exata.** O passo 11 exige a liberação igual à do manifesto, e não só abaixo do teto: o passo 9 já igualou
  versão e estado. O teto continua valendo para as promoções, que a fila e o relógio ainda podem reduzir.
- **Recibo na leitura (decisão do dono na revisão do PR).** `GET /runs/{runId}` passa a expor `units[].receipt`, de forma
  aditiva, para a evidência e a recuperação de resposta incerta não dependerem de um POST. Repetir `:apply` só é replay em
  unidade terminal. Se o dono recusar, retira-se o campo antes do merge e o runbook volta a usar o replay.
