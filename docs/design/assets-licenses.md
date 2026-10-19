# Registro de licenças dos assets de mídia

Origem e direito de uso de cada imagem, modelo 3D e áudio versionado no repositório (spec 13.3, "Ilustração e
fotografia": direitos de uso registrados; spec 6.4; [ADR-0027](../architecture/adr/0027-midia-das-experiencias-cenario-audio-e-3d.md)). Asset sem linha aqui não entra no repositório; o teste
`frontend/config/illustrations.test.ts` (roda no job `frontend` do CI) falha se uma ilustração gerada do catálogo não tiver linha com o
task id da geração.

## Fornecedores aprovados

| Fornecedor | Para quê | Plano | Termos de uso comercial |
|---|---|---|---|
| fonte externa | Imagem (fonte externa, fonte externa) e modelo 3D | Pago, do responsável pelo produto | Conforme informado pelo responsável; termos não verificados (ver abaixo) |
| fonte externa (fonte externa) | Modelo 3D (fonte offline e GLB em tempo real) | Business, do responsável pelo produto (decisão de 2026-10-19) | Conforme informado pelo responsável; termos não verificados. O responsável registra aqui o link e a data em que leu a cláusula de uso comercial |
| fonte externa | Voz editorial (TTS) e música | **Creator** (decisão do responsável, 2026-10-19) | Termos em <registro externo de origem>; o responsável registra aqui a data em que leu a cláusula de uso comercial do Creator (a registrar). A chave `EDITORIAL_API_KEY` fica só no ambiente de quem gera; o MCP hospedado usa OAuth (metadados em <registro externo de origem>; fluxo a confirmar no primeiro uso) |
| Freesound | Efeitos sonoros | Gratuito; **só CC0** | Licença CC0 por som, conferida na página de cada um (<https://freesound.org/help/faq/>); nada com voz identificável |
| Sonniss | Efeitos sonoros (pacotes) | Pacotes baixados pelo responsável | Licença do pacote (<https://sonniss.com/gameaudiogdc>) proíbe redistribuir som avulso; como o repositório é público, só o mix final entra no Git (termo exato a confirmar) |

## Ilustrações de objetos (`frontend/src/shared/ui/illustrations/`)

- **Provedor:** fonte externa (API REST `POST /v3/generation/text-to-image`), no plano pago do responsável pelo
  produto. Uso comercial das imagens geradas: conforme informado pelo responsável; termos não verificados. Os
  termos públicos (<registro externo de origem>) responderam 403 à leitura automática em 2026-10-16; o
  responsável confirma a cláusula de uso comercial do plano e registra aqui o link e a data em que a leu.
- **Modelo:** fonte externa, identificador `fonte externa` no fonte externa.
- **Fonte:** PNG 1024 × 1024 sobre verde chroma, fora do Git (`tools/visual/out/source/`). O que se versiona é
  o WebP derivado por `tools/visual/process.py` (recorte, recoloração para os tokens e redução para 1x e 2x).
- **Prompt:** prefixo comum (`promptPrefix` de `tools/visual/catalog.json`) seguido do prompt do objeto.
- **Prefixo v2** (vigente desde 2026-10-19; ADR-0027 retirou a proibição de pessoas). A cópia abaixo é conferida
  contra `promptPrefix` de `tools/visual/catalog.json` por `frontend/config/illustrations.test.ts`:

  > Studio illustration of a single stylized object, soft matte clay look, rounded simplified shapes, two-tone deep indigo and warm off-white, three-quarter view from slightly above, soft light from the top, on a flat solid chroma-key green background with no shadow on it, no text, no letters, no numbers, no symbols, no logos. Object: 

- **Prefixo v1** — **Superseded by ADR-0027** para gerações novas; é o prefixo com que os 10 assets abaixo, de
  2026-10-16, foram gerados, e fica aqui como registro da origem deles:

  > Studio illustration of a single stylized object, soft matte clay look, rounded simplified shapes, two-tone deep indigo and warm off-white, three-quarter view from slightly above, soft light from the top, on a flat solid chroma-key green background with no shadow on it, no text, no letters, no numbers, no symbols, no logos, no people, no faces, no hands, no body parts. Object: 

| Asset | Modelo | Provedor | Data | Task id | Prompt do objeto |
|---|---|---|---|---|---|
| `card-pair` | fonte externa(`fonte externa`) | fonte externa | 2026-10-16 | `014ccb8e-d744-4b74-abc1-3a56904f80a1` | two rounded blank cards leaning against each other, each with one small embossed dot, standing on their edges |
| `compass` | fonte externa(`fonte externa`) | fonte externa | 2026-10-16 | `918079b3-46cf-4187-b23a-701d6d2f8a9b` | a small round pocket compass with the hinged lid open, a simple diamond-shaped needle, a plain dial with four small dots instead of letters |
| `constellation` | fonte externa(`fonte externa`) | fonte externa | 2026-10-16 | `8b4172dd-467f-45e7-bb5e-0b86edfcec01` | a small desk sculpture of five spheres of different sizes joined by thin rods into a constellation, on a round flat base |
| `envelope` | fonte externa(`fonte externa`) | fonte externa | 2026-10-16 | `ac94f3d7-68db-4351-832b-15165dac9765` | a closed envelope with a rounded flap and a plain round seal without any emblem, slightly tilted |
| `folded-map` | fonte externa(`fonte externa`) | fonte externa | 2026-10-16 | `e07da4a4-f147-47eb-85a7-693eddea73d1` | a folded paper map in three panels standing slightly open, with a single dotted path and two round pins, no place names |
| `lantern` | fonte externa(`fonte externa`) | fonte externa | 2026-10-16 | `df965059-9fb9-4ffa-b795-1e38f591e356` | a small rounded camping lantern with a handle on top and a softly frosted glass body |
| `padlock` | fonte externa(`fonte externa`) | fonte externa | 2026-10-16 | `5e966509-3a5d-4712-b665-d6e5f6e81159` | a chunky rounded padlock with a closed shackle and a plain body without keyhole details |
| `shield` | fonte externa(`fonte externa`) | fonte externa | 2026-10-16 | `515a420c-b2fa-4df0-b657-55514fd5b207` | a soft rounded shield with a raised smooth rim and a plain center, standing upright |
| `speech-bubbles` | fonte externa(`fonte externa`) | fonte externa | 2026-10-16 | `bcb8be68-1d8f-48b3-89fa-5eb7a4126c43` | two overlapping rounded speech bubble sculptures of different sizes, empty, no dots or marks |
| `ticket` | fonte externa(`fonte externa`) | fonte externa | 2026-10-16 | `0370e3f7-1bfe-4826-a231-11cca0035871` | a blank admission ticket with notched corners and a perforated stub, slightly curled |

## Cenários, personagens e modelos 3D

Nenhum versionado hoje. Cada linha entra no PR que versiona o arquivo, com o one pager de origem
([one-pager-template.md](one-pager-template.md)). Modelo 3D usado só como fonte offline registra também os WebP
derivados.

| Asset | Tipo (`scene`, `character`, `model3d`) | Ferramenta e modelo | Provedor | Data | Task id | SHA-256 | Licença | One pager | Prompt ou fonte |
|---|---|---|---|---|---|---|---|---|---|

## Áudio

Nenhum versionado hoje. O que entra no Git é o **arquivo final** (mix codificado em AAC-LC, ADR-0027), com uma
linha por idioma (`pt-BR`, `en-US`) na tabela de arquivos finais; as fontes de cada fornecedor ficam nas tabelas
seguintes, ligadas pelo nome do arquivo final. A transcrição é o texto da fala no conteúdo da versão.

### Arquivos finais

| Asset | Canal (`music`, `ambience`, `voice`, `ui`) | Idioma | Data | SHA-256 | Fontes (linhas abaixo) | Sintética |
|---|---|---|---|---|---|---|

### Fontes fonte externa (voz e música)

Voz da Voice Library: registrar o `voice_id` e o prazo de retirada que a autora da voz pode exercer. Voz clonada de
pessoa real só entra com consentimento assinado (documento, data e escopo de uso) na última coluna.

| Asset final | Plano ativo na data | `voice_id` | Modelo | Id da geração | Formato de saída | Data | Voice Library: prazo de retirada | Voz clonada: consentimento |
|---|---|---|---|---|---|---|---|---|

### Fontes Freesound (só CC0)

| Asset final | URL | ID | Autor | Licença | Data do download |
|---|---|---|---|---|---|

### Fontes Sonniss

O arquivo original do pacote nunca entra no repositório.

| Asset final | Pacote | Ano | Arquivo de origem | Licença | Data do download |
|---|---|---|---|---|---|

## Assets com processamento determinístico

Nenhum versionado hoje. O `placeholder` do catálogo é desenhado por `draw_placeholder` em
`tools/visual/process.py` só para os testes do pipeline e não vai para o frontend.

## Como registrar um asset novo

1. Gere a fonte pelo processo de [tools/visual/README.md](../../tools/visual/README.md) e guarde o task id.
2. Acrescente a linha na tabela certa no mesmo PR que versiona o arquivo: modelo, provedor, data, task id e
   prompt (imagem), mais SHA-256 e licença (cenário, 3D e áudio) e consentimento (voz de referência). O mesmo
   hash vai para o manifesto de mídia da versão da experiência (ADR-0027).
3. Asset de terceiro (banco de imagens, fonte, ícone) entra com a licença e o link da página de origem; licença
   que não permite uso comercial ou exige atribuição não registrada aqui não entra.
