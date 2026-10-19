# ADR-0024: Fluxo da verificação etária: worker com outbox, webhook inline e regra pura no Core

- **Status:** Accepted (2026-10-15)
- **Substituído em parte:** a noção de "adulto" sem jurisdição, pelo [ADR-0026](0026-publico-idiomas-e-regiao-do-servidor.md) (2026-10-19; adendo de maioridade por jurisdição)
- **Origem:** spec 4.2, 8.2 (API-P05, API-P06), 8.5 (webhooks), 12.2, 16.3 e 16.4; threat model TM-35 a TM-40 e
  TM-64; [ADR-0003](0003-monolito-modular-web-worker.md), [ADR-0019](0019-implementacao-identidade-sessao-outbox.md)
  e [ADR-0021](0021-jobs-duraveis-e-leases-no-postgresql.md); issue #9, item 5 (ECA Digital)
- **Revisar quando:** houver fornecedor contratado (protocolo real de assinatura, consulta de reconciliação) ou o
  caso de revisão de `NotAdult` entrar no M2 (o parecer sobre o ECA Digital não será feito: adendo de 2026-10-16)

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

## Adendo (revisões dos PRs #42, #39 e #43, 2026-10-16)

Detalhes decididos na revisão, sem mudar as decisões acima:

- **Evento repetido com outro corpo (decisão do responsável técnico).** Mesmo `provider_event_id` com `payload_sha256`
  diferente do recibo gravado: 204 sem efeito (vale o primeiro), log estruturado de alerta sem o corpo e métrica
  `webhook_event_conflict` (meter `Duora.Webhooks`). Não responde 409, para o fornecedor não ficar repetindo.
- **Callback tardio.** `Pending` além de `RequestLifetime` e ainda não substituída recebe o resultado normalmente.
  Numa solicitação `Expired`, `NotAdult` ainda restringe a conta (auditoria `identity.age_verification_late_not_adult`
  com o id da solicitação); `VerifiedAdult` e `Inconclusive` não mudam nada.
- **Solicitação abandonada.** Também conta como abandonada a `Pending` sem referência cuja tarefa do worker terminou
  `Failed` ou `Expired`. `ManualReview` responde 202 com `WaitForReview`; conta restrita ou suspensa recebe 409 mesmo
  com uma aberta; `PendingEligibility` com `VerifiedAdult` válida volta a `StartVerification`.
- **Rotas de participação.** Quando surgir a primeira rota de evento (M2), entra um grupo `/api/v1` de participação
  com `RequireEligibleParticipant()` aplicado no grupo, e não rota a rota.
- **Rate limit** de API-P05 e do webhook: aceito sem limite próprio por ora (TM-39).

## Adendo: verificação única no cadastro, decisão do dono 2026-10-16

Decisão do dono na issue #9 (2026-10-16): não haverá análise jurídica do ECA Digital (item 5 encerrado); a
verificação etária acontece só no cadastro da conta; uma `VerifiedAdult` não expira e não há reverificação por
acesso nem por sessão. O status continua **Accepted**; este adendo substitui a parte da decisão 3 que tratava da
validade e o desdobramento "elegível é `VerifiedAdult` cujo `expires_at` é posterior ao instante".

- **Sem validade.** `AgeVerification:Validity` e `AgeVerificationPolicy.Validity` deixam de existir. Elegível é conta
  `Active` com uma verificação `VerifiedAdult`, em qualquer instante. O tempo continua sem conceder elegibilidade
  (TM-40): ele só faz uma solicitação `Pending` abandonada (`RequestLifetime`) virar `Expired` e dar lugar a uma
  nova. `Expired` passa a ser só estado de **solicitação** abandonada, nunca de verificação adulta vencida.
- **A decisão mais recente decide, e a conta manda antes dela.** A elegibilidade lê a decisão mais recente da
  pessoa (`VerifiedAdult`, `Rejected` ou `ManualReview`, `AgeEligibility.Decisions`) e, só sem nenhuma, o pedido mais
  recente (`Pending` ou `Expired`). Pedido sem decisão nunca apaga uma decisão: a `VerifiedAdult` não vence, e uma
  `Rejected` ou `ManualReview` posterior prevalece sobre ela mesmo que um pedido venha depois. Conta fora de `Active`
  nunca é elegível: na dúvida, não elegível. Pelo fluxo não se cria solicitação depois de uma `VerifiedAdult` nem de
  uma `Rejected`; a regra protege linhas antigas, do tempo da validade de 365 dias, e contas devolvidas a `Active`
  por outro caminho. (Correção da revisão de segurança do PR #59: a primeira versão priorizava sempre a
  `VerifiedAdult`, o que deixava elegível uma conta ativa com `Rejected` ou `ManualReview` posterior.)
- **A sessão sai do contrato.** Sem "verificação a cada acesso", `AgeCheckStart` e `GetEligibility` não recebem
  mais a sessão de login. O pedido ao worker (`identity.age-verification-requested`) sai dela em dois deploys: nesta
  release o worker a ignora (presente ou ausente) e API-P05 ainda a grava, para o worker da release anterior; a
  gravação sai na release seguinte ([deploy.md](../../operations/deploy.md)). A policy e API-P05 continuam exigindo
  um principal de sessão.
- **`expires_at` em expand/contract.** Nesta release a coluna não é mais gravada nem lida, e a constraint
  `ck_age_verifications_verified_expiry` sai (migration `AgeVerificationWithoutExpiry`); valores antigos ficam,
  ignorados. **A coluna sai num PR futuro** (contract), depois que nenhuma release em uso a ler. Rollback para a
  release anterior falha fechado: o código antigo trata `VerifiedAdult` sem `expires_at` como vencida e oferece
  nova verificação (a pessoa refaz; ninguém ganha participação).
- **`PendingEligibility` com `VerifiedAdult` (revisão do M4 do PR #37).** O fluxo não produz esse par: o mesmo
  callback grava `VerifiedAdult` e leva `PendingEligibility` a `Active`. Se aparecer, a conta não participa e o
  próximo passo é `NotAvailable`: nenhuma verificação nova é oferecida, porque nada volta a exigir verificação.
  Regra para o futuro: toda transição que devolver uma conta ao fluxo (revisão de restrição, M2) ou que gravar
  `VerifiedAdult` por outro caminho (revisão manual) leva a conta a `Active` na mesma transação se já houver
  `VerifiedAdult`.
- **`NotAdult` tardio (revisão do M8 do PR #39).** Continua restringindo a conta em verificação, mesmo a de quem já
  é adulta verificada: a verificação permanente não blinda contra suspeita de menor (spec 12.2), e só a revisão do
  M2 tira a restrição. `VerifiedAdult` e `Inconclusive` tardios numa `Expired` seguem sem efeito (falha fechada; a
  pessoa pede de novo se ainda não for adulta verificada).
- **Suspeita de menor é durável (revisão independente do PR #59).** Todo `NotAdult`, em qualquer estado da
  solicitação (inclusive corrigindo a `VerifiedAdult` da mesma solicitação, ou chegando com a conta suspensa),
  grava `age_verifications.minor_suspected_at` (o primeiro instante; repetição não muda nada). Numa `Pending` a
  solicitação vira `Rejected`; nas demais o estado fica. A elegibilidade nega (`NotAvailable`) enquanto qualquer
  solicitação da pessoa tiver a suspeita, qualquer que seja o estado da conta: se ela voltar a `Active` por outro
  caminho (fim de suspensão, erro de operação), a `VerifiedAdult` não volta a valer. **Lacuna do M2:** só a revisão
  humana remove a suspeita; o caso de uso, a autorização dele, a auditoria e a regra de quando a `VerifiedAdult`
  volta a valer ficam para o M2. Até lá, ninguém remove (nem por SQL de operação). Os `NotAdult` recebidos antes da
  coluna entram pela migration de dados `AgeVerificationMinorSuspicionBackfill`, com a regra que falha fechado: toda
  `Rejected` (só `NotAdult` produz esse estado, qualquer que seja o `decision_source`) recebe
  `coalesce(decided_at, requested_at)`, e toda solicitação citada na auditoria
  `identity.age_verification_late_not_adult` recebe o instante do primeiro registro. Não há `Down`.

## Consequências

- Positivas: a liberação de participação depende só de callback autenticado associado a solicitação criada pelo
  servidor; repetir, atrasar ou reordenar callbacks não duplica efeito; a verificação é feita uma vez, no cadastro
  (adendo de 2026-10-16).
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
- Verificação única (adendo de 2026-10-16): `AgeEligibilityPropertyTests.AdultVerificationStaysEligibleForever` e
  `AnAdultVerificationNeverAsksForANewOne`; `EligibleParticipantPolicyTests.AdultVerificationStaysEligibleYearsLater`
  (`FakeTimeProvider`, 11 anos); `AgeVerificationRequestTests.VerifiedAdultCannotOpenAnotherRequestYearsLater` e
  `PendingEligibilityWithAnAdultVerificationIsNeverAskedToVerifyAgain`; `AgeWebhookTests.LateNotAdultRestrictsEvenAVerifiedAdult`.
- TM-35, TM-38, T-17 (fornecedor lento ou falhando), boot sem `Fake` fora de Development/Testing: testes de
  integração do PR da API e do worker (A2).
- TM-36 (assinatura inválida, replay fora da janela, callback duplicado, dois callbacks concorrentes): testes de
  integração do PR do webhook (A3).
- TM-64: `EndpointAuthorizationTests` com `/webhooks/{provider}` na allowlist; T-01 parcial pela policy
  `EligibleParticipant` (A4).
- Revisão humana obrigatória (autorização e integração externa, [permissions.md](../../automation/permissions.md)).

## Adendo (2026-10-19): maioridade por jurisdição

Com o público principal fora do Brasil ([ADR-0026](0026-publico-idiomas-e-regiao-do-servidor.md)), "adulto" deixa de
ter um só significado: a maioridade e as regras de verificação etária dependem da jurisdição da pessoa (lacuna J-05
em [data-map.md](../../privacy/data-map.md#jurisdições-a-confirmar)). O fluxo desta decisão não muda: o Duora não
calcula idade, só recebe do fornecedor um resultado `VerifiedAdult`, `NotAdult` ou `Inconclusive` (`AgeCheckOutcome`; o código não tem idade
mínima nem data de nascimento). O que fica em aberto é o critério que a política versionada passa ao fornecedor (idade
mínima por país ou subdivisão) e se um fornecedor cobre todos os países do lançamento. A decisão do dono de não fazer
análise jurídica do ECA Digital (adendo de 2026-10-16) continua valendo para o Brasil.

## Adendo (2026-10-19): rate limit e teto de tentativas em API-P05

Regra do dono (2026-10-19): toda operação disparada pela pessoa que custa dinheiro tem rate limit por conta e por
IP, teto de tentativas e `429` com `Retry-After`. API-P05 dispara uma verificação paga no fornecedor, e a
idempotência da decisão 6 só limita as solicitações **abertas**: depois de uma abandonada (`RequestLifetime`) ou de
uma tarefa `Failed`, cada novo pedido é outra verificação paga, e várias contas atrás do mesmo IP não têm limite
nenhum. Este adendo substitui a regra "Rate limit de API-P05 e do webhook: aceito sem limite próprio por ora" na
parte de API-P05; o webhook continua como estava (não é disparado pela pessoa, e a assinatura é conferida antes de
qualquer escrita).

- **Rate limit por conta e por IP** em API-P05, nas duas partições ao mesmo tempo (o padrão de
  `PhotoUploadRateLimiting`), com `429`, `Retry-After` e corpo `application/problem+json` sem dado da conta.
  Valor inicial: 10 por hora em cada partição, a medir no piloto e versionado (spec 11.5). Pedido repetido que
  devolve a solicitação aberta também conta, porque é a mesma rota.
- **Teto de tentativas por pessoa no banco** (spec 11.5: quota de abuso financeiro fica no banco, não no limitador
  em memória): no máximo 3 solicitações **criadas** por conta numa janela móvel de 7 dias (decisão do dono de
  2026-10-19), contadas em
  `identity.age_verifications` sob o lock da linha da conta (`SELECT ... FOR UPDATE` em `identity.users`), na
  mesma transação que cria a solicitação; nunca contar e depois inserir sem lock. Acima do teto, API-P05 responde
  `429` com `Retry-After` até a solicitação mais antiga sair da janela, sem criar linha nem evento na outbox.
  Devolver a solicitação aberta não conta no teto.
- O teto do worker (decisão 1: timeout, backoff e teto de tentativas por chamada ao fornecedor) continua valendo e
  não substitui os dois acima: um limita o retry de uma solicitação; estes limitam quantas solicitações pagas a
  pessoa pode abrir.

Compliance (a criar com a implementação; outro PR, código já mergeado não muda aqui):
`AgeVerificationRequestIsRateLimitedPerAccount` e `AgeVerificationRequestIsRateLimitedPerIp` (N+1 → `429` com
`Retry-After`, e a contagem por IP vale trocando de conta), `AgeVerificationRequestsAboveTheWeeklyCapAreRefused`
(teto + 1 → `429`, banco e outbox inalterados), `ReturningTheOpenRequestDoesNotConsumeTheCap` e
`ConcurrentRequestsAtTheCapCreateOnlyOne` (duas chamadas paralelas com uma vaga → uma solicitação, prova
determinística do lock, sem `sleep`).
