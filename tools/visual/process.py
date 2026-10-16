"""Transforma as fontes geradas (PNG sobre verde chroma) nos WebP versionados do frontend.

Etapas por objeto: chroma key com despill, enquadramento quadrado com margem, redução para 1x e 2x do
`cssPx` do uso, recoloração para os tokens, verificação de matiz sobre o WebP já decodificado e gravação.
O manifesto TypeScript do frontend é gerado do catálogo, para os nomes do componente não divergirem dele.

Uso (a partir da raiz do repositório, com o venv de requirements-visual.txt):
    python tools/visual/process.py            # todos os objetos do catálogo
    python tools/visual/process.py compass    # só os nomes indicados
"""

from __future__ import annotations

import argparse
import io
import statistics
import sys
from pathlib import Path
from typing import Iterable, Mapping, Sequence

from PIL import Image, ImageDraw

from catalog import REPO_ROOT, TOOLS_DIR, Catalog, CatalogObject, load_catalog, resolve_roles
from palette import HueReport, Oklch, check_hues, hue_distance, oklab_distance, srgb_to_oklch
from recolor import Recolorer, key_alpha

SOURCE_DIR = TOOLS_DIR / "out" / "source"
FRONTEND_UI_DIR = REPO_ROOT / "frontend" / "src" / "shared" / "ui"
ASSETS_DIR = FRONTEND_UI_DIR / "illustrations"
MANIFEST_PATH = FRONTEND_UI_DIR / "illustrations.generated.ts"

DENSITIES = (("", 1), ("@2x", 2))
PLACEHOLDER_SOURCE_PX = 1024
# Pixel quase transparente quase não aparece na tela; o matiz dele não conta na verificação.
MIN_CHECKED_ALPHA = 128
# O fundo precisa ser o verde chroma pedido no prompt; outro fundo indica uma fonte inutilizável.
CHROMA_GREEN_HUE = 142
KEY_HUE_TOLERANCE = 30
KEY_MIN_CHROMA = 0.15
WEBP_METHOD = 6  # compressão mais lenta e menor; o pipeline roda fora do app

RGB = tuple[int, int, int]
RGBA = tuple[int, int, int, int]
TRANSPARENT: RGBA = (0, 0, 0, 0)


class ProcessError(RuntimeError):
    pass


def main(argv: Sequence[str] | None = None) -> int:
    args = _parse_args(argv)
    catalog = load_catalog()
    roles = resolve_roles(catalog, tokens=None)
    selected = [catalog.find(name) for name in args.names] if args.names else list(catalog.objects)
    failures = []
    for item in selected:
        try:
            written = process_object(catalog, item, roles, args.source_dir, args.output_dir)
        except ProcessError as error:
            failures.append(str(error))
            print(f"FALHOU {item.name}: {error}", file=sys.stderr)
            continue
        print(f"ok {item.name}: {', '.join(str(path.relative_to(REPO_ROOT)) for path in written)}")
    MANIFEST_PATH.write_text(render_manifest(catalog), encoding="utf-8")
    return 1 if failures else 0


def _parse_args(argv: Sequence[str] | None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("names", nargs="*", help="objetos do catálogo (padrão: todos)")
    parser.add_argument("--source-dir", type=Path, default=SOURCE_DIR)
    parser.add_argument("--output-dir", type=Path, default=ASSETS_DIR)
    return parser.parse_args(argv)


def process_object(
    catalog: Catalog,
    item: CatalogObject,
    roles: Mapping[str, Oklch],
    source_dir: Path,
    output_dir: Path,
) -> list[Path]:
    """Grava `<nome>.webp` e `<nome>@2x.webp`; se uma densidade reprovar, nenhuma é gravada."""
    framed = frame(_load_object(catalog, item, source_dir), float(catalog.process["marginFraction"]))
    recolorer = _recolorer(catalog, roles)
    css_px = catalog.css_px(item.name)
    quality = int(catalog.process["webpQuality"])
    encoded: dict[str, bytes] = {}
    for suffix, scale in DENSITIES:
        label = f"{item.name}{suffix}"
        image = render(framed, css_px * scale, recolorer)
        encoded[f"{label}.webp"] = _encode_within_palette(label, image, quality, roles, catalog.hue_check)
    output_dir.mkdir(parents=True, exist_ok=True)
    written = []
    for file_name, data in encoded.items():
        path = output_dir / file_name
        path.write_bytes(data)
        written.append(path)
    return written


def _encode_within_palette(
    label: str,
    image: Image.Image,
    quality: int,
    roles: Mapping[str, Oklch],
    hue_check: Mapping[str, float],
) -> bytes:
    """WebP com perda em `quality`; se a subamostragem de croma (4:2:0) do modo com perda tirar detalhes finos
    de dois tons da paleta, cai para WebP sem perda em vez de afrouxar a tolerância."""
    lossy = _encode_webp(image, {"quality": quality})
    if review_encoded(lossy, roles, hue_check).passed:
        return lossy
    lossless = _encode_webp(image, {"lossless": True})
    report = review_encoded(lossless, roles, hue_check)
    if report.passed:
        return lossless
    raise ProcessError(
        f"{label}: matiz fora da paleta em {report.outliers} de {report.chromatic} pixels cromáticos "
        f"(máximo {report.max_outliers}), mesmo sem perda"
    )


def review_encoded(data: bytes, roles: Mapping[str, Oklch], hue_check: Mapping[str, float]) -> HueReport:
    """Verifica o arquivo como o navegador o decodifica, não a imagem antes da compressão."""
    with Image.open(io.BytesIO(data)) as decoded:
        return review_hues(decoded.convert("RGBA"), roles, hue_check)


def _load_object(catalog: Catalog, item: CatalogObject, source_dir: Path) -> Image.Image:
    if item.source == "procedural":
        return draw_placeholder(PLACEHOLDER_SOURCE_PX)
    path = source_dir / f"{item.name}.png"
    if not path.is_file():
        raise ProcessError(f"fonte de '{item.name}' não encontrada em {path}")
    with Image.open(path) as source:
        image = source.convert("RGB")
    key = catalog.process["chromaKey"]
    return chroma_key(image, estimate_key_color(image), float(key["inner"]), float(key["outer"]))


def _recolorer(catalog: Catalog, roles: Mapping[str, Oklch]) -> Recolorer:
    low, high = catalog.palette["lightness"]
    classify = catalog.palette["classify"]
    return Recolorer(
        roles=roles,
        lightness=(float(low), float(high)),
        min_chroma=float(classify["minChroma"]),
        action_hue_window=float(classify["actionHueWindowDegrees"]),
    )


def estimate_key_color(image: Image.Image) -> RGB:
    """Mediana de cada canal na borda: o objeto pode tocar a borda, mas não domina o contorno."""
    rgb = image.convert("RGB")
    width, height = rgb.size
    pixels = _rgb_pixels(rgb)
    border = [*range(width), *range((height - 1) * width, height * width)]
    border += [row * width + column for row in range(1, height - 1) for column in (0, width - 1)]
    pixels = [pixels[index] for index in border]
    key = (
        round(statistics.median(p[0] for p in pixels)),
        round(statistics.median(p[1] for p in pixels)),
        round(statistics.median(p[2] for p in pixels)),
    )
    color = _oklch(key)
    if color.c < KEY_MIN_CHROMA or hue_distance(color.h, CHROMA_GREEN_HUE) > KEY_HUE_TOLERANCE:
        raise ProcessError(f"o fundo {key} não é o verde chroma pedido no prompt")
    return key


def chroma_key(image: Image.Image, key: RGB, inner: float, outer: float) -> Image.Image:
    """RGBA com alfa pela distância OKLab até o fundo, cor da borda desmisturada do fundo e sem verde."""
    rgb = image.convert("RGB")
    key_color = _oklch(key)
    cache: dict[RGB, RGBA] = {}
    keyed = []
    for pixel in _rgb_pixels(rgb):
        result = cache.get(pixel)
        if result is None:
            result = _key_pixel(pixel, key, key_color, inner, outer)
            cache[pixel] = result
        keyed.append(result)
    return _rgba_image(rgb.size, keyed)


def _key_pixel(pixel: RGB, key: RGB, key_color: Oklch, inner: float, outer: float) -> RGBA:
    alpha = key_alpha(oklab_distance(_oklch(pixel), key_color), inner, outer)
    if alpha == 0:
        return TRANSPARENT
    if alpha < 1:
        pixel = _unmix(pixel, key, alpha)
    red, green, blue = pixel
    # Despill em todo pixel do objeto, não só na borda: a argila reflete o fundo. A paleta não tem verde,
    # então limitar G ao maior entre R e B não altera índigo nem off-white quente.
    return (red, min(green, max(red, blue)), blue, round(alpha * 255))


def _unmix(pixel: RGB, key: RGB, alpha: float) -> RGB:
    """Tira a parcela do fundo da cor da borda: observado = alfa x objeto + (1 - alfa) x fundo."""
    red, green, blue = (min(max(round((c - (1 - alpha) * k) / alpha), 0), 255) for c, k in zip(pixel, key))
    return (red, green, blue)


def frame(keyed: Image.Image, margin_fraction: float) -> Image.Image:
    """Recorta no contorno do objeto e centraliza num quadrado com `margin_fraction` de folga no lado maior."""
    if not 0 <= margin_fraction < 0.5:
        raise ProcessError(f"marginFraction {margin_fraction} precisa estar em [0, 0.5)")
    bbox = keyed.getchannel("A").getbbox()
    if bbox is None:
        raise ProcessError("nenhum pixel do objeto depois do chroma key")
    cropped = keyed.crop(bbox)
    longest = max(cropped.size)
    margin = round(longest * margin_fraction / (1 - 2 * margin_fraction))
    side = longest + 2 * margin
    canvas = Image.new("RGBA", (side, side), TRANSPARENT)
    canvas.paste(cropped, ((side - cropped.width) // 2, (side - cropped.height) // 2))
    return canvas


def render(framed: Image.Image, size: int, recolorer: Recolorer) -> Image.Image:
    """Reduz e só então recolore: a redução mistura cores vizinhas, e recolorir depois devolve cada pixel
    misturado a um dos dois matizes da paleta em vez de deixar matizes intermediários."""
    resized = framed.resize((size, size), Image.Resampling.LANCZOS)
    cache: dict[RGB, RGB] = {}
    recolored = []
    for red, green, blue, alpha in _rgba_pixels(resized):
        if alpha == 0:
            recolored.append(TRANSPARENT)
            continue
        pixel = (red, green, blue)
        tone = cache.get(pixel)
        if tone is None:
            tone = recolorer.recolor(pixel)
            cache[pixel] = tone
        recolored.append((*tone, alpha))
    return _rgba_image(resized.size, recolored)


def review_hues(image: Image.Image, roles: Mapping[str, Oklch], hue_check: Mapping[str, float]) -> HueReport:
    """Matizes dos pixels visíveis contra os papéis da paleta, com a tolerância de `hueCheck`."""
    cache: dict[RGB, Oklch] = {}
    visible = []
    for red, green, blue, alpha in _rgba_pixels(image.convert("RGBA")):
        if alpha < MIN_CHECKED_ALPHA:
            continue
        pixel = (red, green, blue)
        color = cache.get(pixel)
        if color is None:
            color = _oklch(pixel)
            cache[pixel] = color
        visible.append(color)
    return check_hues(
        visible,
        allowed_hues=[role.h for role in roles.values()],
        tolerance=float(hue_check["toleranceDegrees"]),
        min_chroma=float(hue_check["minChroma"]),
        max_outlier_fraction=float(hue_check["maxOutlierFraction"]),
    )


def draw_placeholder(size: int) -> Image.Image:
    """Seixo neutro desenhado aqui mesmo, sem IA: corpo cinza quente com um reflexo mais claro em cima."""
    supersample = 4
    big = size * supersample
    canvas = Image.new("RGBA", (big, big), TRANSPARENT)
    draw = ImageDraw.Draw(canvas)
    draw.ellipse(_scaled_box(big, (0.12, 0.26, 0.88, 0.80)), fill=(150, 146, 140, 255))
    draw.ellipse(_scaled_box(big, (0.26, 0.32, 0.62, 0.50)), fill=(206, 202, 195, 255))
    return canvas.resize((size, size), Image.Resampling.LANCZOS)


def _scaled_box(side: int, fractions: tuple[float, float, float, float]) -> tuple[int, int, int, int]:
    left, top, right, bottom = (round(side * fraction) for fraction in fractions)
    return (left, top, right, bottom)


def render_manifest(catalog: Catalog) -> str:
    """Módulo TypeScript com os imports das duas densidades e o tamanho CSS de cada objeto."""
    names = sorted(item.name for item in catalog.objects)
    lines = [
        "// Gerado por tools/visual/process.py a partir de tools/visual/catalog.json; não edite à mão.",
        *_manifest_imports(names),
        "",
        "export const ILLUSTRATIONS = {",
    ]
    for name in names:
        variable = _camel_case(name)
        key = name if variable == name else f"'{name}'"
        lines.append(f"  {key}: {{ cssPx: {catalog.css_px(name)}, src1x: {variable}1x, src2x: {variable}2x }},")
    lines += ["} as const", ""]
    return "\n".join(lines)


def _manifest_imports(names: Iterable[str]) -> list[str]:
    imports = []
    for name in names:
        for suffix, scale in DENSITIES:
            imports.append(f"import {_camel_case(name)}{scale}x from './illustrations/{name}{suffix}.webp'")
    return imports


def _camel_case(name: str) -> str:
    head, *rest = name.split("-")
    return head + "".join(part.capitalize() for part in rest)


def _encode_webp(image: Image.Image, options: Mapping[str, object]) -> bytes:
    buffer = io.BytesIO()
    image.save(buffer, format="WEBP", method=WEBP_METHOD, **options)
    return buffer.getvalue()


def _oklch(pixel: RGB) -> Oklch:
    return srgb_to_oklch(pixel[0] / 255, pixel[1] / 255, pixel[2] / 255)


def _rgb_pixels(image: Image.Image) -> list[RGB]:
    data = image.tobytes()
    return [(data[i], data[i + 1], data[i + 2]) for i in range(0, len(data), 3)]


def _rgba_pixels(image: Image.Image) -> list[RGBA]:
    data = image.tobytes()
    return [(data[i], data[i + 1], data[i + 2], data[i + 3]) for i in range(0, len(data), 4)]


def _rgba_image(size: tuple[int, int], pixels: Sequence[RGBA]) -> Image.Image:
    return Image.frombytes("RGBA", size, bytes(channel for pixel in pixels for channel in pixel))


if __name__ == "__main__":
    sys.exit(main())
