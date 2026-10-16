# ADR-0024: Fluxo da verificação etária: worker com outbox, webhook inline e regra pura no Core

- **Status:** Accepted (2026-10-15)
- **Origem:** spec 4.2, 8.2 (API-P05, API-P06), 8.5 (webhooks), 12.2, 16.3 e 16.4; threat model TM-35 a TM-40 e
  TM-64; [ADR-0003](0003-monolito-modular-web-worker.md), [ADR-0019](0019-implementacao-identidade-sessao-outbox.md)
  e [ADR-0021](0021-jobs-duraveis-e-leases-no-postgresql.md); issue #9, item 5 (ECA Digital)
- **Revisar quando:** houver fornecedor contratado (protocolo real de assinatura, consulta de reconciliação), parecer
  jurídico sobre o ECA Digital art. 9º § 1º ("a cada acesso"), ou o caso de revisão de `NotAdult` entrar no M2

## Contexto

A spec 12.2 exige o adaptador `IAgeEligibilityProvider`, política versionada e os estados `Pending`,
`VerifiedAdult`, `Rejected`, `Expired` e `ManualReview`, e proíbe aceitar `isAdult: true` do navegador. Não há
fornecedor escolhido: o desenvolvimento usa pessoas sintéticas e um fornecedor simulado. Faltava decidir onde o
fornecedor é chamado, como o callback é autenticado e processado, que resultado muda a conta para qual estado e
como a validade convive com a dúvida jurídica do ECA Digital.

Alternativas consideradas:

- **Chamar o fornecedor dentro do request de API-P05.** Descartada: um fornecedor lento ou fora do ar prende a
  requisição e o pool (TM-39), e um retry do navegador abriria duas verificações pagas.
- **Processar o webhook de forma assíncrona (gravar o recibo e decidir no worker).** Descartada: o efeito é uma
  transição de duas linhas do próprio banco, curta e sem chamada externa; o atraso e a segunda fila não compram
  nada. A unicidade do recibo e o UPDATE condicional já tornam o processamento inline idempotente.
- **Assinatura do callback só por segredo compartilhado num header, sem timestamp.** Descartada: não protege contra
  replay (TM-36) e não permite rotação sem janela de falha.
- **`Inconclusive` restringe a conta.** Descartada: puniria a pessoa por falha do fornecedor; a conta continua
  pendente e a solicitação vai para revisão manual.
- **`NotAdult` só rejeita a solicitação e deixa a conta pendente.** Descartada: a spec 12.2 manda que suspeita de
  menor restrinja o acesso.

## Decisão

Decisões do responsável técnico (2026-10-15):

1. **Fornecedor só no worker.** API-P05 grava a solicitação e um evento na outbox na mesma transação. O worker chama
   `IAgeEligibilityProvider.StartAsync` com timeout, retry pelo `ExponentialBackoff` existente e teto de tentativas,
   e grava a referência e a URL do fluxo do fornecedor. O navegador consulta API-P06 até o próximo passo aparecer.
2. **Webhook `/webhooks/{provider}` processado inline**, numa transação: `ops.webhook_receipts` com
   `ON CONFLICT DO NOTHING` em `(provider, provider_event_id)`; depois o UPDATE condicional da solicitação
   `WHERE state = 'Pending'`; a transição da conta; a auditoria. Assinatura do fornecedor simulado no estilo Stripe:
   header com `t=<unix>` e um ou mais `v1=<hex>`, HMAC-SHA256 sobre `"{t}.{corpo}"`, janela de ±5 minutos pelo
   `TimeProvider`, comparação com `CryptographicOperations.FixedTimeEquals` e dois segredos válidos ao mesmo tempo
   para rotação. Corpo de até 64 KiB, lido como bytes crus antes de qualquer parse. `{provider}` fora dos adaptadores
   registrados responde 404.
3. **`sessionId` no contrato, sem comportamento.** `AgeCheckStart` e `GetEligibility` já recebem a sessão de login, e
   a validade é configurável (`AgeVerification:Validity`). Isso deixa o caminho aberto para "verificação a cada
   acesso" enquanto o item 5 da issue #9 está pendente, sem mudar o contrato depois.
4. **`VerifiedAdult` basta** para `PendingEligibility → Active`.
5. **`NotAdult` → conta `Restricted`** e auditoria (o caso de revisão desse resultado fica como lacuna do M2).
   **`Inconclusive` → solicitação `ManualReview`**, e a conta continua `PendingEligibility`.
6. **Idempotência natural em API-P05:** uma solicitação aberta por pessoa (índice único parcial em
   `state IN ('Pending', 'ManualReview')`); pedir de novo devolve a aberta.

Regras que acompanham as decisões:

- O navegador nunca manda resultado: o DTO de API-P05 não tem campo, e corpo com `isAdult` (ou qualquer campo) é
  400 com o banco inalterado (TM-35).
- O app não sobe com o fornecedor `Fake` fora de `Development` e `Testing`.
- Segredos de assinatura em options com `ValidateOnStart`; o boot falha sem eles.
- A URL do fluxo devolvida pelo fornecedor só é mostrada se a origem estiver na allowlist configurada do adaptador;
  a URL de retorno enviada ao fornecedor é montada da origem pública, nunca da requisição.
- `/webhooks/*` é exceção explícita na allowlist de rotas públicas, sem cookie e sem CSRF.
- Nenhum dado real; nenhum campo de documento, imagem, biometria ou data de nascimento.

Desdobramentos decididos aqui, por serem consequência direta das decisões acima:

- **A regra é pura e mora no Core.** `AgeEligibility.Decide` traduz um resultado em novo estado da solicitação,
  validade e estado da conta; `AgeEligibility.Evaluate` diz se a conta participa num instante e qual é o próximo
  passo. Elegível é conta `Active` com verificação `VerifiedAdult` cujo `expires_at` é posterior ao instante. Nenhum
  resultado tira a conta de `Restricted`, `Suspended` ou `DeletionPending`, e o tempo só retira elegibilidade
  (propriedades em `AgeEligibilityPropertyTests`).
- **Solicitação abandonada.** Uma solicitação `Pending` sem resultado por `AgeVerification:RequestLifetime`
  (padrão 24 h) deixa de bloquear um novo pedido; API-P05 a marca `Expired` antes de criar a nova. `ManualReview`
  não vence sozinha.
- **Sem fornecedor configurado, o app sobe.** `AgeVerification:Provider` ausente (ou `None`) desliga o fluxo:
  API-P05 responde 503 e a conta continua pendente (TM-40: indisponibilidade nunca libera participação). Isso
  mantém o deploy de produção possível antes da contratação, sem o fornecedor simulado.
- **Consulta de reconciliação ao fornecedor (`decision_source = 'ProviderQuery'`)** fica para o adaptador real.

Porquê técnico: o request do usuário nunca espera o fornecedor; o callback tem autenticação criptográfica, janela
contra replay, unicidade e transição condicional, então repetição e concorrência resultam em uma transição só. A
regra pura é testável por propriedades sem banco. Porquê de negócio: o fluxo inteiro fica pronto e testado antes da
contratação, e trocar de fornecedor é escrever um adaptador.

## Consequências

- Positivas: a liberação de participação depende só de callback autenticado associado a solicitação criada pelo
  servidor; repetir, atrasar ou reordenar callbacks não duplica efeito; a política de validade muda por
  configuração.
- Negativas: a pessoa vê um passo intermediário ("aguardando o fornecedor") até o worker obter o fluxo; o
  fornecedor simulado não exercita o protocolo real, que pode exigir outro esquema de assinatura e consulta de
  reconciliação.
- Trade-off: o processamento inline do webhook segura uma conexão do pool durante a transação curta; aceito porque
  não há chamada externa dentro dela.

## Segurança e operação

- Corpo do callback é P3: guardado só o mínimo validado contra schema próprio e o SHA-256 do corpo.
- Segredos do fornecedor nunca em log, erro ou resposta; a rotação é: configurar o segredo novo como segundo,
  trocar no fornecedor, remover o antigo.
- Falha definitiva do worker deixa a entrega em `Failed` (fila de revisão) e a conta pendente.
- Gatilho operacional: idade da entrega pendente mais antiga do consumidor `age-verification-provider`.

## Compliance

- Regra pura: `Duora.UnitTests.Identity.AgeEligibility*Tests` e `AgeEligibilityPropertyTests` (mutantes da regra
  vistos falhando).
- TM-35, TM-38, T-17 (fornecedor lento ou falhando), boot sem `Fake` fora de Development/Testing: testes de
  integração do PR da API e do worker (A2).
- TM-36 (assinatura inválida, replay fora da janela, callback duplicado, dois callbacks concorrentes): testes de
  integração do PR do webhook (A3).
- TM-64: `EndpointAuthorizationTests` com `/webhooks/{provider}` na allowlist; T-01 parcial pela policy
  `EligibleParticipant` (A4).
- Revisão humana obrigatória (autorização e integração externa, [permissions.md](../../automation/permissions.md)).
