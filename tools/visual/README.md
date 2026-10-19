# tools/visual

Pipeline das ilustrações de objetos do Duora: gera a fonte com IA, recorta, recolore para os tokens do
frontend e grava os WebP versionados em `frontend/src/shared/ui/illustrations/`, consumidos pelo componente
`<Illustration>` (`frontend/src/shared/ui/illustration.tsx`).

Regras de conteúdo e de uso: [docs/design/system.md](../../docs/design/system.md#ilustração-cenário-e-personagem). Origem e direito
de uso de cada asset: [docs/design/assets-licenses.md](../../docs/design/assets-licenses.md).

## Arquivos

| Arquivo | Papel |
|---|---|
| `catalog.json` | Fonte da verdade: prefixo de prompt, papéis de cor, tolerâncias, usos (`cssPx`) e objetos |
| `catalog.py` | Lê e valida o catálogo; resolve os papéis de cor em `frontend/src/index.css` |
| `palette.py` | OKLCH/OKLab, rampas de luminosidade e verificação de matiz (Python puro) |
| `recolor.py` | Recoloração por pixel e alfa do chroma key (Python puro) |
| `process.py` | Pipeline com Pillow: fonte PNG → WebP 1x/2x + manifesto TypeScript |
| `requirements.in` / `requirements.txt` | Pillow fixado, lockfile com hashes (o nome `requirements.txt` é o que o Trivy lê) |
| `out/` | Fontes e intermediários; **fora do Git** (`.gitignore`) |

## 1. Gerar a fonte

Só modelos aprovados pelo responsável: **GPT Image 2.5** ou **Nano Banana Pro**, pela API REST do Tripo, no plano
pago do responsável. As 10 fontes atuais usam GPT Image 2.5 (`chat_image_2.5_sunburst`).

- **Prompt** = `promptPrefix` + `prompt` do objeto, concatenados sem separador extra (o prefixo termina em
  `Object: `). Não edite o prefixo por objeto: ele carrega as proibições (sem texto, letras, números, símbolos
  ou logos) e o fundo verde chroma de que o recorte depende. Pessoas e personagens não são mais proibidos
  ([ADR-0027](../../docs/architecture/adr/0027-midia-das-experiencias-cenario-audio-e-3d.md)); cenários e
  personagens de experiência partem do [one pager](../../docs/design/one-pager-template.md) e ganham prefixo
  próprio quando o primeiro entrar no pipeline.
- **Envio:** `POST https://openapi.tripo3d.ai/v3/generation/text-to-image` com
  `Authorization: Bearer $TRIPO_API_KEY` e corpo
  `{"model": "chat_image_2.5_sunburst", "prompt": "<prefixo + objeto>", "size": "1024x1024"}`
  (Nano Banana Pro usa `"aspect_ratio": "1:1"` no lugar de `size`). A resposta traz `data.task_id`.
- **Resultado:** consulte `GET /v3/tasks/<task_id>` até `status` ser `success` e baixe a URL de `output`.
- **Grave** em `tools/visual/out/source/<nome>.png` e anote o task id em `out/source/tasks.json`
  (`{"<nome>": "<task_id>"}`), que alimenta o registro de licenças.

A chave do Tripo fica só no ambiente de quem gera; nunca no repositório, em log ou em commit.

Antes de processar, olhe a fonte: objeto único, sem texto, fundo verde liso. Fonte que viola isso é descartada
e gerada de novo; não se corrige no pós-processamento.

## 2. Processar

A partir da raiz do repositório, com Python 3.12+:

```sh
python3 -m venv tools/visual/out/venv           # out/ fica fora do Git
tools/visual/out/venv/bin/pip install --require-hashes -r tools/visual/requirements.txt
tools/visual/out/venv/bin/python tools/visual/process.py            # todos os objetos
tools/visual/out/venv/bin/python tools/visual/process.py compass    # só os nomes indicados
```

Por objeto, `process.py`:

1. **Chroma key:** cor do fundo = mediana da borda (aborta se não for verde chroma); alfa pela distância OKLab
   até ela, 0 até `process.chromaKey.inner` e 1 a partir de `outer`. Na faixa intermediária a cor é
   desmisturada do fundo; em todo pixel do objeto, G fica limitado ao maior entre R e B (despill).
2. **Enquadramento:** recorte no contorno e quadrado com `process.marginFraction` de folga no lado maior.
3. **Redução e recolor:** reduz (Lanczos) para 1x e 2x do `cssPx` do uso e só então recolore cada pixel para o
   papel `action` ou `neutral` (`palette`), mantendo a luminosidade relativa. Recolorir depois da redução evita
   matizes intermediários nas bordas.
4. **WebP e `hueCheck`:** codifica com perda em `process.webpQuality` e verifica os matizes **no arquivo
   decodificado** (pixels com alfa ≥ 128). Se reprovar (a subamostragem de croma do modo com perda borra
   detalhes finos de dois tons), usa WebP sem perda e verifica de novo. Se ainda reprovar, nada é gravado e o
   comando sai com erro.
5. Grava `<nome>.webp` e `<nome>@2x.webp`. Se todos os objetos passarem, regenera
   `frontend/src/shared/ui/illustrations.generated.ts`; se algum falhar, o manifesto fica como estava e o comando
   sai com 1. Os imports usam `?no-inline`, para o Vite nunca embutir um WebP pequeno como `data:` (a CSP
   `img-src 'self'` bloquearia).

Só objetos `generated` vão para o frontend. O `placeholder` (`procedural`) existe para os testes do pipeline, e
o comando o recusa por nome.

O resultado é determinístico: a mesma fonte gera os mesmos bytes.

### Conferência antes do commit

- Abra alguns WebP sobre `surface` e sobre um fundo escuro: sem halo verde, sem listras do papel errado.
- Rode os testes e os tipos:

```sh
cd tools/visual && out/venv/bin/python -m unittest discover -s . && cd -
pyright -p tools/visual/pyrightconfig.json --pythonpath tools/visual/out/venv/bin/python tools/visual
pnpm --dir frontend exec vitest run config/illustrations.test.ts src/shared/ui/illustration.test.tsx
```

- Acrescente a linha do asset em [docs/design/assets-licenses.md](../../docs/design/assets-licenses.md); o teste
  do frontend falha sem ela.

## Ajustes registrados

- `palette.classify.minChroma` = 0,02 (era 0,04): a sombra do índigo nas fontes tem croma ~0,03 com matiz azul e
  virava listra bege no papel neutro. O off-white das fontes fica entre 40° e 100° de matiz, longe da janela de
  `action`, então o limiar menor não o captura.
- A lanterna passa sem tolerância especial: o brilho amarelado (matiz ~80°–100°, croma até ~0,06) cai no papel
  `neutral` e sai no off-white quente do token.
