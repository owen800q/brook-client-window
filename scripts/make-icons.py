"""Builds the Windows icons from assets/AppIcon.png (the macOS app icon). Needs Pillow.

    python scripts/make-icons.py

app.ico is the exe/window icon. tray-*.ico show the connection state in the notification area:
off = greyed out, on = full colour, busy = amber dot (connecting), error = red dot.
"""
from pathlib import Path

from PIL import Image, ImageDraw, ImageEnhance

ASSETS = Path(__file__).resolve().parent.parent / "src" / "BrookClient" / "Assets"
SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]
TRAY_SIZES = [16, 20, 24, 32, 40, 48, 64]

src = Image.open(ASSETS.parent.parent.parent / "assets" / "AppIcon.png").convert("RGBA")
# The macOS artwork has a transparent margin around the rounded square; Windows icons fill the canvas.
icon = src.crop(src.getbbox() or (0, 0, *src.size)).resize((512, 512), Image.LANCZOS)


def with_dot(base: Image.Image, colour: tuple[int, int, int]) -> Image.Image:
    im = base.copy()
    d = ImageDraw.Draw(im)
    r = im.width * 0.23
    cx, cy = im.width - r - im.width * 0.02, im.height - r - im.height * 0.02
    d.ellipse((cx - r, cy - r, cx + r, cy + r), fill=(255, 255, 255, 255))
    r2 = r * 0.74
    d.ellipse((cx - r2, cy - r2, cx + r2, cy + r2), fill=(*colour, 255))
    return im


def grey(base: Image.Image) -> Image.Image:
    alpha = base.getchannel("A")
    g = ImageEnhance.Brightness(base.convert("L").convert("RGBA")).enhance(1.15)
    g.putalpha(alpha)
    return g


def save(im: Image.Image, name: str, sizes: list[int]) -> None:
    im.save(ASSETS / name, sizes=[(s, s) for s in sizes])
    print("wrote", ASSETS / name)


save(icon, "app.ico", SIZES)
save(grey(icon), "tray-off.ico", TRAY_SIZES)
save(icon, "tray-on.ico", TRAY_SIZES)
save(with_dot(icon, (240, 171, 0)), "tray-busy.ico", TRAY_SIZES)  # Retro.Accent amber
save(with_dot(icon, (200, 40, 30)), "tray-error.ico", TRAY_SIZES)  # Retro.StatusError
