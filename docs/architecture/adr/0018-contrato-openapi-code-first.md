# ADR-0018: Contrato OpenAPI code-first, versionado e com gate de compatibilidade

- **Status:** Accepted (2026-10-15)
- **Origem:** spec 2.1, 8.1, 17.1 e 21.2; decisão do responsável técnico
- **Revisar quando:** a API passar a ter consumidores externos que precisem do contrato antes da implementação

## Contexto

A spec define "OpenAPI nativo + cliente TypeScript gerado", com contrato gerado no CI e revisado como artefato
da release (2.1, 17.1). A convenção geral do responsável técnico é escrever a spec OpenAPI antes do código
(design-first). O único consumidor da API é a SPA do próprio projeto, e o maior risco é a divergência entre
documento, implementação e tipos do frontend.

Alternativas consideradas:

- **Design-first com teste de equivalência** (spec escrita à mão e teste comparando-a ao documento gerado).
  Descartada: mantém duas fontes editáveis do mesmo contrato e um teste só para mantê-las iguais.

## Decisão

Vamos gerar o documento OpenAPI a partir do código com o OpenAPI nativo do ASP.NET Core, no CI, e versioná-lo
no repositório. O CI roda `oasdiff breaking` contra a versão da branch principal e barra breaking change não
aprovada; todo PR que muda o documento exige revisão do diff. Os tipos do frontend são gerados desse arquivo
(openapi-typescript) e conferidos com `git diff --exit-code`.

Esta decisão diverge conscientemente da regra de design-first: com um único consumidor interno, uma fonte de
verdade (o código) e nenhum drift valem mais do que escrever o contrato antes. O design pelo consumidor
continua obrigatório, feito na Definition of Ready da feature (contratos e estados, [workflow.md](../../automation/workflow.md)).

Porquê técnico: documento, servidor e cliente nunca divergem. Porquê de negócio: menos artefatos a manter
num projeto de uma pessoa.

## Consequências

- Positivas: nenhum drift entre spec, rotas e tipos; breaking change vira falha de CI, não descoberta em
  produção.
- Negativas: o contrato só existe depois do código; revisar o design exige olhar o diff gerado.
- Trade-off: a qualidade do documento depende de anotações e DTOs bem nomeados no código.

## Segurança e operação

O documento publicado em produção tem acesso controlado (spec 8.5). Mudança incompatível exige nova versão
ou transição documentada (spec 8.1); o manifesto da release registra a versão do contrato (spec 17.2).

## Compliance

- CI exporta o OpenAPI e falha se o arquivo versionado divergir do gerado (a implementar no M0).
- CI roda `oasdiff breaking` e falha sem aprovação explícita (a implementar no M0/M1).
- CI regenera os tipos do frontend e roda `git diff --exit-code` (a implementar no M0/M1).
