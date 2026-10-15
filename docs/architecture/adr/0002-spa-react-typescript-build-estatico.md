# ADR-0002: Frontend como SPA React/TypeScript com build estático

- **Status:** Accepted (2026-10-15)
- **Origem:** spec 1.1, 2.2 e 23.1 (ADR-002)
- **Revisar quando:** surgir requisito concreto de SSR ou de app nativo

## Contexto

A interface precisa de jornadas autenticadas, estado de jogo reconciliado com o servidor e acessibilidade
verificável. A API, a autenticação e o tempo real já vivem no ASP.NET Core (ADR-0001). Um segundo runtime de
servidor só se justificaria por renderização no servidor, que a área autenticada não exige.

Alternativas registradas na spec:

- **Blazor.** Descartado: o produto não depende de Blazor (spec 1.1).
- **Next.js, SSR ou BFF Node na área autenticada.** Descartados sem necessidade demonstrada: acrescentam um
  processo Node em produção e uma segunda fronteira de sessão (spec 2.2).
- **Redux como store global.** Descartado: o estado remoto fica no TanStack Query, com cache por identidade.
- **Landing page indexável.** Permitida como build estático separado, sem expor páginas privadas a buscadores.

## Decisão

Vamos construir uma SPA React 19 + TypeScript `strict`, com Vite, Node.js 24 LTS e pnpm só em desenvolvimento
e CI. O build gera arquivos estáticos servidos pelo Caddy. React Router, TanStack Query, React Hook Form + Zod,
Tailwind CSS 4 e shadcn/ui completam a base; o cliente HTTP é gerado do contrato OpenAPI
(openapi-typescript/openapi-fetch), conforme [ADR-0018](0018-contrato-openapi-code-first.md).

Porquê técnico: separar interface e API com contrato tipado, sem servidor Node a operar. Porquê de negócio:
operação mais simples numa VPS pequena e um contrato único entre frontend e backend.

## Consequências

- Positivas: produção só serve arquivos estáticos; tipos gerados do contrato detectam divergência no CI.
- Negativas: autorização visual no React não protege nada; toda regra crítica vive no servidor e precisa de
  teste negativo próprio. Componentes shadcn/ui ficam no repositório e exigem manutenção e testes próprios.
- Trade-off: sem SSR, o primeiro carregamento depende do bundle; divisão por rota e orçamento de Web Vitals
  (spec 13.5) compensam.

## Segurança e operação

Nenhum segredo em variáveis `VITE_*`. Cache privado é limpo no logout, na revogação e na troca de conta.
Conteúdo privado não é persistido offline (spec 2.2, 13.5).

## Compliance

- CI: `pnpm install --frozen-lockfile`, lint, checagem de tipos, Vitest e Playwright (spec 17.1; a implementar
  no workflow de M0/M1).
- Manifesto de release sem imagem Node de runtime (spec 17.2; a implementar com o pipeline de release).
- T-21 (troca de identidade no mesmo navegador) a partir de M1.
