# Registro de licenças dos assets visuais

Origem e direito de uso de cada imagem versionada no repositório (spec 13.3, "Ilustração e fotografia":
direitos de uso registrados). Asset sem linha aqui não entra no repositório; o teste
`frontend/config/illustrations.test.ts` (roda no job `frontend` do CI) falha se uma ilustração gerada do catálogo não tiver linha com o
task id da geração.

## Ilustrações de objetos (`frontend/src/shared/ui/illustrations/`)

- **Provedor:** fonte externa (API REST `POST /v3/generation/text-to-image`), no plano pago do responsável pelo
  produto. Uso comercial das imagens geradas: conforme informado pelo responsável; termos não verificados. Os
  termos públicos (<registro externo de origem>) responderam 403 à leitura automática em 2026-10-16; o
  responsável confirma a cláusula de uso comercial do plano e registra aqui o link e a data em que a leu.
- **Modelo:** fonte externa, identificador `fonte externa` no fonte externa.
- **Fonte:** PNG 1024 × 1024 sobre verde chroma, fora do Git (`tools/visual/out/source/`). O que se versiona é
  o WebP derivado por `tools/visual/process.py` (recorte, recoloração para os tokens e redução para 1x e 2x).
- **Prompt:** prefixo comum (`promptPrefix` de `tools/visual/catalog.json`) seguido do prompt do objeto. O
  prefixo é:

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

## Assets com processamento determinístico

Nenhum versionado hoje. O `placeholder` do catálogo é desenhado por `draw_placeholder` em
`tools/visual/process.py` só para os testes do pipeline e não vai para o frontend.

## Como registrar um asset novo

1. Gere a fonte pelo processo de [tools/visual/README.md](../../tools/visual/README.md) e guarde o task id.
2. Acrescente a linha na tabela acima no mesmo PR que versiona o WebP: modelo, provedor, data, task id e
   prompt do objeto.
3. Asset de terceiro (banco de imagens, fonte, ícone) entra com a licença e o link da página de origem; licença
   que não permite uso comercial ou exige atribuição não registrada aqui não entra.
