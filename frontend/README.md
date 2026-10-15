# Duora — frontend

SPA React 19 + TypeScript (Vite) do Duora. Em produção é um bundle estático servido na mesma
origem da API; não há servidor Node em produção. Especificação: `docs/product/spec.md` (seções 2.2,
2.5, 8.1 e 13).

## Pré-requisitos

- Node.js 24 LTS (`node --version`).
- pnpm na versão fixada em `package.json#packageManager`. Sem pnpm global, use o Corepack:
  `corepack pnpm <comando>` (ou `corepack enable pnpm`).
- Para `pnpm dev`: .NET SDK (para exportar o certificado de desenvolvimento) e o backend rodando.

## Comandos

| Comando                             | O que faz                                                                                                               |
| ----------------------------------- | ----------------------------------------------------------------------------------------------------------------------- |
| `pnpm install --frozen-lockfile`    | Instala exatamente o que está no lockfile                                                                               |
| `pnpm cert:dev`                     | Exporta o certificado HTTPS de desenvolvimento do ASP.NET Core para `~/.aspnet/https/duora-spa.{pem,key}` (fora do Git) |
| `pnpm dev`                          | Servidor de desenvolvimento em HTTPS, com proxy de `/api` e `/hubs` (WebSocket) para o backend                          |
| `pnpm build`                        | Checa tipos e gera o bundle em `dist/`                                                                                  |
| `pnpm typecheck`                    | `tsc --noEmit` nos projetos da app e das ferramentas                                                                    |
| `pnpm lint`                         | ESLint (typescript-eslint type-checked), zero aviso                                                                     |
| `pnpm format:check` / `pnpm format` | Prettier                                                                                                                |
| `pnpm test`                         | Vitest + Testing Library                                                                                                |
| `pnpm e2e:install`                  | Instala os browsers do Playwright (Chromium e WebKit)                                                                   |
| `pnpm e2e`                          | Playwright + axe (WCAG 2.2 A/AA) contra o bundle de produção                                                            |

## Ambiente local

`pnpm dev` exige HTTPS: sem o certificado ele para com uma mensagem dizendo o que rodar. O proxy
confia só no certificado exportado (o mesmo do backend), sem desligar a verificação TLS.

Variáveis privadas (veja `.env.example`), lidas só pelo `vite.config.ts`:

- `DUORA_API_ORIGIN`: origem HTTPS local do backend (padrão `https://localhost:5001`).
- `DUORA_DEV_CERT_FILE` / `DUORA_DEV_KEY_FILE`: caminhos do certificado da SPA.

Nada de segredo em `VITE_*`: essas variáveis entram no JavaScript público.

## Organização

- `src/app`: composição (rotas, layout, providers, `QueryClient`).
- `src/features/<feature>`: telas e lógica de cada jornada.
- `src/shared`: código sem dono de feature — `api` (cliente HTTP), `ui` (componentes do design system).
- `config`: configuração do servidor de desenvolvimento, testada.
- `e2e`: testes Playwright.

## Cliente HTTP

`src/shared/api` é o único ponto de saída HTTP: mesma origem com `credentials: 'same-origin'`, header
antiforgery `X-XSRF-TOKEN` (lido do cookie `XSRF-TOKEN`) em métodos que alteram estado, `AbortSignal`,
erro tipado para Problem Details (`ApiProblemError`), resposta fora do contrato (`ApiResponseError`) e
falha de rede (`ApiNetworkError`). Não faz retry; o `QueryClient` repete só leituras com falha
transitória e nunca mutações.

Quando o contrato OpenAPI existir, os tipos serão gerados com openapi-typescript e o cliente será
`createClient<paths>({ baseUrl: '/api/v1', fetch: apiClient.fetch })`, mantendo as mesmas garantias.

## Design system

Tokens semânticos em OKLCH no `@theme` de `src/index.css` (`surface`, `surface-muted`, `text`,
`text-muted`, `border`, `action`, `danger`, `focus` e pares `-foreground`/`-hover`). A paleta padrão
do Tailwind está desligada. `src/app/design-tokens.test.ts` mede o contraste WCAG de cada par usado.
Valores provisórios de M0, sem identidade visual final; um único tema claro.

Componentes shadcn/ui (`components.json`, primitivas Radix) são copiados para `src/shared/ui` e
adaptados aos tokens; cada um passa a ter testes próprios.
