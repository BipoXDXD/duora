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
