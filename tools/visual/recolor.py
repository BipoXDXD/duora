"""Recoloração por pixel para os tokens e alfa do chroma key (Python puro, sem Pillow)."""

from __future__ import annotations

from typing import Mapping

from palette import Oklch, hue_distance, lightness_ramp, srgb_to_oklch, to_srgb8

ROLES = ("action", "neutral")
TONE_LEVELS = 256  # um tom por nível de 8 bits da luminosidade de origem


class Recolorer:
    """Troca o matiz de cada pixel pelo do seu papel e mantém a luminosidade relativa.

    O papel é escolhido de forma dura (sem mistura): misturar dois matizes em RGB cria matizes
    intermediários, que o teste de matiz reprova.
    """

    def __init__(
        self,
        roles: Mapping[str, Oklch],
        lightness: tuple[float, float],
        min_chroma: float,
        action_hue_window: float,
    ) -> None:
        missing = [role for role in ROLES if role not in roles]
        if missing:
            raise ValueError(f"papéis ausentes: {', '.join(missing)}")
        self._action_hue = roles["action"].h
        self._min_chroma = min_chroma
        self._action_hue_window = action_hue_window
        self._tones = {
            role: [to_srgb8(stop.color) for stop in lightness_ramp(color.h, color.c, lightness, TONE_LEVELS)]
            for role, color in roles.items()
        }

    def classify(self, color: Oklch) -> str:
        is_action = color.c >= self._min_chroma and hue_distance(color.h, self._action_hue) <= self._action_hue_window
        return "action" if is_action else "neutral"

    def recolor(self, rgb8: tuple[int, int, int]) -> tuple[int, int, int]:
        color = srgb_to_oklch(*(v / 255 for v in rgb8))
        level = round(min(max(color.l, 0.0), 1.0) * (TONE_LEVELS - 1))
        return self._tones[self.classify(color)][level]


def key_alpha(distance: float, inner: float, outer: float) -> float:
    """Alfa pela distância OKLab até o fundo: 0 até `inner`, 1 a partir de `outer`, linear entre eles."""
    if inner >= outer:
        raise ValueError("inner precisa ser menor que outer")
    if distance <= inner:
        return 0.0
    if distance >= outer:
        return 1.0
    return (distance - inner) / (outer - inner)
