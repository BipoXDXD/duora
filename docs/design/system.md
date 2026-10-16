# Design system

Semântica, tokens, ações, movimento e inventário de componentes do Duora (spec
[13.3](../product/spec.md#133-design-system-implementável); catálogo
[21.2](../product/spec.md#212-catálogo-de-documentação-interna)). O repositório é a fonte de verdade dos
tokens publicados: os valores vivem em `frontend/src/index.css`, e este documento explica a semântica e a
escala. Nada aqui é identidade visual final (spec 13.3, "ponto de partida de design").

> **Legenda de status:** **implementado** = existe no código e tem teste; **proposto** = decisão de design
> para revisão, ainda sem código. Todo token proposto entra com o teste de contraste correspondente em
> `frontend/src/app/design-tokens.test.ts`.

Fontes: Refactoring UI (RUI) caps. 2 a 6 e 8; CSS in Depth (CID) caps. 2, 11, 12 e 15; Designing Interfaces
(DI) caps. 8, 10 e 11. Notas de regra da casa em `~/.fonte externa/knowledge/ui-design.md` não são citadas como fonte
normativa.

## Princípios

1. **Semântica antes de valor.** Componentes usam `bg-action`, nunca um tom cru; trocar a identidade visual
   muda tokens, não componentes (DI cap. 11, sistemas de interface e design atômico).
2. **Escala fechada.** Espaço, tipo, raio, sombra e duração só assumem valores da escala; valor arbitrário
   (`p-[13px]`) exige justificativa no PR (RUI cap. 1, "Limit your choices").
3. **Contraste medido em cada par real**, inclusive desabilitado e estados (spec 13.3), por teste automatizado.
4. **Um tema bem testado** no primeiro marco: claro. Segundo tema só com a matriz completa (spec 13.3).
5. **Unidades relativas** para tipo e espaço (`rem`), para respeitar a preferência de fonte do navegador
   (CID cap. 2, "Stop thinking in pixels"; "ems and rems").

## Cor

### Tokens semânticos existentes (implementados)

Valores em OKLCH em `frontend/src/index.css`, com a paleta padrão do Tailwind desligada. A escolha de OKLCH
segue CID cap. 11 (seção 11.3): luminosidade perceptualmente uniforme facilita gerar tons com contraste
previsível. Contrastes calculados com a fórmula WCAG a partir dos valores atuais; o teste automatizado é a
fonte de verdade.

| Token | Uso | Par medido | Razão | Mínimo |
|---|---|---|---|---|
| `surface` | Fundo da página e de cards | — | — | — |
| `surface-muted` | Fundo rebaixado (rodapé, aviso, desabilitado, hover neutro) | — | — | — |
| `text` | Texto principal | sobre `surface` / `surface-muted` | 15,99 / 14,43 | 4,5 |
| `text-muted` | Texto secundário, texto desabilitado | sobre `surface` / `surface-muted` | 6,63 / 5,99 | 4,5 |
| `border` | Bordas de campo e de botão desabilitado | sobre `surface` / `surface-muted` | 3,84 / 3,46 | 3 |
| `action` | Fundo do botão primário, texto e borda do secundário, links | sobre `surface` / `surface-muted` | 6,78 / 6,12 | 4,5 |
| `action-hover` | Hover do primário | `action-foreground` sobre ele | 9,18 | 4,5 |
| `action-foreground` | Texto sobre `action` | sobre `action` | 6,78 | 4,5 |
| `danger` | Fundo do botão destrutivo, texto de erro | sobre `surface` / `surface-muted` | 6,40 / 5,78 | 4,5 |
| `danger-hover` | Hover do destrutivo | `danger-foreground` sobre ele | 8,24 | 4,5 |
| `danger-foreground` | Texto sobre `danger` | sobre `danger` | 6,40 | 4,5 |
| `focus` | Anel de foco (2 px, deslocado 2 px) | sobre `surface` / `surface-muted` | 5,61 / 5,07 | 3 |

O anel de foco fica a 2 px do componente, sobre a superfície; por isso o par relevante é `focus`/`surface`, não
`focus`/`action` (1,21). Se algum componente remover o deslocamento, o par passa a ser contra o próprio
componente e precisa de nova medição.

### Tokens propostos

| Token | Para quê | Regra |
|---|---|---|
| `success` + `success-surface` | Confirmações ("Decisão salva", "Denúncia recebida") | Sempre com ícone e texto; nunca só a cor (RUI cap. 5, "Don't rely on color alone") |
| `warning` + `warning-surface` | Faixa "Reconectando", aviso de prazo, "Sem conexão" | Texto escuro sobre fundo claro do mesmo matiz (RUI cap. 5, contraste invertido) |
| `danger-surface` | Fundo de mensagem de erro de formulário e de banner de erro | `danger` como texto sobre ele ≥ 4,5:1 |
| `surface-raised` | Card e diálogo acima da página | Distinguível de `surface` por borda ou sombra, não só pelo tom |
| `overlay` | Véu atrás do diálogo modal | Não é usado para comunicar estado |

Escalas de tons crus (`--color-brand-100..900`, neutros quentes) ficam para a identidade visual final; até
lá, os tokens semânticos apontam direto para valores OKLCH, como hoje.

## Espaçamento (proposto)

Base de 4 px (`--spacing: 0.25rem` do Tailwind), usando só um subconjunto em que valores vizinhos diferem em
pelo menos ~25% (RUI cap. 3, "Establish a spacing and sizing system"):

| Token (classe Tailwind) | rem | px | Uso típico |
|---|---|---|---|
| 1 | 0,25 | 4 | Ícone ↔ rótulo muito próximo |
| 2 | 0,5 | 8 | Dentro de componente: gap de ícone, padding vertical de botão |
| 3 | 0,75 | 12 | Padding de chip/badge; espaço entre label e campo |
| 4 | 1 | 16 | Padding de card e de botão; gutter lateral no mobile |
| 6 | 1,5 | 24 | Entre campos de um formulário; entre itens de lista com conteúdo |
| 8 | 2 | 32 | Entre grupos de uma tela |
| 12 | 3 | 48 | Entre seções; padding vertical do `main` |
| 16 | 4 | 64 | Respiro de páginas públicas |
| 24 | 6 | 96 | Só em página pública ampla |

Regra de agrupamento: o espaço entre grupos é maior que o espaço dentro do grupo (RUI cap. 3, "Avoid
ambiguous spacing"). Exceções (por exemplo, 6 px num indicador de status) são justificadas no PR.

## Tipografia (proposta)

Corpo de **16 px equivalentes** (`1rem`, já aplicado em `body`), fonte do sistema (stack atual em
`--font-sans`), dois pesos: 400 e 600. Escala feita à mão, em `rem` (RUI cap. 4, "Establish a type scale";
CID cap. 2 sobre `rem`):

| Papel | Classe | rem / px | Altura de linha | Peso |
|---|---|---|---|---|
| Auxiliar (legenda, contador, metadado) | `text-sm` | 0,875 / 14 | 1,5 | 400 |
| Corpo | `text-base` | 1 / 16 | 1,5 | 400 |
| Corpo destacado, rótulo de botão | `text-base` | 1 / 16 | 1,5 | 600 |
| Introdução, título de card | `text-lg` | 1,125 / 18 | 1,4 | 600 |
| Título de seção (`h2`) | `text-xl` | 1,25 / 20 | 1,3 | 600 |
| Título de tela (`h1`) no mobile | `text-2xl` | 1,5 / 24 | 1,25 | 600 |
| Título de tela (`h1`) a partir de 768 px | `text-3xl` | 1,875 / 30 | 1,2 | 600 |
| Destaque da página pública | `text-4xl` | 2,25 / 36 | 1,1 | 600 |

- Nada abaixo de 14 px; nada abaixo de peso 400.
- Texto corrido limitado a ~65 caracteres (`max-w-prose`; RUI cap. 4, "Keep your line length in check").
- Hierarquia visual separada da semântica: o nível do `h` segue o documento; o tamanho segue a tabela (RUI
  cap. 2, "Separate visual hierarchy from document hierarchy").
- Caracteres PT-BR, fonte carregando e fallback são verificados quando houver fonte web (spec 13.3; CID cap.
  12, seções 12.2 a 12.5). Com a stack do sistema, não há carregamento.

## Raio (proposto)

| Token | Valor | Uso |
|---|---|---|
| `rounded-md` | 0,375 rem (6 px) | Botão, campo, card, diálogo, aviso (único raio de UI; já usado no botão) |
| `rounded-full` | — | Avatar e indicador de status; nada mais |

Um raio só, para não misturar cantos (RUI cap. 1, "Choose a personality"). Raio maior é decisão da identidade
visual final.

## Superfícies e elevação (proposto)

Quatro níveis discretos (spec 13.3, "Superfícies"); a hierarquia nunca depende só de sombra ou cor, por isso
os níveis 1 e 2 também têm borda `border`:

| Nível | Elemento | Fundo | Borda | Sombra (duas partes, RUI cap. 6) |
|---|---|---|---|---|
| 0 | Página | `surface` | — | — |
| 1 | Card, item de lista, composer do chat | `surface` | 1 px `border` | nenhuma |
| 2 | Menu, popover, toast | `surface` | 1 px `border` | `0 4px 6px / 10%` + `0 2px 4px / 6%` |
| 3 | Diálogo modal | `surface` | 1 px `border` | `0 20px 40px / 20%` + véu `overlay` |

Sombras escuras somem em tema escuro; se um segundo tema vier, a elevação passa a usar superfície mais clara
por nível.

## Movimento (proposto)

| Token | Duração | Curva | Uso |
|---|---|---|---|
| `motion-instant` | 0 ms | — | Mudança de estado sem animação (padrão) |
| `motion-fast` | 150 ms | `ease-out` | Hover, foco, troca de cor |
| `motion-base` | 250 ms | `ease-out` | Entrada de diálogo, toast, painel |

- Movimento só com finalidade explicativa: mostrar de onde veio um diálogo ou que uma mensagem chegou
  (spec 13.3). Nenhuma animação de recompensa, confete ou pulsação ligada a pessoas.
- **Variante reduzida:** a regra global de `prefers-reduced-motion: reduce` em `frontend/src/index.css` já
  zera duração de transição e animação e desliga `scroll-behavior` suave (padrão da CID caps. 8 e 15,
  seção 15.1). O estado final continua visível; nada depende de ver a animação.
- Nada pisca mais de três vezes por segundo; o tempo restante não anima a cada segundo.

## Ilustração

Direção escolhida pelo responsável pelo produto: **objetos simbólicos** em WebP estático, com dois tons
recoloridos para `action` e `text-muted` (spec 13.3, "Ilustração e fotografia"). Componente e assets estão
**implementados** (`frontend/src/shared/ui/illustration.tsx`); a colocação nas telas entra com cada tela.

### Regra de conteúdo

- **Nenhuma pessoa, rosto, mão, parte do corpo ou silhueta humana**, nem realista nem estilizada. A spec 13.3
  proíbe sugerir identidade falsa ou presença fictícia, e o [brief](../product/experience-brief.md) pede
  ilustração sem casal-padrão; objeto não tem gênero, idade nem corpo.
- **Nenhum texto, letra, número, símbolo ou logo** dentro da imagem: texto vem do catálogo de idiomas, ao lado.
- O prefixo de prompt em `tools/visual/catalog.json` carrega essas proibições, e
  `frontend/config/illustrations.test.ts` (job `frontend` do CI) confere que elas continuam lá. Fonte que viola a regra é descartada e gerada de novo, nunca retocada.
- Só modelos aprovados (fonte externa, via fonte externa) e só com linha em
  [assets-licenses.md](assets-licenses.md). Processo em [tools/visual/README.md](../../tools/visual/README.md).

### Uso

| Uso (`catalog.json`) | Tamanho CSS | Onde |
|---|---|---|
| `onboarding` | 240 px | Boas-vindas e primeira visita |
| `experience` | 200 px | Introdução e conclusão de uma experiência |
| `emptyState` | 160 px | Estado vazio de lista (spec 13.2) |
| `screen` | 128 px | Cabeçalho de tela de conteúdo |

- No máximo uma ilustração por tela; ela nunca é o único portador de informação (RUI cap. 5, "Don't rely on
  color alone", vale também para imagem).
- `<Illustration name alt>` quando a imagem acrescenta algo ao texto ao lado; `decorative` quando só enfeita
  (`alt=""` e `aria-hidden`). `alt` em branco sem `decorative` falha no render.
- Largura e altura fixas pelo uso, para não haver layout shift; `max-w-full` reduz no mobile.

## Hierarquia de ações

As quatro variantes já existem em `frontend/src/shared/ui/button.tsx` (alvo mínimo de 44 px, foco sólido,
desabilitado com tokens medidos). A pirâmide segue RUI cap. 2 ("Semantics are secondary") e DI cap. 8 (botões
e grupos de botões):

| Variante | Aparência | Quando usar | Exemplo |
|---|---|---|---|
| `primary` | Fundo `action`, texto `action-foreground` | **Uma por tarefa**: o passo que avança a tarefa da tela | "Inscrever-me", "Aceitar convite", "Salvar decisão" |
| `secondary` | Contorno `action` sobre `surface` | Alternativa legítima de mesmo nível que não é o caminho principal | "Ver regras", "Alterar decisão" |
| `destructive` | Fundo `danger` | Só quando a ação destrutiva **é** a ação primária da tela: confirmação | "Bloquear pessoa" no diálogo de bloqueio; "Excluir conta" na confirmação |
| `neutral` | Texto `text`, sublinhado no hover e foco | Ações de baixa ênfase, cancelar, navegação dentro da tarefa | "Cancelar", "Voltar ao evento" |

### Regras

1. **Uma ação dominante por tarefa** (spec 13.3). Uma tela com duas tarefas (por exemplo, chat e jogo) tem uma
   primária por região, nunca duas lado a lado.
2. **Sair e bloquear são sempre localizáveis** (spec 13.3):
   - Na sala (convite, experiência, chat da sala): botão "Sair" com rótulo textual, fixo no cabeçalho, alvo
     ≥ 44 px, nunca dentro de menu de três pontos e nunca coberto pelo teclado virtual.
   - Na conversa: "Bloquear", "Denunciar" e "Encerrar conexão" num menu "Opções da conversa" com rótulo
     textual no cabeçalho; Segurança também lista as conexões com as mesmas ações.
   - Rebaixar não é esconder: fora da confirmação, essas ações usam `secondary` ou `neutral`, com contraste
     medido e texto, nunca cinza claro ou ícone isolado.
3. **Recusar tem o mesmo peso que aceitar** quando as duas são escolhas legítimas da pessoa ("Aceitar convite"
   × "Recusar"; "Tenho interesse" × "Não tenho interesse"): as duas como `secondary` com a mesma largura, e
   "Salvar decisão" como primária separada (ver [screens.md](screens.md#decisão-privada)).
4. **Confirmação só para efeito difícil de desfazer** (bloquear, excluir conta, encerrar conexão). "Sair da
   sala" não tem confirmação constrangedora: o diálogo existe para oferecer bloquear e denunciar, e "Sair
   agora" é a primária dele.
5. **Botão em pendência** mantém o rótulo, ganha indicador e `aria-busy`, e fica desabilitado para evitar
   clique duplo; a idempotência continua no servidor (spec 13.5).

## Inventário de componentes

Catálogo mínimo da spec 13.3 para virar Storybook (D-DES-04). Toda story inclui as variações transversais:
**tamanho** (320 e 1280 px de viewport), **conteúdo longo** (texto PT-BR longo, nome de 40 caracteres),
**foco**, **desabilitado**, **pendência** e **erro**, além dos estados específicos abaixo. Cada story roda a
verificação de acessibilidade do Storybook e, quando houver interação, uma `play function`.

| Componente | Estados específicos exigidos | Semântica e acessibilidade | A11Y |
|---|---|---|---|
| Botão | 4 variantes × padrão, hover, foco, pressionado, desabilitado, pendente; com ícone; `asChild` como link | `<button>` nativo; pendente com `aria-busy`; rótulo nunca só ícone | 07 |
| Campo de texto | Vazio, preenchido, foco, ajuda, erro, desabilitado, somente leitura; senha com mostrar/ocultar; contador de caracteres | `<label>` persistente; erro em texto ligado por `aria-describedby`; `autocomplete` correto; colar permitido (DI cap. 10, "Input Hints", "Error Messages") | 01 |
| Mensagem de erro | De campo, de formulário (resumo com links para os campos), de página | Resumo recebe foco após envio inválido; erro identificado em texto, não só cor | 01 |
| Modal (diálogo) | Fechado, aberto, com formulário, enviando, erro; variante de confirmação destrutiva | `<dialog>` ou padrão APG Dialog Modal: foco inicial, Esc fecha, foco retorna ao gatilho, título como nome acessível | 03 |
| Toast | Sucesso, informação, erro; com ação ("Desfazer", "Tentar de novo"); sumindo | `role="status"`; não some antes de ser lido se tiver ação; nunca é o único registro de um erro | 05 |
| Navegação | Barra inferior (mobile) e cabeçalho (desktop); item ativo; com contador de não lidas; oculta no modo focado | `<nav aria-label="Principal">`; `aria-current="page"`; rótulos sempre visíveis | 02 |
| Avatar | Com foto aprovada, sem foto (iniciais), foto em processamento, foto rejeitada (placeholder) | Imagem com `alt` vazio quando o nome está ao lado; nunca foto não aprovada | 06 |
| Card de evento | Disponível, inscrito, lista de espera (posição), lotado, check-in aberto, cancelado, carregando (skeleton) | Título é link do card; status em texto além da cor; horário com fuso | 06 |
| Convite | Recebido com prazo, aceitando, aceito aguardando a outra pessoa, expirado, recusado | Chegada anunciada uma vez em região `status`; prazo em texto ("até 21:04") | 05, 08 |
| Indicador de conexão (tempo real) | Conectado (oculto), reconectando, offline, falhou | Faixa de texto com ícone; `role="status"`; não anuncia cada tentativa | 05 |
| Decisão privada | Sem escolha, escolha salva, alterável, prazo encerrado, conexão formada, salvando, erro | `fieldset` + `legend` com dois rádios ou botões de alternância; texto de privacidade junto | 01, 03 |
| Chat | Lista vazia, carregando histórico, mensagem `sending`/`sent`/`failed`, nova mensagem, histórico longo, indisponível | Lista com `role="log"`; não move foco ao chegar mensagem; composer com label e contador de 2.000 caracteres | 05 |
| Faixa de prazo (proposto) | Normal, aviso (2 min), final (30 s), encerrado | Horário absoluto + restante em minutos; anuncia só nos marcos | 08 |
| Barra da sala (proposto) | Com etapa, com aviso de prazo, reconectando | Contém "Sair" sempre visível; `header` próprio do modo focado | 03, 07 |
| Estado de página (proposto) | `loading`, `empty`, `error`, `offline` | Quatro componentes distintos (spec 13.2); `empty` com uma chamada para ação (RUI cap. 8, "Don't overlook empty states") | 05, 06 |

Componentes de jogo (cartões de escolha, pistas, roteiro) estão em [screens.md](screens.md#experiência-rotas-improváveis)
e seguem as mesmas regras; eles entram no Storybook quando a primeira experiência for implementada.

## Pendências

- Identidade visual final (matiz, fonte, raio): decisão do responsável pelo produto. A direção de ilustração já
  foi escolhida (seção [Ilustração](#ilustração)).
- Tokens propostos (`success`, `warning`, `*-surface`, `surface-raised`, `overlay`, sombras e movimento) entram
  no código junto com o primeiro componente que os usar, com teste de contraste.
- Storybook ainda não existe no repositório (`frontend/.storybook/` está na estrutura da spec 21.1).
