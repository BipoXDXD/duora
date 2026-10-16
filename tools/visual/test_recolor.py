"""Testes da recoloração por pixel e do chroma key (Python puro, sem Pillow)."""

import unittest

from palette import Oklch, hue_distance, srgb_to_oklch
from recolor import Recolorer, key_alpha

ACTION = Oklch(0.47, 0.13, 265)
NEUTRAL = Oklch(0.47, 0.03, 85)


def recolorer():
    return Recolorer(
        roles={"action": ACTION, "neutral": NEUTRAL}, lightness=(0.32, 0.96), min_chroma=0.04, action_hue_window=50
    )


def as_oklch(rgb8):
    return srgb_to_oklch(*(v / 255 for v in rgb8))


class ClassifyTest(unittest.TestCase):
    def test_saturated_blue_is_action(self):
        self.assertEqual(recolorer().classify(Oklch(0.5, 0.15, 250)), "action")

    def test_hue_window_is_inclusive_on_point_and_excludes_off_point(self):
        self.assertEqual(recolorer().classify(Oklch(0.5, 0.15, 315)), "action")
        self.assertEqual(recolorer().classify(Oklch(0.5, 0.15, 316)), "neutral")

    def test_low_chroma_is_neutral_even_with_blue_hue(self):
        self.assertEqual(recolorer().classify(Oklch(0.5, 0.039, 265)), "neutral")

    def test_chroma_key_green_spill_is_neutral_not_action(self):
        self.assertEqual(recolorer().classify(srgb_to_oklch(0.2, 0.9, 0.2)), "neutral")


class RecolorTest(unittest.TestCase):
    def test_blue_pixel_takes_the_action_hue(self):
        result = as_oklch(recolorer().recolor((40, 50, 160)))

        self.assertLess(hue_distance(result.h, 265), 3)

    def test_off_white_pixel_takes_the_warm_neutral_hue(self):
        result = as_oklch(recolorer().recolor((236, 238, 242)))

        self.assertLess(hue_distance(result.h, 85), 6)
        self.assertLess(result.c, 0.04)

    def test_lightness_order_is_preserved_inside_the_configured_range(self):
        dark = as_oklch(recolorer().recolor((0, 0, 0)))
        light = as_oklch(recolorer().recolor((255, 255, 255)))

        self.assertAlmostEqual(dark.l, 0.32, delta=0.01)
        self.assertAlmostEqual(light.l, 0.96, delta=0.01)

    def test_missing_role_is_rejected(self):
        with self.assertRaisesRegex(ValueError, "neutral"):
            Recolorer(roles={"action": ACTION}, lightness=(0.3, 0.9), min_chroma=0.04, action_hue_window=50)


class KeyAlphaTest(unittest.TestCase):
    def test_background_colour_is_fully_transparent(self):
        self.assertEqual(key_alpha(0.0, inner=0.08, outer=0.16), 0.0)
        self.assertEqual(key_alpha(0.08, inner=0.08, outer=0.16), 0.0)

    def test_far_colour_is_fully_opaque(self):
        self.assertEqual(key_alpha(0.16, inner=0.08, outer=0.16), 1.0)
        self.assertEqual(key_alpha(0.5, inner=0.08, outer=0.16), 1.0)

    def test_between_thresholds_is_partial(self):
        self.assertAlmostEqual(key_alpha(0.12, inner=0.08, outer=0.16), 0.5)

    def test_rejects_inverted_thresholds(self):
        with self.assertRaises(ValueError):
            key_alpha(0.1, inner=0.16, outer=0.08)


if __name__ == "__main__":
    unittest.main()
