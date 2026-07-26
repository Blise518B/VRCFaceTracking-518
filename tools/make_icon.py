"""Generates the VRCFaceTracking - Blise518B Edition icon.

House style (same as VRC Parameter Relay's tools/make_icon.py):
    white disc, thick black ring close to the edge, black glyph inside.

The glyph is NOT drawn by hand - it is *converted* from the real VRCFT artwork
in tools/source/, so every shape (the exact smile angle, the lens, the LED
bars, the eye and its pupil) keeps the original geometry. Only the colours are
reinterpreted as black and white.

How the conversion works
------------------------
The source art is two flat halves - a neutral grey machine side and a pink
avatar side - with brighter features on top. Sampling the real pixels gives
five well separated families:

    grey  rgb(128,128,128) / (152,152,152)   spread 0      -> background
    pink  rgb(240,152,160)                   r-g approx 85 -> background
    green rgb(120,224,24) / (144,240,32)     g-r approx100 -> lens + LEDs
    white rgb(240,240,240)                   spread <8     -> eye + smile
    black rgb(8,8,8)                         lum 8         -> pupil

So each pixel is mapped by which family it belongs to, per side of the split:

    machine side: green -> black, near-white -> white (the lens gloss dots),
                  everything else -> transparent
    avatar side:  near-white -> black (eye + smile), near-black -> white
                  (the pupil, knocked out of the eye), pink -> transparent

Classification happens at full source resolution and the result is then scaled
down smoothly, which is what gives the edges their anti-aliasing.

Usage:
    python tools/make_icon.py                    # write all app assets
    python tools/make_icon.py --divider          # add the centre split line
    python tools/make_icon.py --scale 0.66       # glyph size inside the ring
    python tools/make_icon.py --preview DIR      # only write preview PNGs
"""
from __future__ import annotations

import argparse
import os
import struct
import sys
from pathlib import Path

os.environ.setdefault("QT_QPA_PLATFORM", "offscreen")

from PySide6.QtCore import QBuffer, QByteArray, QPointF, QRectF, Qt
from PySide6.QtGui import QColor, QImage, QPainter, QPainterPath, QPen
from PySide6.QtWidgets import QApplication

ROOT = Path(__file__).resolve().parent.parent
ASSETS = ROOT / "VRCFaceTracking" / "Assets"
# Pristine copy of the upstream artwork. Kept out of Assets/ on purpose: this
# script *overwrites* everything in Assets/, so reading the source from there
# would destroy it on the first run.
SOURCE = Path(__file__).resolve().parent / "source" / "vrcft-logo-600.png"

BLACK = QColor("#0b0b0b")
WHITE = QColor("#ffffff")
TRANSPARENT = QColor(0, 0, 0, 0)

# Ring geometry, matched to the reference icon (256px design space)
RING_RECT = (12.0, 12.0, 232.0, 232.0)
RING_WIDTH = 15.0
DEFAULT_GLYPH_SCALE = 0.62

ICO_SIZES = [16, 24, 32, 48, 64, 128, 256]


# ---------------------------------------------------------------- conversion


def _lum(r: int, g: int, b: int) -> float:
    return 0.299 * r + 0.587 * g + 0.114 * b


def _family(r: int, g: int, b: int) -> str:
    """Which colour family a source pixel belongs to (diagnostics / sanity check)."""
    spread = max(r, g, b) - min(r, g, b)
    if g - r > 40 and g - b > 80:
        return "green"
    if min(r, g, b) > 200 and spread < 30:
        return "white"
    if _lum(r, g, b) < 60:
        return "black"
    if r - g > 40:
        return "pink"
    return "grey"


def _is_green(r: int, g: int, b: int) -> bool:
    """The lens and LED bars."""
    return g - r > 40 and g - b > 80


def _machine_is_background(r: int, g: int, b: int) -> bool:
    """The machine half's flat grey backdrop (neutral, rgb ~120-156).

    Defined as "is this the backdrop" rather than "is this a feature" on
    purpose: anti-aliased blends between the lens and the gloss dots land in no
    obvious colour family, and treating those as background punched visible
    holes through the glyph.
    """
    spread = max(r, g, b) - min(r, g, b)
    return spread < 25 and 100 < _lum(r, g, b) < 172


def _avatar_is_background(r: int, g: int, b: int) -> bool:
    """The avatar half's pink backdrop, including the darker nose nub."""
    return r - g > 30


def convert_glyph(source: QImage, invert_left: bool, simple: bool = True) -> QImage:
    """Map the coloured artwork onto a black/white/transparent glyph.

    With invert_left the machine half is rendered for placement on a black
    background: its features come out white, so the two halves of the icon read
    as photo negatives of each other - which is how the original's grey|pink
    split translates into pure black and white.

    In simple mode (the default) every feature on a side collapses to that
    side's single colour: the lens gloss dots melt into a solid lens and the
    pupil into a solid eye. Note they have to be actively painted over rather
    than skipped - leaving them unpainted would make them transparent and the
    background would show through as holes anyway.
    """
    width, height = source.width(), source.height()
    split_x = width / 2.0  # the grey|pink boundary sits on the centre line

    out = QImage(width, height, QImage.Format_ARGB32)
    out.fill(Qt.transparent)

    counts = {"green": 0, "white": 0, "black": 0, "pink": 0, "grey": 0}

    for y in range(height):
        for x in range(width):
            c = source.pixelColor(x, y)
            if c.alpha() < 128:
                continue

            fam = _family(c.red(), c.green(), c.blue())
            counts[fam] += 1

            r, g, b = c.red(), c.green(), c.blue()

            if x < split_x:
                # machine side: the lens and LED bars, with gloss dots on top
                if _machine_is_background(r, g, b):
                    continue
                base = WHITE if invert_left else BLACK
                detail = BLACK if invert_left else WHITE
                keep = simple or _is_green(r, g, b)
                out.setPixelColor(x, y, base if keep else detail)
            else:
                # avatar side: the eye and smile, with the pupil inside the eye
                if _avatar_is_background(r, g, b):
                    continue
                is_pupil = _lum(r, g, b) < 100
                out.setPixelColor(x, y, WHITE if (is_pupil and not simple) else BLACK)

    if counts["green"] == 0 or counts["pink"] == 0:
        raise SystemExit(
            f"{SOURCE.name} does not look like the original coloured artwork "
            f"(no green/pink found: {counts}). Restore it with:\n"
            "  git show <commit>:VRCFaceTracking/Assets/Square150x150Logo.scale-400.png "
            "> tools/source/vrcft-logo-600.png"
        )

    return out


def crop_about_split(img: QImage) -> QImage:
    """Trim transparent margins, keeping the artwork's split line centred.

    The crop is padded so the source's vertical split stays exactly in the
    middle of the result. That way, centring the glyph in the icon also lines
    the seam up with the icon's centre - which the split style depends on, and
    which a plain content-bbox crop would throw off by a few pixels.
    """
    min_x, min_y = img.width(), img.height()
    max_x = max_y = -1

    for y in range(img.height()):
        for x in range(img.width()):
            if img.pixelColor(x, y).alpha() >= 8:
                if x < min_x:
                    min_x = x
                if x > max_x:
                    max_x = x
                if y < min_y:
                    min_y = y
                if y > max_y:
                    max_y = y

    if max_x < 0:
        raise SystemExit("converted glyph is empty")

    split = img.width() // 2
    reach = max(split - min_x, max_x - split + 1)
    left = max(split - reach, 0)
    right = min(split + reach, img.width())

    return img.copy(left, min_y, right - left, max_y - min_y + 1)


# ---------------------------------------------------------------- rendering


def draw(size: int, glyph: QImage, scale: float = DEFAULT_GLYPH_SCALE,
         split: bool = True) -> QImage:
    img = QImage(size, size, QImage.Format_ARGB32)
    img.fill(Qt.transparent)
    p = QPainter(img)
    p.setRenderHint(QPainter.Antialiasing)
    p.setRenderHint(QPainter.SmoothPixmapTransform)
    s = size / 256.0

    ring_rect = QRectF(*(v * s for v in RING_RECT))
    # The ring is stroked centred on its path, so its outer edge sits half a
    # pen width beyond. The body is filled out to there and the ring is stroked
    # last, on top: if the black half stopped at the ring's inner edge instead,
    # its anti-aliased boundary and the ring's would share a radius and their
    # partial coverages would not sum to opaque, leaving a faint hairline.
    grow = RING_WIDTH / 2.0
    outer_rect = QRectF(
        (RING_RECT[0] - grow) * s, (RING_RECT[1] - grow) * s,
        (RING_RECT[2] + RING_WIDTH) * s, (RING_RECT[3] + RING_WIDTH) * s,
    )
    outer = QPainterPath()
    outer.addEllipse(outer_rect)

    p.setPen(Qt.NoPen)
    p.setBrush(WHITE)
    p.drawEllipse(outer_rect)

    if split:
        # Machine half inverted: fill the left half of the body black, so the
        # seam down the middle carries the original's split.
        p.save()
        p.setClipPath(outer)
        p.setBrush(BLACK)
        p.drawRect(QRectF(0, 0, size / 2.0, size))
        p.restore()

    # Glyph, scaled to fit inside the ring and centred. crop_about_split keeps
    # the artwork's seam at the glyph's centre, so it lands on size/2 here.
    box = size * scale
    scaled = glyph.scaled(
        int(round(box)), int(round(box)),
        Qt.KeepAspectRatio, Qt.SmoothTransformation,
    )
    p.drawImage(
        QPointF((size - scaled.width()) / 2.0, (size - scaled.height()) / 2.0),
        scaled,
    )

    if split:
        # The third LED bar runs flush into the split in the source art
        # (x 257-299 of 600), so once inverted its white would bleed straight
        # into the white avatar half. A hairline seam keeps the two halves
        # separate and, unlike a fixed pixel gutter, stays visible at 16px.
        p.save()
        p.setClipPath(outer)
        p.setPen(QPen(BLACK, max(1.0, 3.0 * s)))
        p.drawLine(QPointF(size / 2.0, 0), QPointF(size / 2.0, size))
        p.restore()

    # Ring last, so it covers the body's edge and any glyph overflow
    p.setBrush(Qt.NoBrush)
    p.setPen(QPen(BLACK, RING_WIDTH * s))
    p.drawEllipse(ring_rect)

    p.end()
    return img


def contact_sheet(glyph: QImage, scale: float, split: bool) -> QImage:
    """Preview strip: big version plus the sizes Windows actually shows."""
    sizes = [256, 96, 48, 32, 16]
    pad = 12
    width = sum(sizes) + pad * (len(sizes) + 1)
    sheet = QImage(width, 256 + pad * 2, QImage.Format_ARGB32)
    sheet.fill(QColor("#2b2b2b"))
    p = QPainter(sheet)
    cursor = pad
    for size in sizes:
        p.drawImage(cursor, pad + (256 - size) // 2, draw(size, glyph, scale, split))
        cursor += size + pad
    p.end()
    return sheet


# ---------------------------------------------------------------- ico writing


def _png_bytes(img: QImage) -> bytes:
    # Hold our own reference to the QByteArray: QBuffer only keeps a pointer to
    # it, so letting a temporary get collected crashes the interpreter.
    storage = QByteArray()
    buf = QBuffer(storage)
    buf.open(QBuffer.WriteOnly)
    if not img.save(buf, "PNG"):
        raise SystemExit("failed to encode PNG")
    buf.close()
    return bytes(storage)


def write_ico(path: Path, images: list[tuple[int, QImage]]) -> None:
    """Multi-resolution ICO with PNG-compressed entries (Vista+).

    Qt's ICO writer only emits a single image, which leaves Windows to
    downscale 256px art for the 16px taskbar slot. Packing each size by hand
    keeps the small sizes crisp.
    """
    blobs = [(size, _png_bytes(img)) for size, img in images]

    header = struct.pack("<HHH", 0, 1, len(blobs))
    offset = len(header) + 16 * len(blobs)
    directory = b""
    for size, data in blobs:
        directory += struct.pack(
            "<BBBBHHII",
            0 if size >= 256 else size,  # 0 encodes 256
            0 if size >= 256 else size,
            0,  # palette size (0 = no palette)
            0,  # reserved
            1,  # colour planes
            32,  # bits per pixel
            len(data),
            offset,
        )
        offset += len(data)

    path.write_bytes(header + directory + b"".join(blob for _, blob in blobs))


# ---------------------------------------------------------------- app assets

SQUARE_SETS = {
    "Square44x44Logo": 44,
    "Square150x150Logo": 150,
    "StoreLogo": 50,
    "SmallTile": 71,
    "LargeTile": 310,
}
SCALES = {"scale-100": 1.0, "scale-125": 1.25, "scale-150": 1.5,
          "scale-200": 2.0, "scale-400": 4.0}
TARGET_SIZES = [16, 24, 32, 48, 256]


def write_assets(glyph: QImage, scale: float, split: bool) -> list[Path]:
    written: list[Path] = []

    ico = ASSETS / "WindowIcon.ico"
    write_ico(ico, [(s, draw(s, glyph, scale, split)) for s in ICO_SIZES])
    written.append(ico)

    big = ASSETS / "AppIcon.png"
    draw(512, glyph, scale, split).save(str(big), "PNG")
    written.append(big)

    for base_name, base_px in SQUARE_SETS.items():
        for suffix, factor in SCALES.items():
            path = ASSETS / f"{base_name}.{suffix}.png"
            if path.exists():
                px = int(round(base_px * factor))
                draw(px, glyph, scale, split).save(str(path), "PNG")
                written.append(path)

    for px in TARGET_SIZES:
        for name in (
            f"Square44x44Logo.targetsize-{px}.png",
            f"Square44x44Logo.targetsize-{px}_altform-unplated.png",
            f"Square44x44Logo.altform-unplated_targetsize-{px}.png",
            f"Square44x44Logo.altform-lightunplated_targetsize-{px}.png",
        ):
            path = ASSETS / name
            if path.exists():
                draw(px, glyph, scale, split).save(str(path), "PNG")
                written.append(path)

    return written


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--scale", type=float, default=DEFAULT_GLYPH_SCALE,
                        help="glyph size as a fraction of the icon (default 0.62)")
    parser.add_argument("--no-split", action="store_true",
                        help="don't invert the machine half; put both halves on white")
    parser.add_argument("--details", action="store_true",
                        help="keep the lens gloss dots and the eye's pupil")
    parser.add_argument("--preview", metavar="DIR",
                        help="only write preview PNGs here")
    args = parser.parse_args()

    QApplication(sys.argv[:1])

    if not SOURCE.exists():
        raise SystemExit(f"missing source artwork: {SOURCE}")

    source = QImage(str(SOURCE))
    if source.isNull():
        raise SystemExit(f"could not read {SOURCE}")

    simple = not args.details

    if args.preview:
        out = Path(args.preview)
        out.mkdir(parents=True, exist_ok=True)
        for split in (True, False):
            tag = "split" if split else "plain"
            glyph = crop_about_split(convert_glyph(source, invert_left=split, simple=simple))
            contact_sheet(glyph, args.scale, split).save(str(out / f"preview-{tag}.png"), "PNG")
            draw(512, glyph, args.scale, split).save(str(out / f"icon-{tag}-512.png"), "PNG")
        print(f"previews written to {out} (simple={simple})")
        return

    split = not args.no_split
    glyph = crop_about_split(convert_glyph(source, invert_left=split, simple=simple))
    print(f"converted glyph: {glyph.width()}x{glyph.height()} from {SOURCE.name}")

    written = write_assets(glyph, args.scale, split)
    print(f"wrote {len(written)} files (scale={args.scale}, split={split}, simple={simple})")
    print(f"  {written[0]}")
    print(f"  {written[1]}")


if __name__ == "__main__":
    main()
