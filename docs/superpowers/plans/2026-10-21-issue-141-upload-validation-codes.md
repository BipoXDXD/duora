# Issue 141 — plano de migração dos códigos de validação de upload

**Objetivo:** preparar uma migração compatível dos dois textos legados de `errors` para códigos estáveis, sem executar a migração nesta entrega.
**Arquitetura proposta:** adesão explícita por requisição ao formato `codes-v1`, preservando integralmente o formato legado como padrão. HTTP 400, `VALIDATION_FAILED`, as chaves de `errors` e seus arrays permanecem.
**Tecnologias:** ASP.NET Core, contratos OpenAPI, TypeScript/Zod, PostgreSQL real nos testes de efeitos.
**Especificação:** issue #141; inventário em [problem-details.md](../../testing/problem-details.md); base inspecionada `a4711d6ec2cdd11dda892ed5ab41f66bb2009edc`.

## Limite desta entrega

Este arquivo é uma proposta técnica revisável e um plano de execução futura. Não altera endpoint, schema executável, texto de resposta, upload, tipo/tamanho permitido, idempotência, segurança ou configuração. A autorização posterior para implementar deve citar este plano. O merge da documentação foi autorizado pelo responsável; não equivale à autorização da migração ou de deploy.

## Inventário confirmado

| Produtor | Condição e precedência | Resposta atual |
|---|---|---|
| `backend/src/Duora.Web/Media/PhotoEndpoints.cs`, `RefuseHeaders` | `PhotoLimits.CheckContentLength` classifica tamanho como `Empty`, após validar Content-Type e antes de validar Idempotency-Key | 400 / VALIDATION_FAILED; `errors.body = ["A foto está vazia."]` |
| Mesmo método | Tamanho aceito, `IdempotencyKey.TryParse` recusa chave ausente/inválida | 400 / VALIDATION_FAILED; `errors["Idempotency-Key"] = ["Obrigatório: de 1 a 64 caracteres entre letras, dígitos, '-' e '_'."]` |

Não confundir corpo de comprimento declarado zero com envio interrompido: este último tem `UPLOAD_INCOMPLETE` e fica fora da migração. Tipo incompatível (415), tamanho ausente (411), tamanho excessivo (413), rate limit, CSRF e autorização conservam a precedência existente. Headers são recusados antes de ler/armazenar a foto. O header de idempotência continua com a semântica definida em `Duora.Core.Media.IdempotencyKey`, em `PhotoUploadRequest.cs`, tamanho máximo 64 e alfabeto atual; não propor alteração desse domínio.

Consumidores conhecidos:

- `ApiProblems.Validation` e seu normalizador em `ApiProblems.cs` compõem/normalizam o Problem Details; manter título público inglês, ausência de detail e correlação segura.
- `docs/api/openapi.json` e `frontend/src/shared/api/schema.d.ts` descrevem 400/ValidationProblemDetails, com `errors` por campo. Strings livres não tornam a troca semanticamente compatível por si só.
- `frontend/src/shared/api/problem-details.ts`, `api-errors.ts` e `request-failure.ts` transportam/classificam a falha. `RequestFailureAlert` apresenta catálogo local/fallback; não exibe os dois textos de upload.
- Não há fluxo de upload de foto na SPA atual: busca de `/me/photos` encontra apenas um teste genérico do cliente de API e os tipos gerados. Não criar uma tela para esta migração.
- `backend/tests/Duora.IntegrationTests/Media/PhotoUploadTests.cs`: `AnEmptyBodyIsRefused` e a teoria de chave ausente/inválida comprovam 400/VALIDATION_FAILED e ausência de uploads/objetos. Ainda não fixam ambos os textos exatos por campo; esses asserts deverão ser acrescentados como baseline.
- `KestrelPhotoUploadTests` cobre transporte HTTP real e caminhos de upload; repetir os casos que dependem de Content-Length no host Kestrel.
- Clientes externos, scripts privados e consumidores fora do repositório são **desconhecidos**. Não há evidência que permita descontinuar o legado.

Busca de execução futura: `rg -n 'A foto está vazia|Obrigatório: de 1 a 64|/me/photos|Idempotency-Key' backend frontend docs/api`, excluindo bundles e dependências.

## Contrato proposto

| Campo | Código `codes-v1` | Significado |
|---|---|---|
| `errors.body[0]` | `PHOTO_BODY_EMPTY` | Comprimento declarado classificado como vazio pela política atual |
| `errors["Idempotency-Key"][0]` | `IDEMPOTENCY_KEY_INVALID` | Chave obrigatória ausente ou fora do domínio atual |

Exemplos conceituais (campos comuns, como type/title/traceId, continuam os atuais):

```json
{ "status": 400, "code": "VALIDATION_FAILED", "errors": { "body": ["PHOTO_BODY_EMPTY"] } }
```

```json
{ "status": 400, "code": "VALIDATION_FAILED", "errors": { "Idempotency-Key": ["IDEMPOTENCY_KEY_INVALID"] } }
```

Os códigos são identificadores de protocolo, sem tradução, sem valor de entrada, caminho local, SQL ou segredo. Código desconhecido continua exigindo fallback local seguro; não renderizar strings de errors/title/detail.

## Decisão técnica do plano: transição por adesão explícita

Propor header de requisição **`Duora-Validation-Format: codes-v1`**, apenas no POST de foto. Somente um valor exato `codes-v1` seleciona o novo formato. Ausente, desconhecido, vazio ou múltiplo mantém o legado; não introduzir erro novo para um cliente que não aderiu. O servidor confirma `Duora-Validation-Format: codes-v1` na resposta negociada. Documentar o header como opcional no OpenAPI.

A resposta selecionada varia pelo header: incluir/combinar `Vary: Duora-Validation-Format` nos dois formatos, preservando outros valores e a política atual de cache. Não sobrescrever cabeçalhos existentes. Esse seletor nunca deve alterar CSRF, rate limit, autenticação, conteúdo aceito, idempotência ou criação de efeito. Não aceitar decisão de formato do corpo da foto. Não ampliar CORS para introduzir o formato; clientes de outra origem exigem revisão própria se existirem.

A alternativa de substituir as strings imediatamente foi rejeitada por possível dependência semântica externa. Acrescentar códigos ao mesmo array também foi rejeitado: muda cardinalidade e pode ser exibido por clientes antigos. Uma extensão paralela em todas as respostas pode quebrar validadores estritos de campos desconhecidos. Um endpoint/versionamento completo seria mais custoso para dois valores e permanece alternativa caso o inventário posterior demonstre que negociação por header é inadequada.

Esta escolha é uma proposta de execução, sem header implementado agora. O PR futuro deverá verificar convenções de headers do projeto e compatibilidade do gerador de clientes antes de codificar; se incompatível, revisar este contrato no mesmo PR, sem alterar silenciosamente o padrão.

## Sequência futura, arquivos e critérios de teste

### 1. Fixar o baseline legado antes de implementar

Arquivos: `PhotoUploadTests.cs`, `KestrelPhotoUploadTests.cs` e `MediaScenario.cs` apenas se precisar encaminhar o header opcional.

- [ ] Assertar corpo vazio com chave válida: HTTP 400, VALIDATION_FAILED, `errors.body` contendo exatamente o texto legado; nenhum upload, evento/entrega de processamento ou objeto criado.
- [ ] Assertar chave ausente, vazia, tamanho 65 e caractere inválido com bytes/tipo/tamanho válidos: mesma estrutura e texto legado exato em `errors["Idempotency-Key"]`, sem efeitos.
- [ ] Assertar erro composto: corpo vazio + chave inválida continua retornando `errors.body`; Content-Type incompatível continua 415 antes desses dois casos.
- [ ] Validar título inglês/ausência de detail/traceId via `ProblemAssert`, Content-Type problem+json e cabeçalhos atuais pertinentes. Não fixar um traceId ou acrescentar Retry-After a estes 400.
- [ ] Executar baseline no PostgreSQL real. Não usar SQLite/InMemory para efeitos transacionais.

### 2. Introduzir formato opt-in e provar isolamento

Arquivos: `PhotoEndpoints.cs`, helper local de seleção somente se o tamanho justificar; testes acima; `docs/api/README.md` para registrar semântica e eventual quebra.

- [ ] Escrever casos RED com o novo header: os dois códigos exatos, arrays de um elemento, chaves preservadas e código principal/status inalterados.
- [ ] Repetir sem header, com valor desconhecido, vazio e duplicado: legado exato. Alternar formatos na mesma conta para demonstrar ausência de estado global e contaminação entre requisições.
- [ ] Acrescentar requisições concorrentes com seletores distintos; verificar ausência de efeito e resposta independente, sem enfraquecer limites.
- [ ] Implementar apenas a escolha dos dois valores no ponto de recusa; não alterar RequestValidation, ApiProblems, domínio da chave nem upload bem-sucedido.
- [ ] Verificar confirmação do formato e combinação de Vary; cabeçalhos de correlação/cache/segurança existentes permanecem válidos. Headers de falha de autenticação/rate limit não dependem desse seletor.
- [ ] Testar negociação como conta não autenticada, CSRF inválido e limite esgotado: mesma recusa anterior, sem bypass nem detalhe privado.

### 3. Contrato e apresentação

Arquivos futuros: OpenAPI, schema.d.ts, testes de parser/classificador/alerta e, se houver consumidor de upload até lá, sua página/teste.

- [ ] Regenerar OpenAPI e tipos, comparar com a base e revisar também a semântica. O gate de schema não substitui análise de consumidores; qualquer quebra requer aprovação própria.
- [ ] Exercitar em pt-BR/en-US: códigos conhecidos selecionam mensagens locais previstas; código principal desconhecido e campos desconhecidos usam fallback seguro. Se não existir formulário de upload, provar em parser → classificador → RequestFailureAlert e registrar essa limitação.
- [ ] Injetar canário HTML/texto em title/detail/errors, inclusive constructor/__proto__/toString; exigir ausência no DOM e de execução, além de html lang e axe nos caminhos reais de interface que existirem.
- [ ] Não passar a renderizar errors só porque contêm códigos. Uma futura UX por campo deve definir allowlist local e mensagens antes de adicionar esse comportamento.

### 4. Publicação, adoção e retirada

- [ ] Restore travado, build/format/backend PostgreSQL, frontend tipos/lint/format/test/build e todos os nove gates atuais aprovados no SHA revisado. Nenhum deploy é autorizado pelo plano.
- [ ] Publicar primeiro servidor capaz de responder aos dois formatos; aderir nos clientes conhecidos em entrega posterior. Sem consumidor SPA, não há adesão artificial a implementar.
- [ ] Registrar clientes/versões suportados, responsáveis, confirmação de adoção e janela de suporte aprovada antes de propor mudança do padrão. Métricas agregadas por formato podem ser propostas, sem corpo, IP, e-mail ou chave bruta.
- [ ] Não há prazo de retirada presumido: com consumidores desconhecidos, manter o legado por padrão. Retirada ou troca de default é uma mudança semântica separada, sujeita a aprovação explícita; ausência de tráfego não prova ausência de cliente.

## Reversão

Enquanto o padrão for legado, suspender adesão dos clientes e retornar ao servidor anterior restaura a mesma resposta padrão, sem migração de dados. Cliente novo deve tratar ausência da confirmação de formato como legado e cair no fallback seguro, nunca exibir o valor bruto. A reversão não pode mudar chaves de idempotência nem repetir uploads aceitos para obter outro formato. Testar servidor novo/cliente antigo e servidor antigo/cliente aderente antes da release.

## Comandos de validação da implementação futura

```sh
dotnet test --project backend/tests/Duora.IntegrationTests -- --filter-class '*PhotoUploadTests'
dotnet build backend/Duora.slnx
pnpm --dir frontend api:generate
pnpm --dir frontend typecheck
pnpm --dir frontend lint
pnpm --dir frontend format:check
pnpm --dir frontend test --run --maxWorkers=2
pnpm --dir frontend build
```

Confirmar a sintaxe de filtros do runner no momento da execução; não omitir casos ao interpretar esse exemplo. O CI completo continua obrigatório.

## Revisão do plano e limites

Todos os seis critérios da #141 estão mapeados: inventário; códigos; alternativas/transição/retirada/reversão; testes de contrato; apresentação bilíngue/fallback; separação de autorização. Os principais riscos são consumidores desconhecidos, negociação/cache, precedência de erros, geração de cliente e rollback sem confirmação do formato. Cada risco tem casos acima. Este documento não comprova transporte/fornecedor real, homologação ou execução dos testes futuros.
