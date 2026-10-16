"""Testes do módulo puro de cor do pipeline visual: python3 -m unittest discover -s tools/visual -v"""

import unittest
from pathlib import Path

from palette import (
    Oklch,
    check_hues,
    hue_distance,
    lightness_ramp,
    oklch_to_linear_srgb,
    read_tokens,
    srgb_to_oklch,
)

REPO_ROOT = Path(__file__).resolve().parents[2]

CSS_SAMPLE = """
@theme {
  --color-*: initial;
  --color-surface: oklch(0.99 0.004 85);
  --color-action: oklch(0.47 0.13 265);
  --font-sans: system-ui;
}
"""


class ReadTokensTest(unittest.TestCase):
    def test_reads_every_oklch_color_token(self):
        tokens = read_tokens(CSS_SAMPLE)

        self.assertEqual(tokens, {"surface": Oklch(0.99, 0.004, 85), "action": Oklch(0.47, 0.13, 265)})

    def test_ignores_css_without_color_tokens(self):
        self.assertEqual(read_tokens("--font-sans: system-ui;"), {})

    def test_reads_the_real_stylesheet_of_the_frontend(self):
        tokens = read_tokens((REPO_ROOT / "frontend/src/index.css").read_text(encoding="utf-8"))

        self.assertIn("action", tokens)
        self.assertIn("text-muted", tokens)


class ConversionTest(unittest.TestCase):
    def test_white_round_trips(self):
        white = srgb_to_oklch(1.0, 1.0, 1.0)

        self.assertAlmostEqual(white.l, 1.0, places=3)
        self.assertLess(white.c, 0.001)

    def test_known_blue_has_hue_near_264(self):
        # sRGB (0, 0, 1) é OKLCH ≈ (0.452, 0.313, 264.05), valor de referência do CSS Color 4.
        blue = srgb_to_oklch(0.0, 0.0, 1.0)

        self.assertAlmostEqual(blue.l, 0.452, places=2)
        self.assertAlmostEqual(blue.c, 0.313, places=2)
        self.assertAlmostEqual(blue.h, 264.05, delta=0.5)

    def test_oklch_to_linear_and_back_preserves_hue(self):
        action = Oklch(0.47, 0.13, 265)
        linear = oklch_to_linear_srgb(action)
        encoded = [12.92 * v if v <= 0.0031308 else 1.055 * v ** (1 / 2.4) - 0.055 for v in linear]

        back = srgb_to_oklch(*encoded)

        self.assertAlmostEqual(back.h, 265, delta=0.5)
        self.assertAlmostEqual(back.c, 0.13, places=2)


class HueDistanceTest(unittest.TestCase):
    def test_wraps_around_zero(self):
        self.assertEqual(hue_distance(355, 5), 10)

    def test_is_symmetric(self):
        self.assertEqual(hue_distance(85, 265), 180)
        self.assertEqual(hue_distance(265, 85), 180)

    def test_zero_for_same_hue(self):
        self.assertEqual(hue_distance(265, 265), 0)


class LightnessRampTest(unittest.TestCase):
    def test_keeps_hue_and_spans_the_lightness_range(self):
        ramp = lightness_ramp(hue=265, chroma=0.13, lightness=(0.3, 0.9), stops=5)

        self.assertEqual([round(stop.position, 2) for stop in ramp], [0.0, 0.25, 0.5, 0.75, 1.0])
        self.assertAlmostEqual(ramp[0].color.l, 0.3)
        self.assertAlmostEqual(ramp[-1].color.l, 0.9)
        self.assertTrue(all(stop.color.h == 265 for stop in ramp))

    def test_reduces_chroma_to_stay_inside_srgb(self):
        # Croma 0.13 a L=0.9 no matiz 265 sai do sRGB; o tom tem de caber sem mudar o matiz.
        ramp = lightness_ramp(hue=265, chroma=0.13, lightness=(0.3, 0.9), stops=5)

        top = ramp[-1].color
        self.assertLess(top.c, 0.13)
        self.assertTrue(all(-1e-6 <= v <= 1 + 1e-6 for v in oklch_to_linear_srgb(top)))

    def test_rejects_less_than_two_stops(self):
        with self.assertRaises(ValueError):
            lightness_ramp(hue=265, chroma=0.13, lightness=(0.3, 0.9), stops=1)


class CheckHuesTest(unittest.TestCase):
    ALLOWED = (265.0, 85.0)

    def check(self, pixels, max_outlier_fraction=0.0):
        return check_hues(
            pixels, allowed_hues=self.ALLOWED, tolerance=12, min_chroma=0.02, max_outlier_fraction=max_outlier_fraction
        )

    def test_passes_when_every_chromatic_pixel_is_near_an_allowed_hue(self):
        report = self.check([Oklch(0.5, 0.1, 270), Oklch(0.8, 0.03, 80)])

        self.assertTrue(report.passed)
        self.assertEqual(report.chromatic, 2)
        self.assertEqual(report.outliers, 0)

    def test_tolerance_is_inclusive_on_point_and_fails_off_point(self):
        self.assertTrue(self.check([Oklch(0.5, 0.1, 277)]).passed)
        self.assertFalse(self.check([Oklch(0.5, 0.1, 277.5)]).passed)

    def test_ignores_achromatic_pixels_whose_hue_is_noise(self):
        report = self.check([Oklch(0.9, 0.019, 150), Oklch(0.5, 0.1, 265)])

        self.assertTrue(report.passed)
        self.assertEqual(report.chromatic, 1)

    def test_fails_when_outliers_exceed_the_allowed_fraction(self):
        pixels = [Oklch(0.5, 0.1, 265)] * 97 + [Oklch(0.5, 0.1, 150)] * 3

        self.assertFalse(self.check(pixels, max_outlier_fraction=0.02).passed)
        self.assertTrue(self.check(pixels, max_outlier_fraction=0.03).passed)

    def test_all_neutral_image_passes_with_zero_chromatic_pixels(self):
        # Imagem toda neutra (placeholder) não tem matiz a conferir: passa, com 0 pixels cromáticos.
        report = self.check([Oklch(0.9, 0.01, 85)])

        self.assertTrue(report.passed)
        self.assertEqual(report.chromatic, 0)

    def test_empty_image_fails(self):
        self.assertFalse(self.check([]).passed)

    def test_reports_median_hue_per_allowed_hue(self):
        report = self.check([Oklch(0.5, 0.1, 260), Oklch(0.5, 0.1, 266), Oklch(0.5, 0.1, 270), Oklch(0.8, 0.03, 85)])

        self.assertEqual(report.median_hue_by_target[265.0], 266)
        self.assertEqual(report.median_hue_by_target[85.0], 85)


if __name__ == "__main__":
    unittest.main()
