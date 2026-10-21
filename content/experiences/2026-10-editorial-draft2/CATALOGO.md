# Catálogo editorial bilíngue para revisão

Primeira entrega desta tarefa, em 21/10/2026. Rascunho sem aprovação, sem áudio produzido e sem publicação. Adapta o candidato já presente na main consultada, preservando os IDs. `contentVersion: 2` é uma proposta local para distinguir a revisão; não é UUID publicado.

## Como ler

Os JSONs de cada experiência contêm somente contentVersion, enUS e ptBR. Os campos internos correspondem aos contratos existentes. Não são requisições completas de publicação: faltam áudio real, hashes e aprovação. Instruções complementares e metadados de produção estão em arquivos separados.

## Direção editorial

Duas pessoas adultas exploram escolhas cotidianas ou constroem um passeio fictício. Nenhuma resposta vale mais, mede afinidade ou pede intimidade. O som é dispensável. Nesta versão não há personagens: a voz é narração da interface, nunca uma terceira pessoa presente. Não se exige elenco para um catálogo sem personagens. Qualquer personagem futuro deverá ser adulto, fictício, estilizado, sem sexualização e ter one pager e revisão de diversidade.

## Constelação de Escolhas

| Campo | PT-BR | EN-US |
|---|---|---|
| Título | Constelação de Escolhas | Constellation of Choices |
| Objetivo e transcrição de intro | Explore escolhas do dia a dia. Você pode pular qualquer cartão. | Explore everyday choices. You can skip any card. |

### small-pause

| Campo ou ID | PT-BR | EN-US |
|---|---|---|
| Pergunta | Imagine uma tarde livre. Qual pequena pausa você escolheria? | Imagine a free afternoon. Which small break would you pick? |
| read | Ler algumas páginas | Read a few pages |
| make | Criar algo à mão | Make something by hand |
| listen | Ouvir música | Listen to music |
| notice | Olhar pela janela | Look out the window |

### shared-table

| Campo ou ID | PT-BR | EN-US |
|---|---|---|
| Pergunta | Imagine um encontro em um centro cultural. O que você colocaria na mesa? | Imagine a gathering at a cultural center. What would you put on the table? |
| draw | Papel e lápis de cor | Paper and colored pencils |
| play | Um jogo de tabuleiro simples | A simple board game |
| stories | Histórias curtas para compartilhar | Short stories to share |
| puzzle | Um quebra-cabeça para montar em conjunto | A puzzle to work on together |

### curiosity

| Campo ou ID | PT-BR | EN-US |
|---|---|---|
| Pergunta | Um centro comunitário fictício oferece uma oficina curta. Qual você exploraria? | A fictional community center offers a short workshop. Which would you explore? |
| sound | Explorar sons do cotidiano | Explore everyday sounds |
| plants | Cultivar ervas em vasos | Growing herbs in pots |
| print | Criar uma gravura simples | Make a simple print |
| sky | Ler um mapa do céu | Reading a map of the sky |

### closing-note

| Campo ou ID | PT-BR | EN-US |
|---|---|---|
| Pergunta | Um dia imaginário está terminando. Qual detalhe você guardaria como lembrança? | An imaginary day is ending. Which detail would you keep as a reminder? |
| color | Uma paleta de cores | A color palette |
| line | Uma frase de uma história | A line from a story |
| rhythm | Um ritmo curto | A short rhythm |
| shape | Uma forma desenhada no papel | A shape sketched on paper |
## Rotas Improváveis

| Campo | PT-BR | EN-US |
|---|---|---|
| Título | Rotas Improváveis | Unlikely Routes |
| Objetivo e transcrição de intro | Planejem um passeio por uma cidade em uma ilha imaginária. | Plan an outing through a town on an imaginary island. |
| Exemplo e instrução pública | Cada pessoa recebe pistas diferentes. Compartilhe o que quiser no idioma combinado. Alternem as três escolhas. Depois, ambos confirmam a rota. Pedir revisão, se disponível, apaga toda a rota. Não há rota certa nem necessidade de informar localização real. | Each person gets different clues. Share what you want in your agreed language. Take turns making three choices. Then both confirm the route. Requesting a revision, if available, clears the whole route. There is no correct route or need to share a real location. |

### meeting-place

| Campo ou ID | PT-BR | EN-US |
|---|---|---|
| Pergunta | Onde a rota imaginária deve começar? | Where should the imaginary route begin? |
| garden | No jardim das lanternas | The lantern garden |
| workshop | Na oficina de papel | The paper workshop |
| observatory | No observatório das nuvens | The cloud observatory |

### crossing

| Campo ou ID | PT-BR | EN-US |
|---|---|---|
| Pergunta | Como a rota deve atravessar a ilha? | How should the route cross the island? |
| bridge | Pela ponte de madeira | Across the wooden bridge |
| ferry | Na pequena balsa | On the little ferry |
| path | Pela trilha sombreada | Along the shaded path |

### last-stop

| Campo ou ID | PT-BR | EN-US |
|---|---|---|
| Pergunta | Onde a rota imaginária deve terminar? | Where should the imaginary route end? |
| lookout | No mirante do pôr do sol | At the sunset lookout |
| library | Na biblioteca ao ar livre | At the open-air library |
| courtyard | No pátio dos mosaicos | In the mosaic courtyard |

### Pistas privadas do conjunto 0

Somente a pessoa à qual este conjunto for atribuído recebe estas pistas. As posições são iguais nos dois idiomas; o contrato não possui clueId.

| Posição e parada relacionada | PT-BR | EN-US |
|---|---|---|
| 0 · meeting-place | O jardim das lanternas tem bancos sob as árvores. A oficina de papel tem uma grande mesa compartilhada. | The lantern garden has benches under trees. The paper workshop has a large shared table. |
| 1 · crossing | A ponte oferece uma vista aberta. A trilha sombreada tem vários lugares para fazer uma pausa. | The bridge offers an open view. The shaded path has several places to pause. |
| 2 · last-stop | O mirante tem bancos voltados para o céu do entardecer. O pátio tem uma parede de azulejos coloridos. | The lookout has benches facing the evening sky. The courtyard has a wall of colorful tiles. |

### Pistas privadas do conjunto 1

Somente a pessoa à qual este conjunto for atribuído recebe estas pistas. As posições são iguais nos dois idiomas; o contrato não possui clueId.

| Posição e parada relacionada | PT-BR | EN-US |
|---|---|---|
| 0 · meeting-place | O observatório das nuvens tem um terraço coberto. A oficina de papel expõe mapas feitos à mão. | The cloud observatory has a covered terrace. The paper workshop displays handmade maps. |
| 1 · crossing | A pequena balsa passa por uma cachoeira. A ponte liga duas praças tranquilas. | The little ferry passes a waterfall. The bridge connects two quiet squares. |
| 2 · last-stop | A biblioteca ao ar livre tem livros ilustrados. O pátio tem espaço para desenhar um novo mapa. | The open-air library has illustrated books. The courtyard has space to draw a new map. |

As pistas ampliam as possibilidades sem impor uma resposta correta. Os conjuntos são diferentes, não avaliações de quem participa. A ilha contém uma cidade fictícia, alinhando o cenário ao resumo acessível já existente. Não há localização real, trajeto recomendado fora do jogo ou teste de memória.

## Instruções complementares bilíngues

Textos editoriais para revisão junto da interface existente. Não são novos campos do catálogo nem áudios adicionais. A visibilidade “self” indica mensagem exibida individualmente, sem revelar estado privado de outra pessoa.

| Experiência e etapa | Visibilidade | PT-BR | EN-US |
|---|---|---|---|
| common / sound | public | O som é opcional. Todo o conteúdo necessário está disponível em texto. | Sound is optional. All content needed to play is available as text. |
| common / exit | public | Você pode sair da sala a qualquer momento. Não precisa explicar o motivo. | You can leave the room at any time. You do not need to explain why. |
| common / speaker | public | Narração. | Narration. |
| common / language | public | Usem o idioma combinado para conversar. O jogo não traduz a conversa. | Use your agreed language to talk. The game does not translate your conversation. |
| constellation / cards | public | Escolha uma opção e confirme, ou pule. Conversar sobre o cartão é opcional. | Choose an option and confirm, or skip. Talking about the card is optional. |
| constellation / privacy | public | Sua escolha fica privada até ambos escolherem. Se alguém pular, nenhuma escolha aparece. | Your choice stays private until both choose. If either person skips, neither choice is shown. |
| constellation / waiting | self | Sua resposta foi registrada. O cartão avança quando as duas pessoas responderem. | Your response was recorded. The card advances when both people have responded. |
| constellation / summary | public | O resumo mostra cartões revelados e pulados, sem identificar quem pulou. Não há nota de compatibilidade. | The summary shows revealed and skipped cards without naming who skipped. There is no compatibility score. |
| unlikely-routes / introduction | public | Leia o objetivo e o exemplo. Selecione Entendi. A etapa avança quando ambos confirmarem. | Read the objective and example. Select Got it. The stage advances when both confirm. |
| unlikely-routes / clues | self | Só você vê estas pistas. Compartilhe o que quiser. Selecione Entendi para seguir. | Only you can see these clues. Share what you want. Select Got it to continue. |
| unlikely-routes / choices | public | Na sua vez, marque uma opção e selecione Escolher. Vocês alternam as três escolhas. | On your turn, select an option and choose Choose. Take turns making the three choices. |
| unlikely-routes / other-turn | public | Esta escolha é da outra pessoa. A rota continua visível para vocês. | This choice belongs to the other person. The route remains visible to both of you. |
| unlikely-routes / adjustment | public | Revise a rota. Ambos precisam confirmar. Pedir revisão, se disponível, apaga a rota e reabre as escolhas. | Review the route. Both must confirm. Requesting a revision, if available, clears the route and reopens choices. |
| unlikely-routes / conclusion | public | Esta é a rota que vocês montaram. Ela não indica compatibilidade nem exige continuar o contato. | This is the route you built. It does not indicate compatibility or require further contact. |
| common / keyboard | public | Use Tab para chegar aos controles. Use setas nas opções e Espaço para marcar; ative o botão para confirmar. | Use Tab to reach controls. Use arrows within options and Space to select; activate the button to confirm. |

## Roteiros de áudio

A fala é a transcrição acima, sem adaptação durante a gravação. Somente a cue pública `intro` está prevista neste lote. A descrição acessível transportada pela API permanece igual ao título. O rótulo de narrador consta no roteiro de produção, mas não possui campo próprio na interface atual.

| Experiência | Idioma | Caracteres da fala | Descrição acessível |
|---|---|---|---|
| constellation | pt-BR | 63 | Constelação de Escolhas |
| constellation | en-US | 48 | Constellation of Choices |
| unlikely-routes | pt-BR | 58 | Rotas Improváveis |
| unlikely-routes | en-US | 53 | Unlikely Routes |

A narração apresenta apenas a experiência. A direção de voz é adulta e conversacional: pausas naturais, pronúncia fluente no idioma e calor discreto, sem tom robótico, publicitário ou teatral. A seleção depende de escuta humana. O rótulo editorial é “Narração” / “Narration”; não simula uma terceira pessoa presente.

## Produção e limites

Produzidos nesta entrega: textos bilíngues, opções, pistas, instruções complementares, quatro roteiros/transcrições previstos e metadados de produção. Não produzidos: WAV, AAC, Opus, música, ambientes, efeitos, imagens, personagens e modelos 3D. Nenhuma gravação anterior foi copiada nem teve seu hash reutilizado.

Para cada fala, aprovar o texto e a equivalência primeiro; selecionar voz e documentar direitos; gerar offline com teto de custo e tentativas; ouvir nos dois idiomas e conferir palavra por palavra; exportar WAV PCM canônico; medir duração, tamanho e níveis; calcular SHA-256 dos bytes finais; preencher origem, fornecedor, modelo, conta, geração e registro de licença. Consentimento será exigido se houver referência humana. Ausência de referência deve ser registrada e verificada, não presumida como licença.

O manifesto desta entrega usa valores nulos para fatos ainda inexistentes. Não o enviar ao endpoint. Arquivos e hashes só entram no manifesto de publicação após produção. WAV PCM de 16 bits, 48 kHz e mono é a meta deste lote; o limite operacional é 60 s e 6 MiB por variante. AAC-LC/Opus, canais, ducking, barks, ambiência e 3D pertencem à evolução do ADR, não a este catálogo entregue. Nenhuma pista será narrada no áudio público.

## Aprovações humanas necessárias

1. Responsável editorial: aceitar os temas e a utilidade das escolhas e pistas para duas pessoas adultas; decidir se os quatro cartões e três paradas formam o lote inicial.
2. Revisores PT-BR e EN-US: confirmar naturalidade e equivalência semântica de cada par, inclusive nuances de encontro/gathering, gravura/print e mirante/lookout. A conferência automática não certifica tradução.
3. Produto e acessibilidade: validar a compreensão da revelação bilateral, do pulo neutro, das pistas individuais e do reinício completo na revisão; testar com teclado, leitor de tela e som desligado.
4. Produto e produção: selecionar a interpretação mais natural em cada idioma por escuta, sem confundir naturalidade com origem humana ou aprovação do áudio.
5. Produção e direitos: escolher voz, conferir licença e consentimento aplicável, aprovar escuta e correspondência literal dos quatro arquivos futuros.
6. Após tudo acima, responsável autorizado: registrar pessoa revisora, data com fuso, digest dos textos, hashes dos áudios e ambiente de destino. Somente então considerar publicação administrativa com MFA. Nenhuma dessas aprovações foi registrada por esta tarefa.

IDs e separação privada seguem os contratos e não exigem uma decisão editorial nova. O número de revisões é configuração da sessão; não se fixa um número no texto. A decisão posterior de contato pertence ao fluxo privado do produto, não a uma escolha ou resultado narrado pelo catálogo.

## Fontes e rastreabilidade

Base consultada: commit `6c89d99bf990db780e8ac8bf7c89ae0dc4e6816f` da main. A main já continha um candidato editorial e áudio de revisão quando esta tarefa foi executada; a premissa sobre fixtures descreve o PR #112, mas não todo o conteúdo atual. Os registros anteriores de plano pago, voz e licença não foram adotados como validação deste lote.

- [PR 112](https://github.com/BipoXDXD/duora/pull/112)
- [Issue 101](https://github.com/BipoXDXD/duora/issues/101)
- [docs/architecture/adr/0027-midia-das-experiencias-cenario-audio-e-3d.md](https://github.com/BipoXDXD/duora/blob/6c89d99bf990db780e8ac8bf7c89ae0dc4e6816f/docs/architecture/adr/0027-midia-das-experiencias-cenario-audio-e-3d.md)
- [docs/product/experience-brief.md](https://github.com/BipoXDXD/duora/blob/6c89d99bf990db780e8ac8bf7c89ae0dc4e6816f/docs/product/experience-brief.md)
- [docs/testing/reports/2026-10-21-issue-100.md](https://github.com/BipoXDXD/duora/blob/6c89d99bf990db780e8ac8bf7c89ae0dc4e6816f/docs/testing/reports/2026-10-21-issue-100.md)
- [content/experiences/2026-10-review/README.md](https://github.com/BipoXDXD/duora/blob/6c89d99bf990db780e8ac8bf7c89ae0dc4e6816f/content/experiences/2026-10-review/README.md)
- [backend/src/Duora.Core/Experiences/Constellation/ConstellationContent.cs](https://github.com/BipoXDXD/duora/blob/6c89d99bf990db780e8ac8bf7c89ae0dc4e6816f/backend/src/Duora.Core/Experiences/Constellation/ConstellationContent.cs)
- [backend/src/Duora.Core/Experiences/UnlikelyRoutes/UnlikelyRoutesContent.cs](https://github.com/BipoXDXD/duora/blob/6c89d99bf990db780e8ac8bf7c89ae0dc4e6816f/backend/src/Duora.Core/Experiences/UnlikelyRoutes/UnlikelyRoutesContent.cs)
- [backend/src/Duora.Core/Experiences/UnlikelyRoutes/UnlikelyRoutesRules.cs](https://github.com/BipoXDXD/duora/blob/6c89d99bf990db780e8ac8bf7c89ae0dc4e6816f/backend/src/Duora.Core/Experiences/UnlikelyRoutes/UnlikelyRoutesRules.cs)
- [backend/src/Duora.Infrastructure/Participation/ParticipationService.RoutesCatalog.cs](https://github.com/BipoXDXD/duora/blob/6c89d99bf990db780e8ac8bf7c89ae0dc4e6816f/backend/src/Duora.Infrastructure/Participation/ParticipationService.RoutesCatalog.cs)
- [backend/src/Duora.Contracts/Participation/CatalogContracts.cs](https://github.com/BipoXDXD/duora/blob/6c89d99bf990db780e8ac8bf7c89ae0dc4e6816f/backend/src/Duora.Contracts/Participation/CatalogContracts.cs)
- [backend/src/Duora.Contracts/Participation/RoutesCatalogContracts.cs](https://github.com/BipoXDXD/duora/blob/6c89d99bf990db780e8ac8bf7c89ae0dc4e6816f/backend/src/Duora.Contracts/Participation/RoutesCatalogContracts.cs)
