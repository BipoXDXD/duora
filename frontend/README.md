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
  `i18n` (catálogos de textos em PT-BR e EN e o idioma ativo) e `paths.ts` (rotas).
- `src/test`: setup do Vitest, servidor MSW e helpers de renderização.
- `config`: configuração do servidor de desenvolvimento e a regra de lint `duora/no-literal-ui-text`, testadas.
- `e2e`: testes Playwright; `e2e/support/fake-api.ts` simula a API por `route` no contexto do navegador (as abas dividem o mesmo backend).

## Idiomas (PT-BR e EN)

Decisão e alternativas: [ADR-0022](../docs/architecture/adr/0022-i18n-intl-messageformat-catalogo-tipado.md).

- Todo texto visível vem de `src/shared/i18n/catalog.pt-BR.ts` (catálogo de origem) e
  `catalog.en.ts`, em ICU MessageFormat, formatado pelo `intl-messageformat`. Nos componentes:
  `const { t } = useI18n()` e `t('login.title')` ou `t('register.passwordHint', { min, max })`.
- Chave inexistente, valor faltando ou de tipo errado não compilam: os argumentos saem do texto ICU do
  catálogo de origem (`{nome}` aceita texto ou número; `{n, number}`, `{n, plural, …}` exigem número;
  `{s, select, …}` exige texto). Mensagens sem argumentos (`PlainMessageKey`) podem ir em tabelas.
- Idioma inicial: a escolha guardada em `localStorage` (`duora.locale`), depois `navigator.languages`
  (qualquer `pt-*` vira pt-BR, qualquer `en-*` vira en), por fim pt-BR. O seletor do cabeçalho troca na hora,
  atualiza `<html lang>` e o título da rota, e guarda a escolha.
- Datas: `formatDateTime(new Date(instante))` do `useI18n`, no idioma ativo e no fuso do navegador. A API
  continua mandando instantes em UTC; os testes rodam com `TZ=UTC`.
- Erros de validação: o schema Zod guarda a chave (`'validation.emailRequired' satisfies ValidationKey`) e a
  tela traduz com `useValidationMessage()`, para o erro já exibido mudar de idioma junto.
- O lint `duora/no-literal-ui-text` barra texto com letras escrito direto no JSX (filhos e props como
  `aria-label`, `title`, `label`, `intro`). Pontuação entre trechos traduzidos (`{': '}`) passa.

Para adicionar ou mudar um texto:

1. Crie a chave em `catalog.pt-BR.ts`, no grupo da tela (`tela.assunto`), com argumentos em ICU; nada de
   montar frase concatenando strings no componente. Plural e variação vão para `{n, plural, …}` ou
   `{x, select, …}`.
2. Acrescente a mesma chave, com os mesmos argumentos, em `catalog.en.ts`. Traduza a intenção, não palavra a
   palavra, e mantenha a neutralidade das mensagens de erro (não revelar se um e-mail tem conta). Esquecer a
   chave falha o `typecheck` (`satisfies Catalog`); argumento diferente falha o `typecheck` e o
   `catalog.test.ts`.
3. Use `t('chave', valores)` no componente. Para chave guardada numa tabela, tipe a tabela com
   `PlainMessageKey`.
4. Teste o comportamento pelo texto visível em pt-BR (o padrão do `renderApp`/`renderWithI18n`); quando o
   idioma importar, passe `'en'` ao helper.

Fora do escopo do frontend: os e-mails (confirmação e recuperação) são montados pelo backend e seguem só em
PT-BR.

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
- Um 401 em qualquer escrita também reconsulta a identidade (`MutationCache` em `query-client.ts`). O 401 pode ser
  `REAUTHENTICATION_REQUIRED` ou senha atual errada com a sessão válida, então quem decide é o `/me`: só o 401 dele
  leva à entrada. Um `/me` que responde outra pessoa descarta tudo o que era da anterior, leituras e escritas.
- A identidade (`['me']`) e as sessões têm `gcTime: 0`: ao sair da área autenticada, saem da memória. Voltar a ela
  pelo histórico espera o servidor, em vez de reexibir dados de uma sessão que pode ter expirado.
- Entrar ou sair avisa as outras abas por `BroadcastChannel` (`duora.identity`, sem dado pessoal na mensagem); elas
  descartam o cache e reconsultam a identidade (`identity-broadcast.ts`, inscrição no `App.tsx`). Trade-off aceito:
  o aviso não diz quem entrou, então um novo login da mesma pessoa em outra aba também reseta esta aba (a tela
  volta a carregar e o que estava digitado nela se perde).
- Página privada restaurada do bfcache recarrega (`back-forward-cache.ts`, ligado no `main.tsx`); ao entrar no
  bfcache ela é escondida (o `html` tem o fundo da superfície, sem flash), para não reaparecer com dados por um
  instante. Privada é qualquer rota fora da lista pública (negar por padrão); as públicas voltam como estavam, com
  o rascunho do cadastro. O custo é perder o estado em memória da área autenticada ao voltar de outro site. O Playwright desliga o bfcache, então o E2E simula os eventos `pagehide`/`pageshow`.
- Nada da sessão vai para `localStorage` ou `sessionStorage`; as únicas chaves guardadas são `duora.locale` e
  `duora.newVersionReloadAt` (o E2E `session-privacy.spec.ts` falha com qualquer outra).
- Os links de e-mail trazem `userId` e `token` no fragmento, que não vai ao servidor nem aos logs (TM-19).
  A tela lê o fragmento uma vez, guarda em estado e limpa a URL com replace. O `index.html` declara
  `referrer: no-referrer`.
- Cada área de rotas tem seu error boundary; a troca de rota move o foco para o h1 e anuncia o título.

## Divisão do bundle por rota

- A página inicial e a de rota inexistente vão no bundle principal; as demais telas são carregadas sob
  demanda por `routePage`/`lazyPage` (`src/app/AppRoutes.tsx`, `src/app/lazy-page.tsx`), cada uma num chunk
  próprio. Tela nova entra em `AppRoutes.tsx` do mesmo jeito.
- Na primeira carga, o `Suspense` do layout mostra `RouteFallback`: uma região `role="status"` que só ganha
  texto depois de 400 ms e, depois de aparecer, fica pelo menos 500 ms (`route-fallback-timing.ts`), para não
  piscar. Dentro da SPA a navegação acontece numa transition: a tela anterior fica visível, com o foco onde
  estava, até a nova chegar.
- Chunk que não baixa (quase sempre deploy novo com a aba aberta): a falha gruda, como no `React.lazy`, porque
  o navegador guarda o módulo que falhou. O listener `vite:preloadError` do `main.tsx` recarrega a página uma
  vez, com guarda de 60 s em `sessionStorage` (`duora.newVersionReloadAt`). Se ainda falhar, o error boundary da
  área avisa que há versão nova e oferece "Recarregar a página"; ele não tenta baixar o chunk de novo.
- A área da conta espera os chunks do guarda e da página juntos, para não mostrar um segundo fallback.
- Nos testes, `renderApp` é assíncrono e espera o chunk (`await renderApp('/entrar')`); um `render` próprio
  usa `await waitForRouteChunk()`. O E2E `smoke.spec.ts` garante que a página inicial não baixa as telas de
  formulário e que um chunk com 404 dá uma recarga só e depois o aviso.

## Design system

Tokens semânticos em OKLCH no `@theme` de `src/index.css` (`surface`, `surface-muted`, `text`,
`text-muted`, `border`, `action`, `danger`, `focus` e pares `-foreground`/`-hover`). A paleta padrão
do Tailwind está desligada. `src/app/design-tokens.test.ts` mede o contraste WCAG de cada par usado.
Valores provisórios de M0, sem identidade visual final; um único tema claro.

Componentes shadcn/ui (`components.json`, primitivas Radix) são copiados para `src/shared/ui` e
adaptados aos tokens; cada um passa a ter testes próprios.
