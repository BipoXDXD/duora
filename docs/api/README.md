# Contrato HTTP (OpenAPI)

O contrato da API é gerado do código e versionado aqui ([ADR-0018](../architecture/adr/0018-contrato-openapi-code-first.md),
spec 2.1, 8.1 e 17.1). Ninguém edita `openapi.json` nem os tipos gerados à mão: o CI regenera os dois e falha se
divergirem do commit.

| Artefato | Gerado por | Fonte |
|---|---|---|
| `docs/api/openapi.json` | build do `Duora.Web` (`Microsoft.Extensions.ApiDescription.Server`) | endpoints e DTOs do backend |
| `frontend/src/shared/api/schema.d.ts` | `pnpm api:generate` (`openapi-typescript`) | `docs/api/openapi.json` |

O documento sai em **OpenAPI 3.1** (padrão do ASP.NET Core 10, fixado em `Duora.Web.csproj` com
`--openapi-version OpenApi3_1`). É a versão que o openapi-typescript 7 e o oasdiff leem; trocar exige conferir os
dois antes.

## Fluxo ao mudar um endpoint ou DTO

```sh
dotnet build backend/Duora.slnx          # regenera docs/api/openapi.json
cd frontend && pnpm api:generate         # regenera src/shared/api/schema.d.ts
git add docs/api/openapi.json frontend/src/shared/api/schema.d.ts
```

Revise o diff do `openapi.json` no PR como revisaria código: ele é o contrato que a SPA consome.

O build só regenera o documento quando o `Duora.Web.dll` muda (o alvo `GenerateOpenApiDocuments` compara com o
cache em `obj/`). Se o arquivo estiver desatualizado sem mudança de código, rode `dotnet build --no-incremental`
no `Duora.Web`.

## Como a geração funciona

O pacote executa o `Program` do `Duora.Web` dentro do processo `GetDocument.Insider`, com um servidor falso que
não atende requisições, e grava o documento `v1` em `docs/api/openapi.json`. O startup roda de verdade, então a
validação de opções também roda: `BuildTimeOpenApi.cs` injeta, só nesse processo, uma connection string
placeholder que nunca é usada. Os serviços registrados são os mesmos de produção, e o documento corresponde ao
que a app expõe.

Se um código de startup precisar de I/O real (ler chave, abrir conexão), ele quebra a geração no build. Mantenha
esse trabalho fora do `Build` ou faça-o preguiçoso.

## Gate de compatibilidade (job `contract`)

1. Regenera os dois artefatos e roda `git diff --exit-code`; arquivo novo não versionado também reprova.
2. Em pull request, roda `oasdiff breaking --fail-on ERR` comparando o `openapi.json` do commit base do PR com
   o do PR. O changelog completo vai para o resumo do job.
3. Breaking change reprova o PR, salvo a saída de escape abaixo. Na `main` o passo 2 não roda: a decisão já foi
   tomada no PR.

O oasdiff roda pela imagem `tufin/oasdiff` fixada por digest em `.github/workflows/ci.yml`; versão e
atualização em [supply-chain.md](../security/supply-chain.md).

### Saída de escape: `breaking-change-approved`

Mudança incompatível exige nova versão da API ou transição documentada (spec 8.1). Quando ela é intencional:

1. descreva no PR o que quebra, quem consome (hoje só a SPA, que muda no mesmo PR) e o plano de transição;
2. o responsável técnico aplica o label `breaking-change-approved` no PR;
3. o CI reroda sozinho (o workflow reage a `labeled` e `unlabeled`) e o job passa com um aviso.

O label é lido do payload do evento por variável de ambiente, nunca interpolado no script. Só quem tem
permissão de triage ou write no repositório aplica labels, então o autor de um PR de fork não consegue se
autoaprovar. Remover o label volta a reprovar.

## O que o gate não cobre

- Mudança de semântica sem mudança de schema (um campo que passa a significar outra coisa). Isso é revisão.
- Compatibilidade entre releases implantadas lado a lado: fica com o manifesto da release (spec 17.2).
- Os tipos gerados não validam respostas em runtime (D-API-03); o cliente HTTP continua tratando o inesperado.

## Códigos de problema fora do OpenAPI

O documento gerado lista os status de cada rota, não o valor de `code` no corpo (`application/problem+json`). Os
códigos novos entram aqui e no contrato de rotas do [auth.md](../security/auth.md#contrato-de-rotas-api-a01-a-a16),
para a SPA tratá-los:

| Código | Status | Onde | Significado |
|---|---|---|---|
| `PASSWORD_CHANGE_REQUIRED` | `403` | toda rota com sessão, menos A16 | A conta entrou com a senha temporária do bootstrap administrativo e só pode trocá-la ([auth.md](../security/auth.md#bootstrap-administrativo-d4)) |
| `SAME_AS_TEMPORARY_PASSWORD` | `400` (`VALIDATION_FAILED`, em `errors.newPassword`) | A16 | A senha nova repete a temporária |

## Localização do perfil (expansão da issue #78)

API-P01/P02 acrescentam `countryCode` e `subdivisionCode`, opcionais e anuláveis, conservando `regionCode`.
No PATCH, enviar `countryCode` substitui a localização inteira (subdivisão omitida/null significa só país;
país null significa não informada). Omitir ambos preserva; não misturar com `regionCode` nem enviar subdivisão
sem o campo país. Erros por campo: `INVALID_LOCATION`, `COUNTRY_REQUIRED`, `CONFLICTING_LOCATION_FIELDS`,
todos 400/`VALIDATION_FAILED`, sem ecoar valores. ETag/If-Match e respostas 412/428 permanecem.
O [ADR-0030](../architecture/adr/0030-profile-country-subdivision.md) detalha transição, privacidade e rollback.

## Rotas Improváveis (issue #100)

`POST /admin/route-experience-versions` publica conteúdo PT-BR/EN-US com os mesmos IDs de paradas e opções,
dois conjuntos privados de pistas e áudio público introdutório nos dois idiomas. Exige administrador,
MFA recente e aprovação editorial. A versão publicada é imutável, inclusive seu tipo de experiência.
A cue `intro` usa o objetivo público como transcrição e o título como descrição acessível; o responsável
editorial verifica também a correspondência dos bytes de áudio. Não se publicam pistas no áudio compartilhado.

`GET /rooms/{roomId}` acrescenta `routesGame` opcional/nulo, preservando `game` de Constelação. Só um deles
é preenchido. O payload de Rotas contém apenas as pistas da própria pessoa após a introdução, a etapa,
a parada atual, opções permitidas e o roteiro compartilhado. IDs internos, sorteio e recibos não são
projetados. Textos e áudio respeitam o locale fixado de cada participante; versão e estrutura são comuns.

`POST /rooms/{roomId}/routes-actions` recebe `actionId`, `expectedVersion` decimal em string e uma ação:
`acknowledge` com `stage` (`Introduction` ou `Clues`), `choose-option` com `stepId` e `optionId`,
`confirm-route` ou `request-revision`. Campos específicos de outras ações devem ser nulos/ausentes.
O `actionId` do corpo identifica o reenvio, como na ação de Constelação. Repeti-lo com o mesmo corpo e
versão não duplica o efeito; outro corpo é inválido. Conflito de versão retorna `409`; a SPA relê a sala.
Autorização, elegibilidade, saída e bloqueios são revalidados antes de reconhecer um reenvio. A rota de
ações de uma experiência não pode operar a outra.

## Idioma do documento aceito (issue #103)

API-A02 (`POST /api/v1/auth/register`) aceita `acceptedTermsLocale` e `acceptedPrivacyLocale`,
independentes, opcionais e anuláveis. Valores exatos: `pt-BR`, `en-US` ou `null`; outros valores,
inclusive caixa diferente, string vazia ou espaços, retornam 400 `VALIDATION_FAILED` com `INVALID_FORMAT`
no campo correspondente, sem ecoar a entrada. O idioma acompanha a versão do respectivo documento.
Omitido/null significa **desconhecido**; nunca se usa `Accept-Language`, preferência atual da conta ou
locale da interface para completar o valor. Resposta 202 neutra, CSRF e limites permanecem.

Os campos são aditivos: clientes antigos continuam funcionando e produzem idioma desconhecido.
A SPA ainda contém apenas rótulos de versão; `REGISTRATION_DOCUMENTS` mantém locale null até a publicação
identificada dos documentos da issue #9. A futura apresentação deve fornecer versão e locale do mesmo
artefato e invalidar o aceite se trocar esse artefato; não basta copiar `useI18n().locale`.
O servidor registra a declaração explícita do cliente; isso não prova leitura nem aprovação jurídica.
Ver [evidência e validação](../testing/consent-document-locale.md).

## Declaração de 18 anos ou mais (issue #9, item 5)

API-A02 exige `confirmedAdult: true`, enviado pela caixa "Declaro ter 18 anos ou mais" do cadastro. Ausente,
`null` ou `false` retorna 400 `VALIDATION_FAILED` com `REQUIRED` em `confirmedAdult`; outro tipo retorna 400
`INVALID_REQUEST`. Em ambos os casos não se grava conta, aceite nem outbox. O campo é
obrigatório no contrato (`required`) e tipado `boolean | null`: o null explícito também chega à validação com código
estável. Para clientes antigos é uma mudança incompatível, aceitável porque a SPA e a API são publicadas juntas. Não há campo de idade nem de data de
nascimento: a declaração fica registrada pelo aceite dos Termos (versão `2026-10-29` em diante), com versão,
idioma e SHA-256. Isso não é verificação etária nem conformidade com o ECA Digital
([decisões de 29/10](../product/decisions-2026-10-29.md)).

## Novo aceite dos Termos (issue #296)

`GET /me` (API-A08) traz `currentTermsAccepted`: `true` quando a conta tem aceite da versão ativa dos Termos,
`false` quando o aceite é de uma versão anterior (ou não existe). Conta privilegiada (moderator, product_admin,
operator) não aceita os Termos de participante (D13) e recebe sempre `true`. O campo é aditivo e obrigatório na resposta.
**Não bloqueia nada:** com `false`, a SPA mostra um aviso dispensável que leva a `/account/terms`, e todas as
rotas continuam respondendo como antes (decisão do dono na #296).

`POST /me/terms-acceptances` (API-A17) grava o novo aceite pelo mesmo mecanismo do cadastro: corpo
`{acceptedTermsVersion, acceptedTermsLocale, acceptedTermsSha256, confirmedAdult}`, com o recibo exato dos Termos
ativos (o mesmo que `GET /legal-documents` devolve) e `confirmedAdult: true`. Responde `204`; repetir também
responde `204` e mantém o primeiro aceite (chave única conta + documento + versão). Os aceites anteriores continuam
em `identity.consent_records` como histórico. Declaração ausente, `null` ou `false` → `400` `VALIDATION_FAILED`
com `REQUIRED` em `confirmedAdult`; versão, idioma ou SHA-256 que não são os dos Termos ativos → `400`
`VALIDATION_FAILED`; campo desconhecido ou tipo errado → `400` `INVALID_REQUEST`. Nenhum desses casos grava. Não
há campo de conta: o aceite é sempre da conta da sessão. Exige sessão de participante (`401` sem sessão; conta privilegiada → `403` `FORBIDDEN`, sem gravar) e antiforgery (`403`); com a
publicação fechada, `503`. Mudança aditiva: `oasdiff breaking` não aponta breaking change.

## Fuso privado da conta (issue #81)

`GET /me/time-zone`: `{timeZone: string|null, version: string}` + ETag. `PUT /me/time-zone` exige
If-Match e campo `timeZone` obrigatório; null explícito remove. 428 sem precondição, 412 versão divergente,
400/VALIDATION_FAILED (`errors.timeZone: [UNSUPPORTED_TIME_ZONE]`) para ID não suportado. Autorização do
titular, CSRF e status Active/PendingEligibility; 503 em disputa de lock além do teto. Nenhum efeito em
matching, residência ou segurança de login. `GET /me/time-zone/options` é autenticado e devolve
`{timeZones: string[]}` (IANA do servidor mais UTC). `/me` acrescenta `preferredTimeZone` anulável;
cliente sem campo continua usando o navegador. Não persistir detecção sem confirmação explícita.

## Textos de Problem Details (issue #104)

`title` é texto público em inglês selecionado explicitamente pelo `code`; `detail` não expõe mensagens
internas. Status, códigos, `type`, `traceId`, `Retry-After` e valores de validação em `errors` são preservados.
A SPA usa exclusivamente mensagens locais por código/status e fallback local para código desconhecido,
inclusive nomes como `constructor` e `__proto__`. O parser mantém os campos do protocolo para diagnóstico,
mas eles não são texto de interface. O [inventário e os testes](../testing/problem-details.md) registram
produtores, títulos exatos e as duas mensagens legadas de upload em `errors`, preservadas por compatibilidade.

### Fidelidade dos schemas de erro (issue #153)

`ProblemDetails` e `HttpValidationProblemDetails` declaram `code` e `traceId` como strings opcionais no
OpenAPI e nos tipos TypeScript gerados. O transformer específico descreve as extensões já emitidas pelo
escritor de problemas; não modifica a serialização nem os payloads. A ausência continua permitida pelo
schema: esta correção não estabelece obrigatoriedade nova para todas as rotas. Quando presentes, os dois
campos devem ser strings, sem enumeração de códigos nem promessa adicional de formato da correlação.

Ambos continuam com `additionalProperties: false`; `errors` mantém mapa de arrays de strings. A política
JSON dos DTOs de entrada permanece `Disallow`. Os testes validam respostas reais de recusa do upload contra
o documento gerado no host e o artefato publicado, incluindo controles negativos de tipo e campo extra.
Ver [implementação e evidências](../testing/reports/2026-10-21-issue-153-fix.md).

As mensagens legadas e o comportamento de PT-BR/EN-US permanecem por padrão. A presença destes campos
no schema não seleciona o formato codes-v1 da negociação abaixo.

### Formato opt-in das duas validações de upload (#154)

Somente `POST /api/v1/me/photos`: um único valor exato `Duora-Validation-Format: codes-v1`, após a
normalização HTTP de espaços externos (SP/HTAB), seleciona os códigos. O nome do header é insensível a
caixa; o valor é sensível. Ausência, vazio, desconhecido, caixa diferente, duplicatas ou lista por vírgula
mantêm o legado, sem novo erro. `Accept-Language` não escolhe formato nem traduz valores da API.

| Campo | Padrão legado, em ambos os idiomas | Valor solicitado com codes-v1 |
|---|---|---|
| `errors.body[0]` | `A foto está vazia.` | `PHOTO_BODY_EMPTY` |
| `errors["Idempotency-Key"][0]` | `Obrigatório: de 1 a 64 caracteres entre letras, dígitos, '-' e '_'.` | `IDEMPOTENCY_KEY_INVALID` |

As duas recusas preservam HTTP/status 400, VALIDATION_FAILED, type, título inglês, detail ausente,
traceId e um único campo com array unitário. Combinam `Vary: Duora-Validation-Format` nos dois formatos,
preservando valores existentes e `*`, no-store/no-cache. Só no formato codificado confirmam
`Duora-Validation-Format: codes-v1`. Nunca confirmar em sucesso, outro erro ou legado; Retry-After não é
acrescentado aos dois 400. O parâmetro e os headers de 400 são opcionais no OpenAPI/tipos gerados; não
há enum novo em `errors` nem mudança nos schemas compartilhados.

Isso não ativa upload nem muda Content-Type → comprimento → chave, limites, domínio ASCII da chave,
idempotência, streaming, conta, CSRF, autenticação, CORS ou rate limit. UPLOAD_INCOMPLETE e
IDEMPOTENCY_KEY_REUSED conservam seus significados. A SPA continua sem fluxo de upload e sem adesão
automática; o alerta mantém fallback local nos dois idiomas. Um futuro consumidor precisa conservar e
exigir a confirmação exata antes de interpretar pares de campo/código, sem retry de POST para descobrir
capacidade. Sem confirmação (inclusive servidor antigo), usar fallback local seguro.

Ver [ADR-0034](../architecture/adr/0034-codigos-de-validacao-do-upload.md) e
[testes/compatibilidade/reversão](../testing/reports/2026-10-21-upload-validation-format.md). Adesão, ativação,
retirada/default e deploy continuam decisões e entregas próprias.

## Preferência regional privada — issue #108, etapas 1 e 2

`GET /api/v1/me/regional-preferences` retorna `mode`, `countryCodes` e `version`, com ETag e `no-store`.
Sem declaração, responde `pending`, lista vazia e versão/ETag `0`, sem criar linha nem inferir consentimento.
`PUT` substitui a preferência com `If-Match`: `countries` exige lista não vazia de países habilitados, sem
repetição; `unrestricted` exige lista vazia explícita. `pending`, null, ausência, códigos desconhecidos e campos
extras são recusados. O catálogo desta release continua somente BR; US não é aceito. Header ausente ou `*`
retorna 428; versão vencida ou ETag incompatível retorna 412; corpo inválido retorna 400 sem ecoar valores.

A autorização é do próprio titular; CSRF e as proteções existentes de conta permanecem. Gravação de modo,
países e versão é atômica sob a guarda compartilhada do usuário; a conta e a versão são relidas após o lock.
A versão regional é independente de perfil, locale e idiomas. Leitura de modo/lista/versão usa um único snapshot.
Nenhuma preferência privada é projetada em DTO de outra pessoa.

Esta fatia adiciona persistência e API; não ativa filtro no pareamento, UI de preferência, detecção por IP,
atualizador ou US. O tratamento de `pending` no pareamento virá com as etapas 3/4 do plano, não neste PR.
Responsáveis nominais e canal de alertas continuam pendentes. Aplicação da migração e implantação exigem
revisão/autorização próprias; reversão mantém schema e dados e exige migração forward revisada.

### Serialização da localização (issue #108, etapas 3–4)

PATCH `/api/v1/me/profile` usa a guarda compartilhada com o pareamento para countryCode/subdivisionCode
e regionCode, incluindo limpeza explícita. Omissão mantém localização e preferência/idiomas independentes.
Conta e versão são revalidadas depois da espera: 403 para restrição, 412 para versão vencida e 503 com
Retry-After para contenção/timeout, sem escrita parcial. Edição posterior à reserva não cancela a sala.
Não há endpoint de detecção nesta entrega. [Corte V3 ainda inativo](../operations/regional-matching.md).


## Estado integrado regional — etapa 10

As notas de etapas anteriores acima são históricas. A API de preferência, PATCH moderno/legado e interface
manual já existem. GET `/api/v1/me/region-detection/capability` responde `enabled: false`;
POST `/api/v1/me/region-detection`, sem corpo, responde `disabled` no registro executável atual.
Não existe configuração operacional que substitua `DisabledCountryLookup` nesta entrega.

Implantar servidor compatível antes da SPA; testar os contratos privados, CSRF, ETag e no-store no ambiente
isolado. Configurar localização e preferência são salvamentos independentes: sucesso parcial não é sucesso
conjunto; conflito 412 exige reler e escolher novamente. Não criar preferência a partir de perfil antigo,
idioma, país sugerido ou tempo de espera. A UI manual existente serve contas antigas e novas, sem detector.
Ver [roteiro de homologação e reversão](../operations/regional-matching.md#homologação-e-release--etapa-10).


## Contrato documental de revisão humana — #173 / M0

O [contrato consolidado](media-review.md) inventaria API-M01/M02 atuais e especifica estados, rotas futuras,
autorização, retenção e o recorte faltante de API-M03/M04/T-26 (o recurso de fotos foi removido em 28/10/2026;
ver a [matriz de autorização](media-review.md#matriz-de-autorização-28102026)). É alvo de implementação M1–M7,
sem endpoints, DTOs, OpenAPI ou tipos executáveis novos nesta entrega. AwaitingReview e PHOTO_REVIEW_*
não são negociação de errors do ADR-0034. Upload permanece desligado.
