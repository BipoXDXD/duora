"""Cor do pipeline visual: lê os tokens OKLCH do frontend, gera rampas e confere matizes.

Python puro (sem bpy nem numpy) para rodar no Python do Blender e nos testes do repositório.
Conversões OKLab/OKLCH conforme CSS Color 4 (matrizes de Björn Ottosson).
"""

from __future__ import annotations

import math
import re
from dataclasses import dataclass
from statistics import median
from typing import Iterable, NamedTuple, Sequence

_TOKEN = re.compile(r"--color-([a-z-]+):\s*oklch\(\s*([\d.]+)\s+([\d.]+)\s+([\d.]+)\s*\)")
_GAMUT_EPSILON = 1e-6
_GAMUT_SEARCH_STEPS = 24


class Oklch(NamedTuple):
    l: float  # noqa: E741 - nome da coordenada no espaço OKLCH
    c: float
    h: float


class RampStop(NamedTuple):
    position: float
    color: Oklch


def read_tokens(css: str) -> dict[str, Oklch]:
    """Tokens `--color-<nome>: oklch(L C H)` do CSS, sem o prefixo `--color-`."""
    return {name: Oklch(float(l), float(c), float(h)) for name, l, c, h in _TOKEN.findall(css)}


def oklch_to_linear_srgb(color: Oklch) -> tuple[float, float, float]:
    a = color.c * math.cos(math.radians(color.h))
    b = color.c * math.sin(math.radians(color.h))
    l_ = (color.l + 0.3963377774 * a + 0.2158037573 * b) ** 3
    m_ = (color.l - 0.1055613458 * a - 0.0638541728 * b) ** 3
    s_ = (color.l - 0.0894841775 * a - 1.2914855480 * b) ** 3
    return (
        4.0767416621 * l_ - 3.3077115913 * m_ + 0.2309699292 * s_,
        -1.2684380046 * l_ + 2.6097574011 * m_ - 0.3413193965 * s_,
        -0.0041960863 * l_ - 0.7034186147 * m_ + 1.7076147010 * s_,
    )


def srgb_to_oklch(r: float, g: float, b: float) -> Oklch:
    """Converte sRGB codificado (0..1, como num PNG ou WebP de 8 bits) para OKLCH."""
    lr, lg, lb = (v / 12.92 if v <= 0.04045 else ((v + 0.055) / 1.055) ** 2.4 for v in (r, g, b))
    l_ = math.cbrt(0.4122214708 * lr + 0.5363325363 * lg + 0.0514459929 * lb)
    m_ = math.cbrt(0.2119034982 * lr + 0.6806995451 * lg + 0.1073969566 * lb)
    s_ = math.cbrt(0.0883024619 * lr + 0.2817188376 * lg + 0.6299787005 * lb)
    lightness = 0.2104542553 * l_ + 0.7936177850 * m_ - 0.0040720468 * s_
    a = 1.9779984951 * l_ - 2.4285922050 * m_ + 0.4505937099 * s_
    b_ = 0.0259040371 * l_ + 0.7827717662 * m_ - 0.8086757660 * s_
    return Oklch(lightness, math.hypot(a, b_), math.degrees(math.atan2(b_, a)) % 360)


def to_srgb8(color: Oklch) -> tuple[int, int, int]:
    """OKLCH para sRGB de 8 bits, recortando o que sobrar do gamut."""
    encoded = []
    for linear in oklch_to_linear_srgb(color):
        clipped = min(max(linear, 0.0), 1.0)
        value = 12.92 * clipped if clipped <= 0.0031308 else 1.055 * clipped ** (1 / 2.4) - 0.055
        encoded.append(round(value * 255))
    return (encoded[0], encoded[1], encoded[2])


def oklab_distance(first: Oklch, second: Oklch) -> float:
    """Distância euclidiana no OKLab: diferença perceptual entre duas cores."""

    def lab(color: Oklch) -> tuple[float, float, float]:
        radians = math.radians(color.h)
        return (color.l, color.c * math.cos(radians), color.c * math.sin(radians))

    return math.dist(lab(first), lab(second))


def is_in_srgb_gamut(color: Oklch) -> bool:
    return all(-_GAMUT_EPSILON <= v <= 1 + _GAMUT_EPSILON for v in oklch_to_linear_srgb(color))


def lightness_ramp(hue: float, chroma: float, lightness: tuple[float, float], stops: int) -> list[RampStop]:
    """Tons de escuro a claro com matiz fixo; o croma cai onde o tom sairia do sRGB."""
    if stops < 2:
        raise ValueError("a rampa precisa de pelo menos 2 paradas")
    low, high = lightness
    ramp = []
    for index in range(stops):
        position = index / (stops - 1)
        ramp.append(RampStop(position, _fit_chroma(Oklch(low + (high - low) * position, chroma, hue))))
    return ramp


def _fit_chroma(color: Oklch) -> Oklch:
    if is_in_srgb_gamut(color):
        return color
    inside, outside = 0.0, color.c
    for _ in range(_GAMUT_SEARCH_STEPS):
        middle = (inside + outside) / 2
        if is_in_srgb_gamut(color._replace(c=middle)):
            inside = middle
        else:
            outside = middle
    return color._replace(c=inside)


def hue_distance(a: float, b: float) -> float:
    """Menor distância angular entre dois matizes, em graus (0 a 180)."""
    difference = abs(a - b) % 360
    return min(difference, 360 - difference)


@dataclass(frozen=True)
class HueReport:
    total: int
    chromatic: int
    outliers: int
    max_outliers: int
    median_hue_by_target: dict[float, float]

    @property
    def passed(self) -> bool:
        return self.total > 0 and self.outliers <= self.max_outliers


def check_hues(
    pixels: Iterable[Oklch],
    allowed_hues: Sequence[float],
    tolerance: float,
    min_chroma: float,
    max_outlier_fraction: float,
) -> HueReport:
    """Confere se cada pixel cromático fica a até `tolerance` graus de um matiz permitido.

    Pixels com croma abaixo de `min_chroma` são neutros: o matiz deles é ruído e não conta.
    """
    total = 0
    outliers = 0
    offsets_by_target: dict[float, list[float]] = {target: [] for target in allowed_hues}
    for pixel in pixels:
        total += 1
        if pixel.c < min_chroma:
            continue
        target = min(allowed_hues, key=lambda allowed: hue_distance(pixel.h, allowed))
        if hue_distance(pixel.h, target) > tolerance:
            outliers += 1
        offsets_by_target[target].append(_signed_offset(pixel.h, target))
    chromatic = sum(len(offsets) for offsets in offsets_by_target.values())
    return HueReport(
        total=total,
        chromatic=chromatic,
        outliers=outliers,
        max_outliers=math.floor(chromatic * max_outlier_fraction + _GAMUT_EPSILON),
        median_hue_by_target={
            target: (target + median(offsets)) % 360 for target, offsets in offsets_by_target.items() if offsets
        },
    )


def _signed_offset(hue: float, target: float) -> float:
    return (hue - target + 180) % 360 - 180
