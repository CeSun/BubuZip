"""Convert PNG brand masters into compressed RGBA resources (requires Pillow)."""
import gzip
from pathlib import Path
from PIL import Image

root = Path(__file__).resolve().parent.parent / "assets/icons"
for name, size in (("BubuZip", 256), ("BubuZip.Archive", 64)):
    with Image.open(root / (name + ".png")) as image:
        pixels = image.convert("RGBA").resize((size, size), Image.Resampling.LANCZOS)
        (root / (name + ".rgba.gz")).write_bytes(gzip.compress(pixels.tobytes(), mtime=0))
