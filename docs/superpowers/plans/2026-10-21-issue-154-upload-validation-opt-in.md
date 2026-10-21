# Migração opt-in de validação do upload — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Oferecer códigos estáveis nas duas recusas existentes de API-M01 somente por adesão explícita,
preservando o legado padrão e todo o restante do comportamento do upload.

**Architecture:** Seleção por requisição em um helper do Web, chamado apenas nas duas recusas de
`PhotoEndpoints.RefuseHeaders`. O helper produz o mesmo ValidationProblem, combinando Vary e confirmando
codes-v1 somente quando selecionado. Core, Infrastructure, policies, configuração e SPA não mudam.

**Tech Stack:** .NET 10 / ASP.NET Core, PostgreSQL 18 / Testcontainers, xUnit v3, OpenAPI 3.1,
openapi-typescript. Nenhuma nova dependência de produto ou pacote de teste.

**Spec:** [ADR-0034](../../architecture/adr/0034-codigos-de-validacao-do-upload.md),
[#154](https://github.com/BipoXDXD/duora/issues/154). Base: `41b505955d629c6d058e7f592b0037c380798b53`.
Autorização explícita do responsável nesta conversa em 21/10/2026: implementar esta migração em PR separado,
conforme o ADR, preservando o formato legado, limites, segurança e ativação. Execução nesta sessão.

## Global Constraints

- API-M01, INV-06/INV-12 e T-06/T-13/T-16; sem deploy ou acesso a produção.
- Legado padrão; somente um valor exato `Duora-Validation-Format: codes-v1` após normalização HTTP de OWS.
- Valor sensível a caixa; nome insensível. Ausência, vazio, desconhecido, duplicatas e lista mantêm legado.
- Somente `errors.body = ["PHOTO_BODY_EMPTY"]` ou `errors["Idempotency-Key"] = ["IDEMPOTENCY_KEY_INVALID"]`.
- HTTP/status 400, VALIDATION_FAILED, type/título inglês, ausência de detail, traceId e arrays unitários iguais.
- Confirmação apenas nas duas recusas codificadas; Vary combinado nos dois formatos, preservando `*`.
- Content-Type → comprimento → chave; streaming, 5 MiB, JSON 32 KiB e chave ASCII 1–64 intactos.
- Nenhuma alteração de autenticação, CSRF, CORS, rate limit, conta, persistência ou configuração de upload.
- Sem fluxo/adesão/catálogos novos na SPA: inventário #152 confirma ausência de consumidor de POST de foto.
- OpenAPI/tipos gerados por build e api:generate, nunca editados à mão. Nove checks obrigatórios sem exceções.

## Review Focus

1. Duplicatas/listas ou espaços Unicode não podem selecionar codes-v1; OWS HTTP deve ser tratado sem ampliar domínio.
2. Vary preexistente, caixa diferente ou `*` não pode ser perdido ou duplicado; outras respostas não confirmam formato.
3. Requisições concorrentes/alternadas e repetição idempotente não podem compartilhar seleção nem criar efeitos extras.
4. Limites e barreiras anteriores devem prevalecer; novos códigos não podem substituir UPLOAD_INCOMPLETE/IDEMPOTENCY_KEY_REUSED.
5. Um consumidor aderente sintético contra servidor antigo deve usar fallback local sem repetir POST; nenhuma adesão real é presumida.

## Task 1: Protocolo opt-in, contrato e prova de compatibilidade

**Files:**
- Create: `backend/src/Duora.Web/Media/PhotoValidationProblems.cs` — seleção e cabeçalhos das duas recusas.
- Modify: `backend/src/Duora.Web/Media/PhotoEndpoints.cs` — duas chamadas e metadados do POST.
- Create: `backend/tests/Duora.IntegrationTests/Media/PhotoValidationCompatibilityTests.cs` — matriz HTTP e efeitos.
- Modify: `backend/tests/Duora.IntegrationTests/Media/PhotoUploadTests.cs` — fixar literais baseline sem retirar asserts.
- Modify: `backend/tests/Duora.IntegrationTests/Media/KestrelPhotoUploadTests.cs` — partial para reutilizar TLS/sessão;
  caso anterior de seletor ignorado vira expectativa codificada autorizada, demais casos preservados.
- Create: `backend/tests/Duora.IntegrationTests/Media/KestrelPhotoValidationTests.cs` — transporte real e limites.
- Create: `backend/tests/Duora.IntegrationTests/Media/KestrelPhotoProxyTests.cs` — configuração Caddy e transporte same-origin.
- Create: `frontend/src/shared/api/photo-validation-compatibility.test.tsx` — cliente/parser/alerta reais com transporte simulado.
- Modify: `backend/tests/Duora.IntegrationTests/Http/ProblemOpenApiTests.cs` — parâmetro opcional e headers só no POST/400.
- Generate: `docs/api/openapi.json`, `frontend/src/shared/api/schema.d.ts`.
- Document: ADR-0034/índice (decisão agora autorizada), docs/api/README.md, docs/testing/problem-details.md,
  novo relatório `docs/testing/reports/2026-10-21-issue-154-opt-in.md`.

**Interfaces:**
- Consumes: HttpRequest/HttpContext da validação atual e `ApiProblems.Validation(IDictionary<string,string[]>)`.
- Produces: `PhotoValidationProblems.EmptyBody(HttpRequest)` e `InvalidIdempotencyKey(HttpRequest)` retornam
  `ValidationProblem`; constantes `HeaderName` e `CodesV1` para metadados, sem estado mutável.
- Consumers de teste conservam resposta bruta + confirmação; nenhuma interface pública da SPA é ampliada.

- [x] **Step 1: Baseline.** Restore/build travados e runner de integração para PhotoUploadTests e
  KestrelPhotoUploadTests: 44 passaram, 0 falhas/ignorados. Doctor passou. Árvore e branch isolada conferidas.

- [x] **Step 2: Escrever e executar RED sem produto.** Fixar campos/literais legados por HTTP. Nova teoria
  cobre dois campos, PT-BR/EN-US, opt-in e ausência; seletor inválido/vazio/desconhecido/CODES-V1/lista/duplicata;
  testes de Vary com middleware de teste que pré-carrega Accept-Encoding, nome existente e `*`.
  Cada recusa verifica `ProblemAssert`, type exato, um campo/array unitário, cache/correlação, ausência de
  Retry-After, uploads/guards/objetos/eventos por conta. Os casos codes-v1 e Vary devem falhar na base.

```csharp
using var response = await browser.PostContentAsync(MediaScenario.PhotosPath, content,
    new MutationHeaders(Extra: new Dictionary<string, string>
    {
        ["Idempotency-Key"] = "bad key",
        ["Duora-Validation-Format"] = "codes-v1",
    }));
var body = await ProblemAssert.IsProblemAsync(response, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
Assert.Equal("IDEMPOTENCY_KEY_INVALID", (string?)body["errors"]!["Idempotency-Key"]![0]);
Assert.Equal("codes-v1", Assert.Single(response.Headers.GetValues("Duora-Validation-Format")));
```

  Run: build de integração e runner `-class Duora.IntegrationTests.Media.PhotoValidationCompatibilityTests`;
  runner `-class Duora.IntegrationTests.Http.ProblemOpenApiTests`. Logs e XML em scratch desta tarefa.
  Expected: falhas de valor/headers/metadados ausentes, sem erro de configuração/compilação.

- [x] **Step 3: Implementar mínimo.** Em `RefuseHeaders`, trocar somente as duas construções de ValidationProblem
  pelas chamadas do helper, mantendo a posição de cada return. Helper seleciona uma string por campo e monta
  os headers antes de retornar o mesmo `ApiProblems.Validation`. Núcleo da seleção e composição:

```csharp
var codes = request.Headers.TryGetValue(HeaderName, out var values)
    && values.Count == 1
    && string.Equals(values[0]?.Trim(' ', '\t'), CodesV1, StringComparison.Ordinal);
var response = request.HttpContext.Response;
if (!response.Headers.GetCommaSeparatedValues("Vary").Any(value => value == "*"
    || string.Equals(value, HeaderName, StringComparison.OrdinalIgnoreCase)))
{
    response.Headers.AppendCommaSeparatedValues("Vary", HeaderName);
}
if (codes) response.Headers[HeaderName] = CodesV1;
return ApiProblems.Validation(new Dictionary<string, string[]> { [field] = [codes ? code : legacy] });
```

  Documentar parâmetro opcional string (sem enum que rejeite desconhecidos) e headers de resposta somente
  em 400, com descrição das duas recusas. Preservar schemas globais, requireds e domínio da chave.

- [x] **Step 4: GREEN e geração.** Build da solução, api:generate e os mesmos runners/expectativas RED.
  Expected: novos códigos só no opt-in; legado exato continua. Diff de contrato apenas metadados/headers
  do POST e descrições pertinentes; schemas de erro permanecem os da #153.

- [x] **Step 5: Compatibilidade e transporte.** Repetir matriz seletor/idioma/campo em Kestrel TLS, com
  duplicatas reais e OWS. Cobrir precedência, 411/413/415, chaves 1/64 válidas e inválidas 65/espaço/barra/Unicode,
  stream interrompido, replay alternando formato, conflito com outros bytes, concorrência, CSRF/sessão/conta,
  upload desativado e rate limit sem confirmação enganosa. Preservar teste de corpo lento/teto de outra rota.
  Exercitar Caddy de teste como intermediário same-origin, usando imagem já fixada e ponte de portas
  Testcontainers para o Kestrel loopback; sem alterar configuração/infra de produto. Se infraestrutura local
  falhar, diagnosticar e registrar a causa sem dispensar a prova no CI.

  Testar cliente antigo que não manda seletor com servidor novo; sonda de cliente com respostas simuladas
  com/sem confirmação; servidor antigo usando fixture histórica do diagnóstico e parser/alerta reais.
  O ApiProblemError atual não transporta a confirmação: provar fallback/retry, sem alegar adoção ou leitura
  de códigos por um adaptador futuro.
  Fallback PT-BR/EN-US deve continuar local, sem retry ou nova página. Não implementar adaptador de produção
  para fluxo inexistente. Regenerar tipos e compilar o frontend.

- [ ] **Step 6: Documentação e verificação.** Adendo datado no ADR registra a autorização atual, inventário #152
  e fidelidade #153 atendidos; textos e matriz históricos ficam identificados. Relatório enumera requisitos,
  testes/commands/resultados, limites de clientes/adoção e rollback sem dados/idempotência/reenvio.
  Executar restore/build/format/backend completo; frontend typecheck/lint/format/test/build/E2E; oasdiff,
  diff dos gerados, scanners/gates de CI. Não presumir merge ou deploy deste PR.

- [ ] **Step 7: Commit e revisão.** Commit da fatia, revisão independente do diff inteiro/Review Focus e
  correções necessárias via RED→GREEN. Abrir PR separado, anexar ao chat e vincular #154; aguardar os nove
  checks no SHA final. Preservar legado padrão sem data de retirada.

## Pre-flight e decisões de escopo

Uma única tarefa cobre o protocolo e seus metadados, que dependem do mesmo seletor/confirmação. Nenhuma
interface compartilhada entre tarefas. O desenho já foi especificado no ADR-0034 e agora foi explicitamente
autorizado pelo responsável; este plano organiza sua execução, sem nova escolha de produto ou autorização.
Não há consumidor aderente de upload na SPA ou externo nesta fase; testes não serão chamados de adoção real.
