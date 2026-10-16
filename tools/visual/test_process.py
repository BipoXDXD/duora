"""Testes do processamento das fontes com imagens sintéticas (não dependem de out/source)."""

import dataclasses
import tempfile
import unittest
from pathlib import Path

from PIL import Image, ImageDraw

from catalog import CatalogObject, load_catalog
from palette import Oklch, hue_distance, srgb_to_oklch
from process import (
    ProcessError,
    chroma_key,
    draw_placeholder,
    estimate_key_color,
    frame,
    process_object,
    render,
    render_manifest,
    review_hues,
)
from recolor import Recolorer

GREEN = (10, 248, 2)
INDIGO = (40, 50, 130)
OFF_WHITE = (238, 232, 220)
WARM_GLOW = (250, 236, 170)
RED = (220, 30, 30)
ROLES = {"action": Oklch(0.47, 0.13, 265), "neutral": Oklch(0.47, 0.03, 85)}
HUE_CHECK = {"toleranceDegrees": 12, "minChroma": 0.02, "maxOutlierFraction": 0.02}
INNER, OUTER = 0.08, 0.16


def recolorer():
    return Recolorer(roles=ROLES, lightness=(0.32, 0.96), min_chroma=0.04, action_hue_window=50)


def on_green(size=64, box=(16, 16, 48, 48), fill=INDIGO):
    image = Image.new("RGB", (size, size), GREEN)
    ImageDraw.Draw(image).rectangle(box, fill=fill)
    return image


def blend(color, background, coverage):
    return tuple(round(c * coverage + b * (1 - coverage)) for c, b in zip(color, background))


class EstimateKeyColorTest(unittest.TestCase):
    def test_key_is_the_border_color_even_with_an_object_in_the_middle(self):
        self.assertEqual(estimate_key_color(on_green()), GREEN)

    def test_key_ignores_an_object_touching_a_minority_of_the_border(self):
        image = on_green(box=(0, 0, 10, 10))

        self.assertEqual(estimate_key_color(image), GREEN)

    def test_background_that_is_not_chroma_green_is_rejected(self):
        with self.assertRaisesRegex(ProcessError, "verde chroma"):
            estimate_key_color(Image.new("RGB", (16, 16), (128, 128, 128)))


class ChromaKeyTest(unittest.TestCase):
    def test_background_becomes_transparent_and_object_stays_opaque(self):
        keyed = chroma_key(on_green(), GREEN, INNER, OUTER)

        self.assertEqual(keyed.mode, "RGBA")
        self.assertEqual(rgba_at(keyed, (2, 2))[3], 0)
        self.assertEqual(rgba_at(keyed, (32, 32)), (*INDIGO, 255))

    def test_slightly_varying_background_inside_inner_band_is_fully_transparent(self):
        image = on_green()
        image.putpixel((3, 3), (13, 244, 2))

        self.assertEqual(rgba_at(chroma_key(image, GREEN, INNER, OUTER), (3, 3))[3], 0)

    def test_edge_pixel_gets_partial_alpha(self):
        image = on_green()
        image.putpixel((15, 32), blend(INDIGO, GREEN, 0.15))

        alpha = rgba_at(chroma_key(image, GREEN, INNER, OUTER), (15, 32))[3]

        self.assertGreater(alpha, 0)
        self.assertLess(alpha, 255)

    def test_edge_pixel_loses_the_green_spill(self):
        image = on_green()
        image.putpixel((15, 32), blend(INDIGO, GREEN, 0.15))

        r, g, b, _ = rgba_at(chroma_key(image, GREEN, INNER, OUTER), (15, 32))

        self.assertLessEqual(g, max(r, b))

    def test_opaque_pixel_with_green_reflection_is_despilled(self):
        image = on_green(fill=(200, 235, 190))

        r, g, b, alpha = rgba_at(chroma_key(image, GREEN, INNER, OUTER), (32, 32))

        self.assertEqual(alpha, 255)
        self.assertEqual((r, g, b), (200, 200, 190))

    def test_warm_white_is_untouched_by_despill(self):
        keyed = chroma_key(on_green(fill=OFF_WHITE), GREEN, INNER, OUTER)

        self.assertEqual(rgba_at(keyed, (32, 32)), (*OFF_WHITE, 255))


class FrameTest(unittest.TestCase):
    def keyed(self, box):
        return chroma_key(on_green(size=200, box=box), GREEN, INNER, OUTER)

    def test_off_center_object_is_centered_on_a_square_canvas_with_margin(self):
        framed = frame(self.keyed((10, 20, 59, 69)), margin_fraction=0.1)

        self.assertEqual(framed.width, framed.height)
        left, top, right, bottom = object_box(framed)
        self.assertEqual((left, top), (framed.width - right, framed.height - bottom))
        self.assertAlmostEqual(left / framed.width, 0.1, delta=0.02)

    def test_wide_object_keeps_its_margin_on_the_long_side(self):
        framed = frame(self.keyed((10, 80, 189, 119)), margin_fraction=0.06)

        left, top, right, _ = object_box(framed)
        self.assertAlmostEqual(left / framed.width, 0.06, delta=0.01)
        self.assertGreater(top, left)
        self.assertEqual(right, framed.width - left)

    def test_image_without_object_is_rejected(self):
        empty = chroma_key(Image.new("RGB", (32, 32), GREEN), GREEN, INNER, OUTER)

        with self.assertRaisesRegex(ProcessError, "nenhum pixel"):
            frame(empty, margin_fraction=0.06)

    def test_margin_must_leave_room_for_the_object(self):
        with self.assertRaisesRegex(ProcessError, "marginFraction"):
            frame(self.keyed((10, 10, 50, 50)), margin_fraction=0.5)


class RenderTest(unittest.TestCase):
    def test_renders_at_the_requested_square_size(self):
        framed = frame(chroma_key(on_green(), GREEN, INNER, OUTER), margin_fraction=0.06)

        self.assertEqual(render(framed, 40, recolorer()).size, (40, 40))

    def test_object_pixels_take_the_palette_hues(self):
        framed = frame(chroma_key(on_green(), GREEN, INNER, OUTER), margin_fraction=0.06)

        r, g, b, _ = rgba_at(render(framed, 40, recolorer()), (20, 20))

        self.assertLess(hue_distance(srgb_to_oklch(r / 255, g / 255, b / 255).h, 265), 3)

    def test_transparent_pixels_stay_transparent(self):
        framed = frame(chroma_key(on_green(), GREEN, INNER, OUTER), margin_fraction=0.06)

        self.assertEqual(rgba_at(render(framed, 40, recolorer()), (0, 0))[3], 0)


class CatalogRecolorTest(unittest.TestCase):
    def test_indigo_in_shadow_stays_action_with_the_catalog_threshold(self):
        # Sombra do índigo na fonte: matiz azul, croma ~0,03. Com limiar alto, virava listra bege.
        shadow = srgb_to_oklch(34 / 255, 36 / 255, 52 / 255)
        catalog = load_catalog()
        classify = catalog.palette["classify"]
        recolorer = Recolorer(ROLES, (0.32, 0.96), classify["minChroma"], classify["actionHueWindowDegrees"])

        self.assertLess(shadow.c, 0.04)
        self.assertEqual(recolorer.classify(shadow), "action")


class ReviewHuesTest(unittest.TestCase):
    def two_tone(self, accent):
        image = on_green(size=96, box=(10, 10, 85, 85), fill=OFF_WHITE)
        ImageDraw.Draw(image).rectangle((30, 30, 65, 65), fill=accent)
        return frame(chroma_key(image, GREEN, INNER, OUTER), margin_fraction=0.06)

    def test_recolored_two_tone_object_passes(self):
        report = review_hues(render(self.two_tone(INDIGO), 64, recolorer()), ROLES, HUE_CHECK)

        self.assertTrue(report.passed)

    def test_warm_glow_is_pulled_to_the_neutral_hue_and_passes(self):
        report = review_hues(render(self.two_tone(WARM_GLOW), 64, recolorer()), ROLES, HUE_CHECK)

        self.assertTrue(report.passed)

    def test_red_area_without_recolor_fails(self):
        report = review_hues(self.two_tone(RED), ROLES, HUE_CHECK)

        self.assertFalse(report.passed)

    def test_transparent_pixels_are_not_counted(self):
        report = review_hues(Image.new("RGBA", (8, 8), (*RED, 0)), ROLES, HUE_CHECK)

        self.assertEqual(report.total, 0)


class PlaceholderTest(unittest.TestCase):
    def test_placeholder_is_an_opaque_neutral_pebble_on_transparency(self):
        pebble = draw_placeholder(256)

        self.assertEqual(pebble.mode, "RGBA")
        self.assertEqual(rgba_at(pebble, (0, 0))[3], 0)
        r, g, b, alpha = rgba_at(pebble, (128, 140))
        self.assertEqual(alpha, 255)
        self.assertLess(srgb_to_oklch(r / 255, g / 255, b / 255).c, 0.04)


class ProcessObjectTest(unittest.TestCase):
    def setUp(self):
        self.catalog = load_catalog()
        self.tmp = Path(tempfile.mkdtemp())
        self.source_dir = self.tmp / "source"
        self.output_dir = self.tmp / "assets"
        self.source_dir.mkdir()

    def generated(self):
        return CatalogObject("compass", "generated", "experience", "teste", "a compass")

    def test_writes_1x_and_2x_webp_at_the_css_size_of_the_use(self):
        on_green(size=256, box=(60, 60, 200, 200)).save(self.source_dir / "compass.png")

        written = process_object(self.catalog, self.generated(), ROLES, self.source_dir, self.output_dir)

        self.assertEqual([path.name for path in written], ["compass.webp", "compass@2x.webp"])
        with Image.open(written[0]) as one, Image.open(written[1]) as two:
            self.assertEqual((one.format, one.size, one.mode), ("WEBP", (200, 200), "RGBA"))
            self.assertEqual(two.size, (400, 400))

    def test_simple_object_keeps_the_lossy_encoding(self):
        on_green(size=256, box=(60, 60, 200, 200)).save(self.source_dir / "compass.png")

        written = process_object(self.catalog, self.generated(), ROLES, self.source_dir, self.output_dir)

        self.assertIn(b"VP8 ", webp_chunks(written[0]))

    def test_detail_lost_by_lossy_compression_falls_back_to_lossless_instead_of_failing(self):
        image = on_green(size=256, box=(20, 20, 236, 236), fill=OFF_WHITE)
        draw = ImageDraw.Draw(image)
        for x in range(24, 232, 6):
            draw.line((x, 24, x, 232), fill=INDIGO, width=2)
        image.save(self.source_dir / "compass.png")
        # 4 graus sem outlier: a quantização de 8 bits cabe, a subamostragem de croma do WebP com perda não.
        strict = {"toleranceDegrees": 4, "minChroma": 0.02, "maxOutlierFraction": 0}
        catalog = dataclasses.replace(self.catalog, hue_check=strict)

        written = process_object(catalog, self.generated(), ROLES, self.source_dir, self.output_dir)

        self.assertEqual([webp_chunks(path)[-1] for path in written], [b"VP8L", b"VP8L"])

    def test_missing_source_names_the_object(self):
        with self.assertRaisesRegex(ProcessError, "compass"):
            process_object(self.catalog, self.generated(), ROLES, self.source_dir, self.output_dir)

    def test_hue_failure_writes_nothing(self):
        on_green(size=256, box=(60, 60, 200, 200)).save(self.source_dir / "compass.png")
        zero_tolerance = {"toleranceDegrees": 0, "minChroma": 0.0, "maxOutlierFraction": 0}
        catalog = dataclasses.replace(self.catalog, hue_check=zero_tolerance)

        with self.assertRaisesRegex(ProcessError, "matiz"):
            process_object(catalog, self.generated(), ROLES, self.source_dir, self.output_dir)
        self.assertFalse(self.output_dir.exists() and any(self.output_dir.iterdir()))

    def test_procedural_object_needs_no_source(self):
        placeholder = self.catalog.find("placeholder")

        written = process_object(self.catalog, placeholder, ROLES, self.source_dir, self.output_dir)

        self.assertEqual([path.name for path in written], ["placeholder.webp", "placeholder@2x.webp"])


class ManifestTest(unittest.TestCase):
    def test_manifest_imports_both_densities_and_carries_the_css_size(self):
        text = render_manifest(load_catalog())

        self.assertIn("import cardPair1x from './illustrations/card-pair.webp'", text)
        self.assertIn("import cardPair2x from './illustrations/card-pair@2x.webp'", text)
        self.assertIn("'card-pair': { cssPx: 200, src1x: cardPair1x, src2x: cardPair2x },", text)
        self.assertIn("compass: { cssPx: 200, src1x: compass1x, src2x: compass2x },", text)

    def test_manifest_lists_every_catalog_object_in_name_order(self):
        catalog = load_catalog()
        text = render_manifest(catalog)

        positions = [text.index(f"src1x: {_camel(item.name)}1x") for item in catalog.objects]
        names = [item.name for item in catalog.objects]
        self.assertEqual([n for _, n in sorted(zip(positions, names))], sorted(names))

    def test_committed_manifest_matches_the_catalog(self):
        from process import MANIFEST_PATH

        self.assertEqual(MANIFEST_PATH.read_text(encoding="utf-8"), render_manifest(load_catalog()))


def object_box(image):
    box = image.getchannel("A").getbbox()
    if box is None:
        raise AssertionError("imagem sem pixel visível")
    return box


def rgba_at(image, point):
    """Pixel RGBA como tupla de ints (o `getpixel` do Pillow é tipado como união de vários formatos)."""
    pixel = image.getpixel(point)
    if not isinstance(pixel, tuple) or len(pixel) != 4:
        raise AssertionError(f"esperava RGBA em {point}, veio {pixel!r}")
    return tuple(int(channel) for channel in pixel)


def webp_chunks(path):
    """FourCCs dos chunks RIFF: `VP8 ` é WebP com perda, `VP8L` é sem perda."""
    data = path.read_bytes()
    chunks, offset = [], 12
    while offset + 8 <= len(data):
        size = int.from_bytes(data[offset + 4 : offset + 8], "little")
        chunks.append(data[offset : offset + 4])
        offset += 8 + size + (size & 1)
    return chunks


def _camel(name):
    head, *rest = name.split("-")
    return head + "".join(part.capitalize() for part in rest)


if __name__ == "__main__":
    unittest.main()
