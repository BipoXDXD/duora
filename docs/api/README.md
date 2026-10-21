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

## Fuso privado da conta (issue #81)

`GET /me/time-zone`: `{timeZone: string|null, version: string}` + ETag. `PUT /me/time-zone` exige
If-Match e campo `timeZone` obrigatório; null explícito remove. 428 sem precondição, 412 versão divergente,
400/VALIDATION_FAILED (`errors.timeZone: [UNSUPPORTED_TIME_ZONE]`) para ID não suportado. Autorização do
titular, CSRF e status Active/PendingEligibility; 503 em disputa de lock além do teto. Nenhum efeito em
matching, residência ou segurança de login. `GET /me/time-zone/options` é autenticado e devolve
`{timeZones: string[]}` (IANA do servidor mais UTC). `/me` acrescenta `preferredTimeZone` anulável;
cliente sem campo continua usando o navegador. Não persistir detecção sem confirmação explícita.
