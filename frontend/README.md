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
- `src/shared`: código sem dono de feature — `api` (cliente HTTP), `ui` (componentes do design system),
  `messages.ts` (todos os textos da interface em PT-BR) e `paths.ts` (rotas).
- `src/test`: setup do Vitest, servidor MSW e helpers de renderização.
- `config`: configuração do servidor de desenvolvimento, testada.
- `e2e`: testes Playwright; `e2e/support/fake-api.ts` simula a API por `page.route`.

## Cliente HTTP

`src/shared/api` é o único ponto de saída HTTP: mesma origem com `credentials: 'same-origin'`, header
antiforgery `X-XSRF-TOKEN` (lido do cookie `XSRF-TOKEN`) em métodos que alteram estado, `AbortSignal`,
erro tipado para Problem Details (`ApiProblemError`), resposta fora do contrato (`ApiResponseError`) e
falha de rede (`ApiNetworkError`), com `retryAfterSeconds` lido do `Retry-After`. Não faz retry; o
`QueryClient` repete só leituras com falha transitória e nunca mutações.

`sendMutation` pede `GET /api/v1/auth/csrf` antes de cada mutação, para nunca enviar o token de outra
identidade, e `refreshAntiforgeryToken(apiClient)` renova o token depois de login e logout. Respostas de API passam
por Zod na fronteira (`*-api.ts` de cada feature); nada de `as T`.

Quando o contrato OpenAPI existir, os tipos serão gerados com openapi-typescript e o cliente será
`createClient<paths>({ baseUrl: '/api/v1', fetch: apiClient.fetch })`, mantendo as mesmas garantias.

## Autenticação e sessão (M1)

| Rota                                                     | Tela                                                                         |
| -------------------------------------------------------- | ---------------------------------------------------------------------------- |
| `/criar-conta`                                           | Cadastro, com aceite dos Termos e da Privacidade (`LEGAL_DOCUMENT_VERSIONS`) |
| `/verifique-seu-email`                                   | Aviso neutro depois do cadastro                                              |
| `/confirmar-email#userId=…&token=…`                      | Confirmação por clique, com a senha do cadastro (link do e-mail)             |
| `/entrar`                                                | Entrada; mostra aviso de sessão expirada, saída ou entrada necessária        |
| `/esqueci-a-senha` e `/redefinir-senha#userId=…&token=…` | Recuperação de senha                                                         |
| `/conta`                                                 | Área autenticada: conta, sessões, revogação e saída                          |

- `401 MFA_REQUIRED` no login mostra só uma mensagem neutra de que o segundo fator é exigido e ainda não
  está disponível. O passo de segundo fator entra com o MFA administrativo, na onda 2 do M1.
- `useMe` (`['me']`) descobre a identidade; dados privados usam chaves sob `['account', userId, …]`.
- `RequireSession` leva à entrada no 401. Um 401 em qualquer leitura privada reconsulta a identidade.
- Ao entrar e ao chegar à tela de entrada, todo o cache do `QueryClient` é descartado (T-21). O descarte
  fica na tela de entrada, e não no clique de sair, porque o React Router navega dentro de uma transition:
  limpar com a área autenticada ainda montada faz as consultas dela buscarem de novo os dados de quem saiu.
- Nada da sessão vai para `localStorage` ou `sessionStorage`.
- Os links de e-mail trazem `userId` e `token` no fragmento, que não vai ao servidor nem aos logs (TM-19).
  A tela lê o fragmento uma vez, guarda em estado e limpa a URL com replace. O `index.html` declara
  `referrer: no-referrer`.
- Cada área de rotas tem seu error boundary; a troca de rota move o foco para o h1 e anuncia o título.

## Design system

Tokens semânticos em OKLCH no `@theme` de `src/index.css` (`surface`, `surface-muted`, `text`,
`text-muted`, `border`, `action`, `danger`, `focus` e pares `-foreground`/`-hover`). A paleta padrão
do Tailwind está desligada. `src/app/design-tokens.test.ts` mede o contraste WCAG de cada par usado.
Valores provisórios de M0, sem identidade visual final; um único tema claro.

Componentes shadcn/ui (`components.json`, primitivas Radix) são copiados para `src/shared/ui` e
adaptados aos tokens; cada um passa a ter testes próprios.
