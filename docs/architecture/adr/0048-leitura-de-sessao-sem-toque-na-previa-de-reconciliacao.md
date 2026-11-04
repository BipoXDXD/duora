# ADR-0048: Leitura de sessão sem toque na prévia da reconciliação

- **Status:** Proposed (04/11/2026). Decisão de escopo do dono para a fatia 2 da #321; o aceite formal vem com a
  revisão humana do PR, exigida por tocar autenticação e autorização.
- **Origem:** [#321](https://github.com/BipoXDXD/duora/issues/321), proposta de reconciliação (seções 6 e 7);
  exceção pontual ao item 2 da Decisão do [ADR-0019](0019-implementacao-identidade-sessao-outbox.md).
- **Revisar quando:** outra rota pedir leitura sem toque, a sessão administrativa mudar de janela, ou a prévia
  ganhar envelope assinado, run ou aplicação (fatias seguintes da #321).

## Contexto

O ADR-0019 decide que toda requisição autenticada executa um `UPDATE ... RETURNING` em `identity.login_sessions`,
gravando `last_seen_at` e deslizando `idle_expires_at`. A prévia da reconciliação de inscrições legadas
(API-ADM25) promete nenhuma mutação lógica no banco durante a requisição, inclusive na sessão. Um endpoint que só
faz SELECT depois desse middleware ainda escreve; a promessa seria falsa.

Alternativas consideradas:

- **Manter o toque e documentar a exceção na promessa.** Simples, mas a prévia deixaria de ser "sem escrita" e a
  revisão teria de aceitar uma escrita implícita a cada leitura sensível. Descartada pelo dono.
- **Autenticar a prévia por outro esquema (token, CLI com IDs).** Cria uma segunda identidade administrativa e um
  caminho de credencial novo; a proposta rejeita CLI que receba IDs como sessão. Descartada.
- **Ler a sessão sem toque só nessa rota, com o mesmo critério.** Escolhida.

## Decisão

Vamos acrescentar `LoginSessions.ReadValidAsync`, que aplica exatamente o critério de validade do toque (chave,
revogação, expiração ociosa e absoluta, security stamp atual, senha temporária vencida) e devolve o mesmo estado
(política, provas de reautenticação/MFA/TOTP, limite absoluto, papel privilegiado lido agora, senha temporária e
suspensão), sem UPDATE e dentro de uma transação `READ ONLY`. O critério e as colunas são um só texto SQL usado
pelos dois caminhos, para a leitura nunca aceitar o que o toque recusaria.

O `PostgresTicketStore` usa esse caminho **somente** quando a rota roteada carrega o metadado
`ReadOnlySessionEndpoint` (`WithReadOnlySession()`). Nessa requisição ele recusa gravar (`StoreAsync`) e renovar
(`RenewAsync`) com exceção, e não revoga (`RemoveAsync` é no-op: o handler de cookie só remove ali o ticket que ele
mesmo julgou expirado, e a sessão já não autentica pelo banco). O cookie já não tem sliding expiration
(ADR-0005), então não há `Set-Cookie`. Falha de leitura propaga e a requisição não é autenticada.

A lista de rotas com o metadado é fechada: hoje só
`GET /api/v1/admin/registration-reconciliations/preview`. Todas as outras rotas continuam tocando a sessão.

Porquê técnico: mantém a identidade e as verificações vivas da sessão existente, sem esquema novo de credencial,
e torna verificável a promessa "sem escrita". Porquê de negócio: o dono quer diagnosticar a #321 sem efeito
colateral antes de autorizar qualquer aplicação.

## Consequências

- Positivas: a prévia é verificável como sem mutação lógica; o critério de sessão é um só para os dois caminhos.
- Negativas: a prévia não conta como atividade; a sessão administrativa (15 min de inatividade) pode expirar
  durante uma análise longa feita só de prévias. O moderador precisa de outra rota (ou novo login) para manter a
  sessão viva, e o TOTP recente (1 h) continua exigido.
- Trade-off: uma exceção nomeada ao ADR-0019 em troca de uma garantia testável; o custo de manutenção é o
  metadado e a lista fechada.

## Segurança e operação

**Onde a garantia "sem escrita" mora.** A policy `account-moderator` (`AccountModeratorHandler`) consulta o papel
ao vivo antes do handler com um SELECT em autocommit, fora de transação `READ ONLY`; a policy é global e não muda
aqui. Essa checagem prévia só decide se a requisição prossegue. A garantia vem de duas outras peças: a leitura da
sessão (`ReadValidAsync`) e a revalidação da moderação dentro do snapshot `REPEATABLE READ READ ONLY` do leitor,
em que o PostgreSQL recusa qualquer escrita, e o fingerprint de todas as tabelas e sequências nos testes, que
cobre também o SELECT em autocommit.

**Limite de uso.** 6 prévias por minuto por conta moderadora (`RateLimiting:ReconciliationPreviewPerModerator`),
contadas depois da autorização e antes do cursor; acima disso, `429 RATE_LIMITED` com `Retry-After`, sem ler o
banco. O limite fica em memória da instância, como os demais (ADR-0009). **Limitação registrada:** a regra geral
pede estado compartilhado entre réplicas; hoje há uma só instância web, e uma segunda réplica teria contagem
própria até o limite ir para estado compartilhado. Não é `RequireRateLimiting` porque o middleware de rate limit
roda antes da autenticação e não enxerga a conta.

**Timeout.** O snapshot tem `statement_timeout` de 10 s. Estourado (57014 sem cancelamento do cliente), a rota
responde `503 TEMPORARILY_UNAVAILABLE` com `Retry-After` de 30 s e corpo só de Problem Details. O mapeamento é da
prévia; `DatabaseUnavailability` não passa a tratar 57014 nas outras rotas.

Nada relaxa nas outras rotas. Na rota marcada valem sessão administrativa, MFA, papel `moderator` lido ao vivo
pela policy, TOTP recente pelo step-up existente e prova de contexto privado (CSRF em leitura privada). O leitor
revalida a sessão de moderação dentro do próprio snapshot `REPEATABLE READ READ ONLY`. `last_seen_at` deixa de
refletir uso da prévia; a lista de sessões (API-A09) mostra o último uso das outras rotas. Rollback: remover o
metadado da rota (volta a tocar) ou a rota inteira; nenhum dado é migrado.

## Compliance

- `ReadOnlySessionTests`: equivalência de `ReadValidAsync` e `TouchAsync` em sessão válida e em cada invalidação,
  ausência de escrita, recusa de gravar/renovar, no-op de revogação e lista fechada de rotas com o metadado.
- `RegistrationReconciliationPreviewEffectsTests`: fingerprint de todas as tabelas dos schemas da aplicação e das
  sequências, cookies e `Set-Cookie` iguais antes/depois; a prévia não estende a inatividade; outras rotas
  continuam deslizando a sessão.
- `RegistrationReconciliationPreviewHttpTests`: 401, 403 por papel, step-up, CSRF e 400 de cursor, com corpo de
  erro sem dados.
- `RegistrationReconciliationPreviewEffectsTests`: N+1 prévias → 429 com `Retry-After` e fingerprint igual, outro
  moderador não afetado; timeout forçado (lock `ACCESS EXCLUSIVE` e teto menor injetado só no teste) → 503 sem
  dados e fingerprint igual.
