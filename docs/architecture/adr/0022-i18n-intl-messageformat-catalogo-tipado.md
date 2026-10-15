# ADR-0022: Interface em PT-BR e EN com intl-messageformat e catálogo tipado próprio

- **Status:** Accepted (2026-10-15), decidido pelo usuário em 2026-10-15
- **Origem:** spec 13.6 (textos centralizados, `Intl`, fuso), 13.4 e 13.7; decisão do responsável técnico de
  oferecer a interface em PT-BR (padrão) e EN
- **Revisar quando:** entrar um terceiro idioma, o catálogo passar de algumas centenas de chaves ou houver
  tradução por fornecedor externo que exija formato de arquivo (XLIFF, JSON por idioma)

## Contexto

A SPA tinha todos os textos em PT-BR num módulo único (`src/shared/messages.ts`). A interface passa a ter
inglês. Requisitos: chave inexistente é erro de compilação; os dois idiomas têm o mesmo conjunto de chaves;
plural e interpolação em ICU MessageFormat, sem concatenar strings; datas e números por `Intl` no idioma ativo;
bundle pequeno (spec 13.5) e licença na allowlist (`scripts/supply-chain/license-policy.json`).

Alternativas consideradas (tamanho medido com esbuild, minificado e gzip, sem React, em 2026-10-15):

- **react-i18next 17 + i18next 26 + i18next-icu 2.5 (MIT).** Ecossistema maior, detecção de idioma e lazy
  loading prontos. Contras: o formato nativo não é ICU; ICU exige o plugin, que embute o intl-messageformat e
  desliga a inferência de argumentos do i18next. Chaves tipadas por module augmentation, argumentos não.
  30,6 kB gz (19,2 kB sem ICU). Muito mais do que o Duora usa.
- **react-intl 12 / FormatJS (BSD-3-Clause).** ICU nativo, componentes `FormattedMessage`/`FormattedDate`.
  Chaves tipáveis pela interface global `FormatjsIntl.Message`; valores continuam
  `Record<string, PrimitiveType>`, sem checar nome nem tipo. 14,6 kB gz.
- **Só `Intl` nativo e funções TypeScript por mensagem** (o que existia). Zero dependência e tipagem perfeita.
  Contras: o navegador não tem MessageFormat (o `Intl.MessageFormat` ainda é proposta e segue o MF2, não o
  ICU); plural e select viram código por mensagem, que tradutor não edita, e o catálogo deixa de ser texto.
- **intl-messageformat 12 (BSD-3-Clause, o núcleo do FormatJS) + módulo tipado próprio.** Escolhida.

## Decisão

Vamos usar o `intl-messageformat` para formatar ICU e um módulo próprio, `src/shared/i18n`, com:

- catálogos `as const` em TypeScript, um por idioma, chaves planas (`login.title`); o PT-BR é a fonte de
  chaves e argumentos, e o EN declara `satisfies Catalog`;
- `t(chave, valores)` com chave e valores checados pelo compilador: um tipo (`IcuArguments`) lê os argumentos
  do texto ICU literal do catálogo de origem (`{n}`, `{n, number}`, `{n, plural, …}`, `{s, select, …}`),
  com testes `expectTypeOf`;
- datas por `Intl.DateTimeFormat` no idioma ativo e no fuso do navegador; os instantes continuam UTC na API;
- idioma inicial: escolha guardada, depois `navigator.languages`, por fim pt-BR. A escolha fica em
  `localStorage` (`duora.locale`), só como preferência;
- os dois catálogos entram no bundle principal, sem carregamento sob demanda: juntos têm poucos kB.

Porquê técnico: é o único caminho com ICU de verdade **e** argumentos checados pelo compilador; usa só o
núcleo de formatação, sem provider de terceiros nem componentes. Porquê de negócio: menos código de terceiros
a manter e o menor custo de bundle entre as opções com ICU (9,7 kB gz da biblioteca).

## Consequências

- Positivas: renomear ou esquecer um argumento numa tradução quebra o build; trocar de idioma atualiza textos,
  título da rota e `<html lang>` sem recarregar; erros de validação guardam a chave e mudam de idioma junto.
- Negativas: o tipo que lê ICU é código nosso (cerca de 60 linhas de tipos), sem suporte a chaves escapadas com
  apóstrofo nem a tags ricas (`<b>…</b>`); se o catálogo precisar disso, o tipo cresce ou a decisão é revista.
- Bundle: de 475,19 kB (150,25 kB gz) para 520,16 kB (162,80 kB gz) no build de 2026-10-15, +12,6 kB gz,
  somando biblioteca, catálogo EN e código. O chunk único passou do aviso de 500 kB do Vite; a divisão por rota
  já pedida na spec 13.5 resolve, e não se sobe o limite para calar o aviso.
- Trade-off: sem extração automática de mensagens nem formato de arquivo padrão de tradução; com dois idiomas
  e tradução feita no próprio repositório, o catálogo em TypeScript basta.

## Segurança e operação

Mensagens de erro continuam neutras nos dois idiomas (não revelam se um e-mail tem conta). O valor lido do
`localStorage` é dado externo: só vale se estiver na lista de idiomas. Nenhum dado pessoal vai para o
armazenamento. Textos são renderizados como texto pelo React; o catálogo não usa HTML.

## Compliance

- `catalog.test.ts`: os dois idiomas têm as mesmas chaves, mensagens ICU válidas e os mesmos argumentos (por
  `isStructurallySame` do parser ICU e por tipo).
- `typecheck`: chave inexistente, valor faltando ou de tipo errado não compilam (`translator.test.ts`).
- Lint `duora/no-literal-ui-text`: texto visível escrito direto no JSX falha o CI.
- E2E `i18n.spec.ts`: jornada principal em inglês com axe (WCAG 2.2 A/AA) e troca de idioma pelo teclado.
