"""Turns a top-down rug photo into a pattern texture for the app.

Cuts away the photographed fringes (the app draws its own, so they can curl
with the rug) and saves a 3:2 JPEG into src/Hali/Assets/Patterns.

    python tools/prepare_pattern.py tasarim/ornekler/1-usak.png usak
"""
import sys
from pathlib import Path

from PIL import Image

FRINGE_CUT = 0.024  # share of the width taken by photographed fringes on each side
EDGE_CUT = 0.004    # thin uneven line along the top and bottom edges
OUT = Path(__file__).resolve().parent.parent / "src" / "Hali" / "Assets" / "Patterns"


def main(src: str, name: str) -> None:
    im = Image.open(src).convert("RGB")
    w, h = im.size
    dx, dy = round(w * FRINGE_CUT), round(h * EDGE_CUT)
    im = im.crop((dx, dy, w - dx, h - dy)).resize((1440, 960), Image.LANCZOS)
    OUT.mkdir(parents=True, exist_ok=True)
    im.save(OUT / f"{name}.jpg", quality=90, optimize=True)


if __name__ == "__main__":
    main(*sys.argv[1:3])
