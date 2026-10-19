# Design system

Semântica, tokens, ações, movimento, áudio e inventário de componentes do Duora (spec
[13.3](../product/spec.md#133-design-system-implementável); catálogo
[21.2](../product/spec.md#212-catálogo-de-documentação-interna)). O repositório é a fonte de verdade dos
tokens publicados: os valores vivem em `frontend/src/index.css`, e este documento explica a semântica e a
escala. Nada aqui é identidade visual final (spec 13.3, "ponto de partida de design").

> **Legenda de status:** **implementado** = existe no código e tem teste; **proposto** = decisão de design
> para revisão, ainda sem código. Todo token proposto entra com o teste de contraste correspondente em
> `frontend/src/app/design-tokens.test.ts`.

Fontes: Refactoring UI (RUI) caps. 2 a 8; CSS in Depth (CID) caps. 2, 11, 12 e 15; Designing Interfaces
(DI) caps. 8, 10 e 11; The Game Audio Strategy Guide (GASG) caps. 3, 5, 8 e 12; The Definitive Game Narrative
Guide (NG) caps. 5 e 12. Notas de regra da casa em `~/.fonte externa/knowledge/ui-design.md` não são citadas como fonte
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

## Áudio (proposto)

Análogo a Movimento: som só com finalidade (situar o ambiente, confirmar a própria ação, dar voz a um
personagem), com variante sem som que não perde nada. Regras de produto em [ADR-0027](../architecture/adr/0027-midia-das-experiencias-cenario-audio-e-3d.md) e spec 6.5;
nada aqui está implementado.

| Canal | Conteúdo | Volume inicial (hipótese) | Regra |
|---|---|---|---|
| `music` | Trilha do ambiente, em loop curto | Baixo | Some ou vira silêncio nas etapas de foco (escolha, ajuste) e na decisão privada (GASG cap. 5, pp. 143–144) |
| `ambience` | Som do lugar (vento, água, salão) | Baixo | Coerente com a paleta visual do one pager; "menos é mais" (GASG cap. 3, pp. 52–54) |
| `voice` | Falas pré-geradas do catálogo e, quando houver, a voz ao vivo da outra pessoa (com volume próprio) | Médio | Ducking: música e ambiente ≥ 20 dB abaixo da voz enquanto ela soa (meta WCAG 1.4.7) |
| `ui` | Confirmação da própria ação | Baixo | Um som, um significado (som "icônico", GASG cap. 12, pp. 384–385); sempre junto de texto ou estado visível |

- **Mudo por padrão.** Nada toca antes de um gesto no Controle de som (WCAG 1.4.2). "Som ligado" lembrado no
  aparelho só volta a tocar depois do primeiro gesto da página.
- **Preferências no aparelho:** ligado/desligado, volume de cada canal e **"Reduzir áudio"** (desliga `music` e
  `ambience`). Guardadas em `localStorage` como preferência, validadas como dado externo; nunca no servidor.
- **Transcrição:** toda fala aparece em texto na tela, no idioma ativo, com quem fala e o rótulo "voz
  sintética" quando for; a introdução da experiência avisa com clareza que as vozes são de produção editorial. A transcrição não é região viva (o leitor de tela não a lê duas vezes por cima do
  áudio); fica alcançável por título.
- **Paleta sonora** por ambiente, escolhida antes de gerar, coerente com a paleta visual e o tom do brief
  (GASG cap. 3, pp. 51–52): cor tonal unificada, sem misturar realismo e lo-fi.
- **Nunca:** som que reage ao prazo (tique-taque, música acelerando, alarme); som de recompensa ligado a
  pessoas (conexão formada, interesse); som disparado por estado privado da outra pessoa; som na decisão
  privada além do silêncio ou do ambiente neutro; TTS ao vivo, nome de participante ou texto do chat em voz.
- **Falas:** até 120 caracteres por idioma; categoria de bark com pelo menos 3 variações e fala distinta com
  peso raro (NG cap. 12, pp. 181–183); nenhuma fala comenta a outra pessoa.
- **Formato e peso:** AAC-LC (`.m4a`), loops por Web Audio, orçamento por experiência e nenhum áudio no
  caminho crítico do LCP (ADR-0027, seção 4).

## Ilustração, cenário e personagem

Duas famílias de imagem, as duas em WebP estático com direitos registrados (spec 13.3, "Ilustração e
fotografia"):

- **Objetos simbólicos** nas telas de produto (onboarding, estado vazio, cabeçalho de tela), com dois tons
  recoloridos para `action` e `text-muted`. Componente e assets **implementados**
  (`frontend/src/shared/ui/illustration.tsx`); a colocação nas telas entra com cada tela.
- **Cenários e personagens** nas experiências (proposto, [ADR-0027](../architecture/adr/0027-midia-das-experiencias-cenario-audio-e-3d.md)), cada um a partir de um
  [one pager](one-pager-template.md). Podem vir de 3D renderizado offline (fonte externa ou fonte externa) e, por experiência,
  de 3D em tempo real com fallback 2D.

> **Superseded by ADR-0027 (2026-10-19):** a decisão anterior, "objetos simbólicos, **nenhuma pessoa, rosto,
> mão, parte do corpo ou silhueta humana**, nem realista nem estilizada", caiu. O texto original fica aqui como
> registro: "A spec 13.3 proíbe sugerir identidade falsa ou presença fictícia, e o brief pede ilustração sem
> casal-padrão; objeto não tem gênero, idade nem corpo."

### Regra de conteúdo

- **Personagens permitidos**, inclusive humanos estilizados, nunca fotorrealistas nem parecidos com pessoa real
  identificável. Todo personagem é adulto e claramente fictício, e nunca aparece no lugar de avatar, perfil,
  participante ou "alguém esperando você" (spec 13.3: não sugerir presença fictícia).
- **Diversidade sem estereótipo:** o elenco de cada experiência varia em tom de pele, corpo, idade adulta e
  apresentação de gênero; sem casal-padrão, sem sexualização, sem papel preso a um estereótipo
  ([brief](../product/experience-brief.md#restrições-spec-131)).
- **Nenhum texto, letra, número, símbolo ou logo** dentro da imagem: texto vem do catálogo de idiomas, ao lado.
- O prefixo de prompt em `tools/visual/catalog.json` carrega essas proibições de texto, e
  `frontend/config/illustrations.test.ts` (job `frontend` do CI) e `tools/visual/test_catalog.py` (job `ops`)
  conferem que elas continuam lá. Fonte que viola a regra é descartada e gerada de novo, nunca retocada.
- Só ferramentas aprovadas (imagem: fonte externa, via fonte externa; 3D: fonte externa ou fonte externa) e só com
  linha em [assets-licenses.md](assets-licenses.md). Processo em [tools/visual/README.md](../../tools/visual/README.md).

### Uso

| Uso (`catalog.json`) | Tamanho CSS | Onde |
|---|---|---|
| `onboarding` | 240 px | Boas-vindas e primeira visita |
| `experience` | 200 px | Introdução e conclusão de uma experiência |
| `emptyState` | 160 px | Estado vazio de lista (spec 13.2) |
| `screen` | 128 px | Cabeçalho de tela de conteúdo |

- Fora das experiências, no máximo uma ilustração por tela. Nas telas de experiência, o cenário conta como
  fundo de contexto e pode ter **um** personagem em cena; nenhuma outra ilustração na mesma tela.
- Nenhuma imagem é o único portador de informação (RUI cap. 5, "Don't rely on color alone", vale também para
  imagem); regra, pista e escolha ficam em texto.
- `<Illustration name alt>` quando a imagem acrescenta algo ao texto ao lado; `decorative` quando só enfeita
  (`alt=""` e `aria-hidden`). `alt` em branco sem `decorative` falha no render.
- Largura e altura fixas pelo uso, para não haver layout shift; `max-w-full` reduz no mobile.

### Cenário e personagem nas experiências (proposto)

- O cenário é uma faixa no topo da área da experiência, com altura máxima de ~30% da viewport no mobile
  (hipótese), para a etapa e a ação primária continuarem visíveis sem rolar.
- Texto nunca fica sobre a imagem sem superfície sólida por trás (RUI cap. 7, texto sobre imagem); o contraste
  é medido contra a superfície, não contra a foto.
- `alt` curto descreve o ambiente quando ele acrescenta algo; senão `decorative`. Pista narrativa do cenário
  (narrativa ambiental, NG cap. 5, pp. 70–71) é decorativa: nada que a regra exija mora só nela.
- 3D em tempo real só onde o one pager pedir; o poster WebP aparece primeiro e é o fallback (sem WebGL,
  "Reduzir movimento" ou falha); nenhuma ação exige girar ou arrastar o modelo.

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
| Controle de som (proposto) | Mudo (padrão), ligado, por canal (música, ambiente, voz), "Reduzir áudio", sem suporte de áudio | Botão de alternância com `aria-pressed` e rótulo textual ("Som desligado"/"Som ligado"); volumes por `<input type="range">` com label; na Barra da sala, sempre alcançável por teclado; preferência lembrada no aparelho | 04, 07 |
| Transcrição de fala (proposto) | Vazia, fala atual, histórico curto, voz editorial | Texto com quem fala; não é região viva; aviso "voz editorial" junto | 04 |
| Cenário de experiência (proposto) | Imagem, imagem + personagem, carregando (dimensões reservadas), 3D com poster, 3D indisponível | `alt` ou `decorative`; nada da regra só na imagem; sem layout shift | 04, 06 |
| Voz ao vivo (proposto) | Desligada (padrão), pedindo permissão, permissão negada, microfone aberto, mudo, push-to-talk, falando (eu/outra pessoa), outra pessoa silenciada, reconectando, indisponível | Indicador de microfone aberto e de quem fala em texto além do ícone; "Silenciar a outra pessoa", "Desligar meu microfone", "Sair da voz", "Bloquear" e "Denunciar" com rótulo; nada depende de ouvir | 03, 04, 05 |
| Faixa de prazo (proposto) | Normal, aviso (2 min), final (30 s), encerrado | Horário absoluto + restante em minutos; anuncia só nos marcos | 08 |
| Barra da sala (proposto) | Com etapa, com aviso de prazo, reconectando | Contém "Sair" e o Controle de som sempre visíveis; `header` próprio do modo focado | 03, 07 |
| Estado de página (proposto) | `loading`, `empty`, `error`, `offline` | Quatro componentes distintos (spec 13.2); `empty` com uma chamada para ação (RUI cap. 8, "Don't overlook empty states") | 05, 06 |

Componentes de jogo (cartões de escolha, pistas, roteiro) estão em [screens.md](screens.md#experiência-rotas-improváveis)
e seguem as mesmas regras; eles entram no Storybook quando a primeira experiência for implementada.

## Pendências

- Identidade visual final (matiz, fonte, raio): decisão do responsável pelo produto. A direção de ilustração já
  foi escolhida (seção [Ilustração, cenário e personagem](#ilustração-cenário-e-personagem)).
- Áudio: fonte externa Creator (voz e música), Freesound CC0 e Sonniss (efeitos), conforme ADR-0027; nada gerado ainda.
- Voz ao vivo: arquitetura decidida no ADR-0028; componente ainda não implementado.
- Tokens propostos (`success`, `warning`, `*-surface`, `surface-raised`, `overlay`, sombras e movimento) entram
  no código junto com o primeiro componente que os usar, com teste de contraste.
- Storybook ainda não existe no repositório (`frontend/.storybook/` está na estrutura da spec 21.1).
