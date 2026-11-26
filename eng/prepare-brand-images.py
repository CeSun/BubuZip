"""Export tightly framed icons and embedded UI images (requires Pillow)."""
import gzip
from pathlib import Path
from PIL import Image, ImageOps, ImageFilter


def outlined_small_icon(canvas, size):
    """Add an antialiased blue-gray outer edge for list-size archive icons."""
    scale = 4
    artwork = canvas.resize((size * scale, size * scale), Image.Resampling.LANCZOS)
    radius = max(2, round(size / 32 * scale))
    edge = artwork.getchannel("A").filter(ImageFilter.MaxFilter(radius * 2 + 1))
    edge = edge.point(lambda value: round(value * 0.78))
    backing = Image.new("RGBA", artwork.size, (91, 116, 144, 0))
    backing.putalpha(edge)
    backing.alpha_composite(artwork)
    return backing.resize((size, size), Image.Resampling.LANCZOS)

root = Path(__file__).resolve().parent.parent / "assets/icons"
for name, size in (("BubuZip", 256), ("BubuZip.Archive", 64)):
    with Image.open(root / (name + ".png")) as image:
        image = image.convert("RGBA")
        # Ignore near-invisible generation residue when finding the actual artwork.
        alpha = image.getchannel("A").point(lambda value: 0 if value <= 8 else value)
        image.putalpha(alpha)
        bounds = alpha.getbbox()
        if bounds is None:
            raise ValueError(f"Empty icon: {name}")
        image = image.crop(bounds)
        image.save(root / (name + ".png"))
        # Square system icons need aspect-preserving padding, never stretching.
        canvas = Image.new("RGBA", (1024, 1024))
        artwork = ImageOps.contain(image, (984, 984), Image.Resampling.LANCZOS)
        canvas.alpha_composite(artwork, ((1024 - artwork.width) // 2, (1024 - artwork.height) // 2))
        small_frames = [outlined_small_icon(canvas, n) for n in (16, 20, 24, 32)] if name.endswith(".Archive") else []
        # Supply every ICO frame explicitly so large sizes never inherit a small outline.
        frames = small_frames + [canvas.resize((n, n), Image.Resampling.LANCZOS)
                                 for n in (16, 20, 24, 32, 40, 48, 64, 128, 256)
                                 if not small_frames or n > 32]
        canvas.save(root / (name + ".ico"), sizes=[frame.size for frame in frames], append_images=frames)
        retina_small = [outlined_small_icon(canvas, n) for n in (32, 64)] if small_frames else []
        canvas.save(root / (name + ".icns"), append_images=retina_small)
        pixels = canvas.resize((size, size), Image.Resampling.LANCZOS)
        if small_frames:
            pixels = outlined_small_icon(canvas, size)
        (root / (name + ".rgba.gz")).write_bytes(gzip.compress(pixels.tobytes(), mtime=0))
