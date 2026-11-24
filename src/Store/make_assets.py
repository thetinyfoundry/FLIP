"""Disegna il logo di FLIP (cerchio arancione, F bianca) nelle misure richieste da Microsoft Store.

Le proporzioni sono quelle di src/Flip/Flip.ico. Uso: python src/Store/make_assets.py
"""
from pathlib import Path
from PIL import Image, ImageDraw

OUT = Path(__file__).resolve().parent / "Assets"
ORANGE = (255, 112, 12, 255)
# Vertici della F e raggio del cerchio in un disegno 512 x 512.
F_SHAPE = [(168, 121), (383, 121), (383, 190), (239, 190), (239, 234), (343, 234), (343, 303), (239, 303), (239, 407), (168, 407)]
CENTER, RADIUS = 259, 243


def mark(size):
    scale = 8 if size < 512 else 4
    big = size * scale
    k = big / 512
    image = Image.new("RGBA", (big, big), ORANGE[:3] + (0,))
    draw = ImageDraw.Draw(image)
    c = 256 * k
    r = RADIUS * k
    draw.ellipse((c - r, c - r, c + r, c + r), fill=ORANGE)
    shift = 256 - CENTER
    draw.polygon([((x + shift) * k, (y + shift) * k) for x, y in F_SHAPE], fill=(255, 255, 255, 255))
    return image.resize((size, size), Image.LANCZOS)


def canvas(width, height, share):
    image = Image.new("RGBA", (width, height), (0, 0, 0, 0))
    logo = mark(round(min(width, height) * share))
    image.paste(logo, ((width - logo.width) // 2, (height - logo.height) // 2))
    return image


def main():
    OUT.mkdir(exist_ok=True)
    for old in OUT.glob("*.png"):
        old.unlink()
    for scale in (100, 125, 150, 200, 400):
        for name, width, height, share in (
            ("Square44x44Logo", 44, 44, 1.0),
            ("Square71x71Logo", 71, 71, 0.6),
            ("Square150x150Logo", 150, 150, 0.5),
            ("Square310x310Logo", 310, 310, 0.5),
            ("Wide310x150Logo", 310, 150, 0.6),
            ("StoreLogo", 50, 50, 1.0),
        ):
            w, h = round(width * scale / 100 + 1e-6), round(height * scale / 100 + 1e-6)
            canvas(w, h, share).save(OUT / f"{name}.scale-{scale}.png", optimize=True)
    for size in (16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256):
        for suffix in ("", "_altform-unplated", "_altform-lightunplated"):
            mark(size).save(OUT / f"Square44x44Logo.targetsize-{size}{suffix}.png", optimize=True)


if __name__ == "__main__":
    main()
