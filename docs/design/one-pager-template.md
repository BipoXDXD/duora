# One pager de ambiente e de personagem

Template do documento de uma página que alimenta arte, som e roteiro de cada ambiente e de cada personagem
das experiências ([ADR-0027](../architecture/adr/0027-midia-das-experiencias-cenario-audio-e-3d.md)). O
formato vem de *The Definitive Game Narrative Guide* (Kindregan e Lasrado), cap. 22, pp. 284–288: categorias
curtas, o mais importante primeiro (palavras-chave), uma página só. Quem lê para gerar imagem, modelo 3D,
trilha ou voz deve achar o que precisa sem ler o resto.

Regras para preencher:

- **Uma página.** Se não couber, corte o menos importante. Estilo: claro e direto, sem frase de efeito.
- **Nenhum dado real:** nada de participante, conversa, foto, denúncia ou pessoa real como referência visual ou
  de voz sem consentimento registrado em [assets-licenses.md](assets-licenses.md).
- **Imagem de referência da internet** é só interna e temporária; nunca vira asset nem entra no repositório
  (NG p. 286).
- **Falas de amostra** não são finais; as finais vivem no conteúdo da versão, com até 120 caracteres por idioma.
- O one pager fica em `docs/design/one-pagers/<experiência>-<nome>.md` e entra no PR que versiona o primeiro
  asset dele; a coluna "One pager" de [assets-licenses.md](assets-licenses.md) aponta para ele.

## Ambiente

```markdown
# <Nome do ambiente>

- **Experiência e etapas:** <ex.: Rotas Improváveis · introdução e conclusão>
- **Tema:** <uma linha: o que este lugar é>
- **Palavras-chave:** <4 a 6 palavras; contradições são bem-vindas>
- **Referência do mundo real:** <lugar ou bioma parecido, para buscar referência>
- **Elementos marcantes:** <2 a 4 marcos visuais que a cena precisa ter>
- **Clima:** <como o lugar se sente: luz, hora, movimento, temperatura>
- **Narrativa ambiental:** <o que a cena conta sem palavras; nunca uma regra ou pista do jogo, que ficam em texto>
- **Personagens em cena:** <nenhum ou um; nome do one pager do personagem>
- **Paleta visual:** <tons e como mapeiam para os tokens (`action`, `text-muted`, superfícies)>
- **Paleta sonora:**
  - música: <instrumentação, andamento, quando some (etapas de foco e decisão: silêncio ou ambiente)>
  - ambiente: <camadas do lugar, poucas>
  - interface: <sons de confirmação, um significado cada>
  - silêncio: <onde não há som de propósito>
- **3D:** <offline (WebP) ou tempo real; se tempo real, por que, interação permitida e orçamento do GLB>
- **Orçamento:** <imagem ≤ 250 KB a 2x; áudio ≤ 3 MB por idioma; GLB ≤ 1,5 MB; hipóteses do ADR-0027>
- **Texto alternativo:** <descrição curta do ambiente, PT-BR e EN-US, ou "decorativo">
- **Nunca aqui:** <o que não pode aparecer nem tocar: casal-padrão, estereótipo, texto na imagem, som de prazo,
  som de recompensa ligado a pessoas, qualquer coisa que pareça um participante>
```

## Personagem

```markdown
# <Nome do personagem>

- **Papel:** <guia, narrador, figurante; em que ambientes e etapas aparece>
- **Palavras-chave:** <4 a 8 palavras; contradições são bem-vindas>
- **Atitude:** <uma linha, dita direto>
- **Visão geral:** <um parágrafo curto: o mais importante sobre ele>
- **Biografia:** <opcional, três linhas no máximo>
- **Aparência:** <humano estilizado ou não; adulto; traços, roupa, silhueta; nunca fotorrealista nem parecido
  com pessoa real; contribui para a diversidade do elenco da experiência>
- **Arte de referência:** <conceito, render ou placeholder interno>
- **Voz:**
  - descrição: <timbre, ritmo, energia, sotaque em cada idioma>
  - referência: <sintética sem referência, ou voz humana de referência com consentimento e licença registrados>
  - aviso: <"voz editorial" exibido junto da transcrição, se for>
- **Falas de amostra (não finais):**
  - PT-BR: <até 120 caracteres>
  - EN-US: <até 120 caracteres>
- **Barks:** <categorias (ex.: entrada na etapa, escolha registrada, conclusão), pelo menos 3 variações cada,
  falas distintas marcadas como raras>
- **Nunca diz:** <nome de participante, comentário sobre a escolha, a aparência ou a compatibilidade da outra
  pessoa, pressão de prazo, convite para insistir>
```
