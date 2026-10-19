# ADR-0027: Mídia das experiências: cenário, personagem, áudio e 3D

- **Status:** Accepted (2026-10-19), decisões do responsável pelo produto em 2026-10-19. A voz ao vivo entre as
  duas pessoas tem ADR próprio: [ADR-0028](0028-voz-ao-vivo-webrtc-relay-cloudflare-turn.md).
- **Origem:** spec 1.1, 6.1, 6.3, 6.4, 13.1, 13.3 a 13.7, 16.3 e 23.2; [ADR-0006](0006-mesma-origem-spa-api-hub.md),
  [ADR-0010](0010-object-storage-externo-leitura-via-api.md), [ADR-0013](0013-ia-fora-do-caminho-critico.md),
  [ADR-0020](0020-persistencia-da-sessao-de-jogo.md), [ADR-0022](0022-i18n-intl-messageformat-catalogo-tipado.md);
  [brief](../../product/experience-brief.md) (O4 e restrições); [system.md](../../design/system.md)
- **Revisar quando:** a primeira experiência pedir 3D em tempo real, o orçamento de mídia medido estourar a
  hipótese, o plano ou os termos de ElevenLabs, Freesound ou Sonniss mudarem, ou entrar um terceiro idioma
- **Substitui em parte:** a direção "objetos simbólicos, sem pessoa" de [system.md](../../design/system.md#ilustração-cenário-e-personagem)
  (marcada como Superseded lá), o prefixo de prompt v1 de [assets-licenses.md](../../design/assets-licenses.md) e
  a linha "Experiências" da spec 1.1, que tirava 3D e voz do escopo. Nenhum ADR anterior é revogado: os ADRs
  0013 (IA fora do caminho crítico) e 0020 (conteúdo pela versão fixada) passam a valer também para a mídia.

## Contexto

A spec deixava 3D e voz fora da primeira entrega, e o design system proibia qualquer pessoa ou silhueta nas
ilustrações. O responsável decidiu que as experiências terão cenários e personagens (inclusive humanos
estilizados), trilha sonora por ambiente, vozes naturais pré-geradas e, por experiência, 3D. Decidiu também que
as duas pessoas da sala poderão conversar por voz ao vivo dentro da experiência. Isso mexe em quatro coisas
caras de reverter: o que entra na versão imutável da experiência (INV-09), o caminho crítico de carregamento
(spec 13.5), a acessibilidade (WCAG 2.2 AA) e a segurança interpessoal, porque voz identifica e expõe.

Fontes de ofício usadas: *The Definitive Game Narrative Guide* (Kindregan e Lasrado, CRC Press; "NG") e *The
Game Audio Strategy Guide* (Zdanowicz e Bambrick, Routledge; "GASG"). Páginas são as impressas.

| Princípio | Fonte | Como vira regra aqui |
|---|---|---|
| Narrativa ambiental: o cenário conta parte da história por pistas que a pessoa junta | NG cap. 2, pp. 39–40; cap. 5, pp. 70–71 | O cenário carrega o tom e pistas decorativas; a regra e a pista privada continuam em texto (INV-05, A11Y-04) |
| Fala curta para voz: frase longa vira duas; limite de caracteres por fala (120 em *Mass Effect 2*) | NG cap. 11, p. 171 | Toda fala tem no máximo 120 caracteres **em cada idioma** |
| Barks: clareza antes de arte, entendidos sem contexto, várias variações por categoria, falas distintas com peso raro | NG cap. 12, pp. 179–183 | Categoria com pelo menos 3 variações; fala distinta com sorteio de baixo peso; nenhuma fala comenta a outra pessoa |
| One pager por personagem e por região como insumo comum da equipe | NG cap. 22, pp. 284–288 | Template de one pager de ambiente e de personagem, insumo comum de arte e áudio ([one-pager-template.md](../../design/one-pager-template.md)) |
| Roteiro mestre: cada fala é um asset rastreado com metadados; casting com arte, referência de voz e falas de amostra | GASG cap. 4, pp. 119–120 | As falas vivem no conteúdo da versão, com id, texto por idioma, personagem e intensidade |
| Paleta sonora escolhida antes, como a paleta de cor; cor tonal unificada | GASG cap. 3, pp. 51–52 | Paleta sonora por ambiente no one pager, coerente com a paleta visual e os tokens |
| Menos é mais; música esparsa; ruído demais esconde a informação | GASG cap. 3, pp. 52–54 | Música mínima quando a tarefa pede foco; nem todo elemento faz som |
| Música de quebra-cabeça sutil, que não distrai; a música serve à jogabilidade | GASG cap. 5, pp. 143–144; cap. 9, p. 330 | Na etapa de escolha, só ambiente baixo ou silêncio |
| Diálogo crítico sempre inteligível por ducking ou snapshot | GASG cap. 8, pp. 288–289 | Voz (pré-gerada ou ao vivo) abaixa música e ambiente |
| Orçamento de áudio em mobile/web; Ogg × AAC no iOS; MP3 tem lacuna em loop; mono economiza | GASG cap. 8, pp. 301–303 | Orçamento por experiência; AAC-LC como formato obrigatório; loop por Web Audio |
| LUFS mede volume percebido; normalizar arquivos curtos por LUFS não funciona bem | GASG cap. 4, p. 134 | Loudness integrado para loops longos; pico para efeitos curtos |
| Som "icônico": o mesmo som sempre significa a mesma coisa | GASG cap. 12, pp. 384–385 | Cada som de interface tem um significado só, e nunca é o único sinal |
| Hostilidade contra jogadoras identificadas pela voz | GASG cap. 12, p. 383 | Voz ao vivo desligada por padrão, silenciar e sair sempre à mão, denúncia a partir da voz |

Alternativas consideradas:

- **3D: fonte offline renderizada em WebP** (escolhida como padrão). Modelos gerados no Rodin ou no Tripo,
  compostos e renderizados fora do produto; o pipeline de `tools/visual` entrega WebP. Zero JavaScript novo,
  peso previsível, mesmo tratamento de acessibilidade das imagens. Contra: sem interação com o objeto.
- **3D em tempo real com `<model-viewer>`** (Apache-2.0). Web component sobre three.js, com `alt`, controle
  por teclado, `poster` e carregamento sob interação. Contra: dependência grande, WebGL e, com geometria
  comprimida (Draco, meshopt) ou textura KTX2, decodificador em WebAssembly, que a CSP atual bloqueia.
- **3D em tempo real com three.js direto** (MIT). Controle total. Contra: acessibilidade, fallback e teclado
  ficam por nossa conta.
- **3D em tempo real como padrão.** Descartada: põe JavaScript pesado e GPU no caminho de toda experiência,
  para um ganho que as tarefas (escolher, confirmar, conversar) não pedem.
- **TTS ao vivo** (gerar a fala na hora, inclusive com nome de participante ou texto do chat). Descartada:
  viola o ADR-0013 (IA fora do caminho crítico), envia dado de participante a fornecedor e impede revisão
  editorial da fala.
- **Mídia do catálogo em object storage** (ADR-0010). Descartada para o catálogo: o ADR-0010 trata mídia de
  pessoas, privada e lida pela API; a mídia do catálogo é pública, não pessoal e versionada com o conteúdo.
- **Voz ao vivo:** alternativas e decisão em [ADR-0028](0028-voz-ao-vivo-webrtc-relay-cloudflare-turn.md).

## Decisão

### 1. Escopo

Vamos incluir nas experiências cenários e personagens ilustrados, trilha por ambiente, som ambiente, efeitos de
interface, vozes pré-geradas com transcrição e 3D (offline por padrão, em tempo real por experiência). Vamos
incluir também a voz ao vivo entre as duas pessoas da sala, dentro da experiência (seção 6 e ADR-0028).
Continuam fora: vídeo, chamada de voz fora da sala (na conversa persistente), scripts arbitrários e qualquer
geração ao vivo (imagem, música ou TTS).

### 2. Cenário e personagem

- Personagens são permitidos, inclusive humanos estilizados, nunca fotorrealistas e nunca parecidos com uma
  pessoa real identificável. Todo personagem é adulto e claramente fictício: nunca ocupa o lugar de avatar,
  perfil, participante ou "alguém esperando você" (spec 13.3, "não sugerir presença fictícia").
- O elenco de cada experiência varia em tom de pele, corpo, idade adulta e apresentação de gênero; nada de
  casal-padrão, sexualização nem papel estereotipado ([brief](../../product/experience-brief.md#restrições-spec-131)).
- Nenhum texto dentro da imagem; texto vem do catálogo de idiomas.
- Cada ambiente e cada personagem nasce de um one pager, que é o insumo comum de arte, som e roteiro. O
  catálogo recusa personagem sem one pager, e cada elenco passa pela checklist de revisão de diversidade de
  [one-pager-template.md](../../design/one-pager-template.md#checklist-de-revisão-de-elenco).
- Quando o prefixo de cenário e personagem entrar em `tools/visual`, um teste exige nele "adult", "stylized, not
  photorealistic", "non-sexualized" e "no text".

### 3. Áudio pré-gerado

- **Mudo por padrão.** Nenhum som toca antes de um gesto explícito no "Controle de som" (WCAG 1.4.2; política de
  autoplay dos navegadores). Preferência "som ligado" guardada no aparelho vale a partir do primeiro gesto da
  página, nunca no carregamento.
- **Três canais com volume próprio:** música, ambiente e voz, lembrados no aparelho (`localStorage`, só
  preferência, validada como dado externo). Preferência **"Reduzir áudio"**: desliga música e ambiente e deixa só
  voz e sons de interface.
- **Ducking:** enquanto houver voz (pré-gerada ou ao vivo), música e ambiente ficam pelo menos 20 dB abaixo
  dela (meta de projeto WCAG 1.4.7, AAA), com transição sem degrau audível (GASG pp. 288–289).
- **Transcrição na tela** de toda fala, no idioma ativo (WCAG 1.2.1), com a identificação de quem fala. A
  transcrição é o mesmo texto do catálogo que gerou o áudio: uma fonte só.
- **Aviso de voz sintética** em dois lugares: um aviso claro na introdução da experiência ("As vozes desta
  experiência são geradas por IA") e o rótulo junto da transcrição de cada fala. Aplicabilidade do art. 50 do
  EU AI Act a confirmar (lacuna J-10 do PR #83); voz de referência de residente de Illinois entra na lacuna J-04.
- **Nenhuma reação sonora ao prazo:** sem tique-taque, sem música que acelera, sem alarme nos marcos (O4 do
  brief). O marco é texto, anunciado uma vez (A11Y-08).
- **Decisão privada em silêncio** ou com som neutro do ambiente, sem nenhum som ao salvar, ao formar conexão ou
  ao expirar.
- **Nenhum som de recompensa ligado a pessoas.** Som de interface confirma a ação da própria pessoa ("escolha
  registrada") e tem um significado só; nenhum som é disparado por estado privado da outra pessoa (INV-05).
- **Falas só do catálogo,** pré-geradas e revisadas: nunca TTS ao vivo, nunca nome de participante, nunca texto
  do chat (ADR-0013). No máximo 120 caracteres por fala em cada idioma; barks com pelo menos 3 variações por
  categoria; fala distinta com peso raro; nenhuma fala comenta a escolha, a aparência ou a compatibilidade da
  outra pessoa.
- **Voz de referência humana** só com consentimento e licença documentados em
  [assets-licenses.md](../../design/assets-licenses.md).
- **Música mínima** quando a etapa pede foco (escolha, ajuste, decisão): ambiente baixo ou silêncio.
- **Fornecedores (decisão do responsável, 2026-10-19):** **ElevenLabs, plano Creator**, para voz (TTS) e música;
  **Freesound, só CC0**, e **Sonniss** para efeitos. Caminho único: texto revisado do catálogo → geração
  offline na saída de maior qualidade que o plano permitir → edição, mix e normalização → **uma única**
  codificação AAC-LC → hash → registro em [assets-licenses.md](../../design/assets-licenses.md) → nova versão de
  conteúdo. Lacuna a confirmar: pela API, o Creator pode entregar só MP3; nesse caso o MP3 é a fonte, e o
  AAC-LC sai dele uma vez, sem nova recodificação.
- **Segredo:** `ELEVENLABS_API_KEY` só no ambiente de quem gera, nunca no repositório, em CI, log ou commit; o
  MCP hospedado da ElevenLabs usa OAuth (metadados em <https://api.us.elevenlabs.io/.well-known/oauth-protected-resource>; fluxo a confirmar no primeiro uso) e não precisa de
  chave no repositório.
- **Riscos de licença:**
  - Sonniss proíbe redistribuir som avulso e o repositório é público: versiona-se só o **mix final** em que o
    efeito entra, nunca o arquivo original (termo exato a confirmar na licença do pacote).
  - Freesound: só CC0, e nenhuma gravação com voz identificável.
  - Voice Library da ElevenLabs: registrar o `voice_id` e o prazo de retirada da voz pela autora; voz clonada de
    pessoa real só com consentimento assinado.

### 4. Formatos, orçamento e carregamento

Os números são **hipóteses** (convenção da spec: valor a medir), revistos com a primeira experiência real.

| Item | Regra |
|---|---|
| Formato de áudio obrigatório | AAC-LC em MP4 (`.m4a`), 48 kHz, para Safari/iOS, Chrome e Edge. O Firefox no Linux depende do decodificador do sistema: o cliente detecta suporte (`canPlayType`/`decodeAudioData`) e usa Opus em WebM como fallback, gerado da mesma fonte |
| Voz | Mono, ~64 kbit/s (hipótese) |
| Música e ambiente | Estéreo, ~128 kbit/s (hipótese); loops curtos (≤ 60 s) em vez de faixas longas (GASG p. 301) |
| Loop sem lacuna | Por Web Audio, com `loopStart`/`loopEnd` do manifesto descontando o atraso do codificador; nunca MP3 em loop (GASG p. 303) |
| Loudness | Loops longos com loudness integrado de referência (−16 LUFS, hipótese) e pico ≤ −1 dBTP; efeitos curtos por pico, não por LUFS (GASG cap. 4, p. 134) |
| Orçamento de áudio | ≤ 3 MB por experiência e idioma (hipótese), somando música, ambiente e vozes |
| Orçamento de imagem de cenário | ≤ 250 KB por cenário a 2x (hipótese), WebP, dimensões fixas. A imagem de cenário provavelmente é o elemento LCP da rota da experiência: `srcset` com variante móvel, `fetchpriority="high"` só nessa rota e orçamento medido em laboratório (spec 13.5) |
| Orçamento de 3D em tempo real | ≤ 1,5 MB por modelo GLB (hipótese), mais o custo do visualizador medido no build |
| Caminho crítico | Nenhum áudio nem modelo 3D no caminho do LCP: áudio só é baixado depois que a pessoa liga o som; 3D em tempo real carrega depois da primeira renderização, por interação ou visibilidade, sempre com o poster WebP primeiro |

### 5. Versionamento

A mídia é conteúdo da `ExperienceVersion` (spec 6.1; ADR-0020). O conteúdo da versão ganha um **manifesto de
mídia**; cada entrada tem `assetId`, tipo (`image`, `audio`, `model3d`), idioma quando houver, SHA-256 do arquivo,
origem (`human`, `ai_assisted`), ferramenta, modelo, id da tarefa de geração, licença e, para voz, a referência
ao registro de consentimento. O `content_sha256` da versão cobre o manifesto, e por ele cada arquivo.

- Os arquivos são estáticos da mesma origem (ADR-0006), com nome derivado do hash, servidos pelo Caddy com cache
  imutável, como os assets da SPA. Arquivo de versão publicada não é apagado enquanto a versão não for
  `Retired` e nenhuma sessão a usar.
- Mudar o texto de uma fala exige gerar o áudio de novo e publicar versão nova; sessões em andamento continuam na
  versão antiga (INV-09, T-09).
- Os "modos de acessibilidade" da versão (spec 6.1) passam a incluir **som desligado** e **transcrição**; uma
  versão só publica se for completável com som desligado.
- Voz e transcrição existem em `pt-BR` e `en-US`. A versão só publica com os dois idiomas completos, com o mesmo
  conjunto de falas.

### 6. Voz ao vivo entre as duas pessoas

Regras de produto, arquitetura (WebRTC P2P com relay forçado na Cloudflare Realtime TURN), credenciais,
sinalização e testes estão no [ADR-0028](0028-voz-ao-vivo-webrtc-relay-cloudflare-turn.md). Do lado da mídia do catálogo, a voz ao vivo entra no canal `voice`, com
volume próprio para a outra pessoa, e dispara o mesmo ducking da trilha.

### 7. 3D

Vamos usar 3D como **fonte offline** por padrão: modelos do Rodin (Hyper3D, plano Business) ou do Tripo, compostos
e renderizados fora do produto e entregues em WebP pelo pipeline. **3D em tempo real** (GLB) é permitido por
experiência, decidido no one pager do ambiente, com orçamento de peso e **fallback 2D acessível** (o poster WebP do
mesmo modelo, com o mesmo texto alternativo). Recomendação para o tempo real: `<model-viewer>`, pela acessibilidade
embutida (`alt`, teclado, `poster`, `reveal="interaction"`); three.js direto só se a interação pedida não couber
nele. A dependência entra no PR da primeira experiência que a usar, com justificativa, licença e medição de bundle
(AGENTS.md, spec 2.3). Regras do tempo real:

- nada da regra, da pista ou da escolha mora só no modelo; nenhuma ação exige arrastar ou girar (A11Y-04);
- sem rotação automática com `prefers-reduced-motion: reduce`; nada pisca;
- WebGL indisponível, falha de carregamento ou "Reduzir movimento" mostram o poster, sem perda de função;
- geometria e texturas sem decodificador WebAssembly até a CSP ser revista no PR de infra; o `<model-viewer>`
  carrega texturas por `blob:` (a confirmar na versão adotada), o que pede `img-src blob:` (e, com decodificador, `worker-src blob:` e
  `'wasm-unsafe-eval'`) nesse mesmo PR.

Porquê técnico: a mídia entra no mesmo contrato de versão imutável que já protege regras e conteúdo, fica fora do
caminho crítico e herda os controles de acessibilidade existentes. Porquê de negócio: ambientes e vozes dão
personalidade às experiências sem comprometer acolhimento, privacidade e saída fácil, que são o produto.

## Consequências

- Positivas: a experiência ganha atmosfera sem mudar regras; toda mídia é rastreável por hash e licença;
  sessões antigas não mudam de som nem de imagem no meio; quem não ouve ou não quer som joga igual.
- Negativas: cada mudança de fala exige regerar dois idiomas e publicar versão; o repositório passa a ter
  binários de áudio (peso do Git a acompanhar; Git LFS fica para quando o peso pedir); a voz ao vivo tem custo e
  riscos próprios, tratados no ADR-0028.
- Trade-off: menos variedade ao vivo (sem TTS na hora) em troca de revisão editorial, privacidade e
  disponibilidade.

## Segurança e operação

- Mídia do catálogo é pública e não pessoal; nenhum dado de participante entra em prompt, roteiro ou referência
  de voz (spec 6.4). Voz de referência humana só com consentimento e licença registrados.
- Arquivo de mídia é conferido pelo hash do manifesto no build: um teste de build confere que todo arquivo citado
  por versão não aposentada existe no bundle com o hash certo; arquivo trocado ou ausente falha o build.
- Voz ao vivo: STRIDE em [threat-model.md](../../security/threat-model.md#512-voz-ao-vivo-na-sala) (TM-68 a
  TM-79) e ADR-0028.
- **Dependências:** CSP num PR de infra separado: `media-src 'self'` para `<audio>`; o `fetch` dos arquivos que
  a Web Audio decodifica cai em `connect-src` (já `'self'`); 3D em tempo real pede `img-src blob:` e, com
  WebAssembly, `worker-src blob:` e `'wasm-unsafe-eval'`; público, idiomas e região no
  ADR-0026 (PR #83, em revisão).

## Compliance

- `tools/visual/test_catalog.py` e `frontend/config/illustrations.test.ts` (jobs `ops` e `frontend` do CI): o prefixo de
  prompt proíbe texto, letras, números, símbolos e logos; pessoas não são mais proibidas; o prefixo registrado
  em `assets-licenses.md` é igual ao de `catalog.json`.
- A criar com o primeiro cenário: teste do prefixo de cenário e personagem ("adult", "stylized, not
  photorealistic", "non-sexualized", "no text") e teste de build de arquivos de mídia por versão não aposentada.
- A criar com a primeira experiência com mídia (M2): teste do manifesto (todo asset tem hash que confere com o
  arquivo, licença, origem e os dois idiomas; fala ≤ 120 caracteres; categoria de bark com ≥ 3 variações);
  T-09 estendido à mídia (sessão antiga continua com os arquivos da versão antiga).
- A criar no frontend (M2): nenhum `AudioContext` nem elemento de áudio toca antes do gesto no Controle de som;
  nenhum som é disparado pelo prazo nem na decisão privada; preferência de canais lida como dado externo; build
  sem áudio no chunk inicial nem `<link rel="preload">` de áudio.
- A11Y-04 do [protocolo](../../design/accessibility-test-protocol.md#a11y-04--minijogos): jogo completo com som
  desligado, transcrição, Controle de som por teclado e leitor de tela, fallback 2D do 3D.
- Voz ao vivo: testes do ADR-0028 e de TM-68 a TM-79.
