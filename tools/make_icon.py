"""Generates the VRCFaceTracking - Blise518B Edition icon.

House style (same as VRC Parameter Relay's tools/make_icon.py):
    white disc, thick black ring close to the edge, black glyph inside.

The glyph is a monochrome reading of the VRCFT mark: a split face, machine
half (camera lens + status LEDs) against avatar half (eye + smile).

    split  - the inner disc is split down the middle: machine half solid black
             with its lens and LEDs knocked out white, avatar half white with
             eye and smile in black. Reads clearly down to 16px.
    float  - the same features floating inside the ring with a white margin,
             closer to the relay icon's lighter feel.

Usage:
    python tools/make_icon.py                 # write all app assets (split)
    python tools/make_icon.py --variant float
    python tools/make_icon.py --preview DIR   # just write preview PNGs
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

BLACK = QColor("#0b0b0b")
WHITE = QColor("#ffffff")

# Ring geometry, matched to the reference icon (256px design space)
RING_RECT = (12.0, 12.0, 232.0, 232.0)
RING_WIDTH = 15.0


def _dome(cx: float, cy: float, w: float, h: float) -> QPainterPath:
    """Eye outline: domed top, gently rounded bottom, like the VRCFT mark."""
    path = QPainterPath()
    half = w / 2.0
    top = cy - h / 2.0
    bottom = cy + h / 2.0
    path.moveTo(cx - half, bottom - h * 0.18)
    path.cubicTo(cx - half, top, cx + half, top, cx + half, bottom - h * 0.18)
    path.cubicTo(cx + half * 0.72, bottom, cx - half * 0.72, bottom, cx - half, bottom - h * 0.18)
    path.closeSubpath()
    return path


def _smile(p: QPainter, s: float, width: float, color: QColor) -> None:
    """Upturned mouth on the avatar side."""
    path = QPainterPath()
    path.moveTo(148 * s, 150 * s)
    path.cubicTo(160 * s, 178 * s, 192 * s, 172 * s, 202 * s, 143 * s)
    p.setBrush(Qt.NoBrush)
    p.setPen(QPen(color, width * s, Qt.SolidLine, Qt.RoundCap))
    p.drawPath(path)


def _machine_features(p: QPainter, s: float, color: QColor) -> None:
    """Camera lens plus the row of status LEDs."""
    p.setPen(Qt.NoPen)
    p.setBrush(color)
    # lens
    p.drawEllipse(QRectF(56 * s, 74 * s, 62 * s, 62 * s))
    # three LED bars
    for x in (58, 80, 102):
        p.drawRoundedRect(QRectF(x * s, 150 * s, 14 * s, 34 * s), 3 * s, 3 * s)


def _avatar_features(p: QPainter, s: float, color: QColor, hole: QColor) -> None:
    """Eye (with pupil knocked out) and smile."""
    p.setPen(Qt.NoPen)
    p.setBrush(color)
    p.drawPath(_dome(172 * s, 106 * s, 66 * s, 78 * s))

    # pupil, offset toward the nose so it reads as a looking eye
    p.setBrush(hole)
    p.drawEllipse(QRectF(155 * s, 88 * s, 30 * s, 34 * s))

    _smile(p, s, 15.0, color)


def draw(size: int, variant: str = "split") -> QImage:
    img = QImage(size, size, QImage.Format_ARGB32)
    img.fill(Qt.transparent)
    p = QPainter(img)
    p.setRenderHint(QPainter.Antialiasing)
    s = size / 256.0

    # White disc with the thick black ring
    x, y, w, h = (v * s for v in RING_RECT)
    p.setBrush(WHITE)
    p.setPen(QPen(BLACK, RING_WIDTH * s))
    p.drawEllipse(QRectF(x, y, w, h))

    if variant == "split":
        # Clip everything to just inside the ring
        inset = RING_RECT[0] + RING_WIDTH / 2.0
        span = RING_RECT[2] - RING_WIDTH
        inner = QPainterPath()
        inner.addEllipse(QRectF(inset * s, inset * s, span * s, span * s))
        p.save()
        p.setClipPath(inner)

        # Machine half: solid black, features knocked out in white
        p.setPen(Qt.NoPen)
        p.setBrush(BLACK)
        p.drawRect(QRectF(0, 0, 128 * s, size))
        _machine_features(p, s, WHITE)
        p.restore()

        # Avatar half: black features straight onto the white disc
        _avatar_features(p, s, BLACK, WHITE)
    else:
        # Everything black, floating, with a centre divider echoing the split
        _machine_features(p, s, BLACK)
        _avatar_features(p, s, BLACK, WHITE)
        p.setPen(QPen(BLACK, 6 * s, Qt.SolidLine, Qt.RoundCap))
        p.drawLine(QPointF(131 * s, 70 * s), QPointF(131 * s, 188 * s))

    p.end()
    return img


def contact_sheet(variant: str) -> QImage:
    """Preview strip: big version plus the sizes Windows actually shows."""
    sizes = [256, 96, 48, 32, 16]
    pad = 12
    width = sum(sizes) + pad * (len(sizes) + 1)
    height = 256 + pad * 2
    sheet = QImage(width, height, QImage.Format_ARGB32)
    sheet.fill(QColor("#2b2b2b"))
    p = QPainter(sheet)
    cursor = pad
    for size in sizes:
        icon = draw(size, variant)
        p.drawImage(cursor, pad + (256 - size) // 2, icon)
        cursor += size + pad
    p.end()
    return sheet


# UWP/MSIX asset sizes derived from each filename's scale factor
SQUARE_SETS = {
    "Square44x44Logo": 44,
    "Square150x150Logo": 150,
    "StoreLogo": 50,
    "SmallTile": 71,
    "LargeTile": 310,
}
SCALES = {"scale-100": 1.0, "scale-125": 1.25, "scale-150": 1.5, "scale-200": 2.0, "scale-400": 4.0}
TARGET_SIZES = [16, 24, 32, 48, 256]
ICO_SIZES = [16, 24, 32, 48, 64, 128, 256]


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


def write_ico(path: Path, variant: str, sizes: list[int]) -> None:
    """Multi-resolution ICO with PNG-compressed entries (Vista+).

    Qt's ICO writer only emits a single image, which leaves Windows to
    downscale 256px art for the 16px taskbar slot. Drawing each size natively
    and packing them by hand keeps the small sizes crisp.
    """
    images = [_png_bytes(draw(size, variant)) for size in sizes]

    header = struct.pack("<HHH", 0, 1, len(sizes))
    offset = len(header) + 16 * len(sizes)
    directory = b""
    for size, data in zip(sizes, images):
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

    path.write_bytes(header + directory + b"".join(images))


def write_assets(variant: str) -> list[Path]:
    written: list[Path] = []

    # The icon that actually shows in the window, taskbar and alt-tab
    ico = ASSETS / "WindowIcon.ico"
    write_ico(ico, variant, ICO_SIZES)
    written.append(ico)

    # Handy standalone copy for READMEs / sharing
    big = ASSETS / "AppIcon.png"
    draw(512, variant).save(str(big), "PNG")
    written.append(big)

    for base_name, base_px in SQUARE_SETS.items():
        for suffix, factor in SCALES.items():
            px = int(round(base_px * factor))
            path = ASSETS / f"{base_name}.{suffix}.png"
            if path.exists():
                draw(px, variant).save(str(path), "PNG")
                written.append(path)

    # Square44x44Logo also ships explicit target sizes and unplated variants
    for px in TARGET_SIZES:
        for name in (
            f"Square44x44Logo.targetsize-{px}.png",
            f"Square44x44Logo.targetsize-{px}_altform-unplated.png",
            f"Square44x44Logo.altform-unplated_targetsize-{px}.png",
            f"Square44x44Logo.altform-lightunplated_targetsize-{px}.png",
        ):
            path = ASSETS / name
            if path.exists():
                draw(px, variant).save(str(path), "PNG")
                written.append(path)

    return written


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--variant", default="split", choices=["split", "float"])
    parser.add_argument("--preview", metavar="DIR", help="only write preview PNGs here")
    args = parser.parse_args()

    QApplication(sys.argv[:1])

    if args.preview:
        out = Path(args.preview)
        out.mkdir(parents=True, exist_ok=True)
        for variant in ("split", "float"):
            contact_sheet(variant).save(str(out / f"preview-{variant}.png"), "PNG")
            draw(512, variant).save(str(out / f"icon-{variant}-512.png"), "PNG")
        print(f"previews written to {out}")
        return

    written = write_assets(args.variant)
    print(f"wrote {len(written)} files (variant: {args.variant})")
    print(f"  {written[0]}")
    print(f"  {written[1]}")


if __name__ == "__main__":
    main()
