# BubuZip icon artwork

The current application and archive icons were generated for BubuZip on
2026-11-26 using the built-in image generation tool and a user-provided reference.
They share a folded-document silhouette, a zipper, and a brown bear peeking
from behind the zipper. The application icon is blue; the archive icon is white.

## Assets

- BubuZip.png / .ico / .icns: application icon.
- BubuZip.Archive.png / .ico / .icns: archive document icon.
- PNG files are tightly cropped transparent masters. ICO and ICNS exports
  preserve their proportions on square canvases with approximately 2% edge padding.
- The .rgba.gz files are embedded UI resources, regenerated from the PNG masters
  with `python eng/prepare-brand-images.py` (requires Pillow). The same script
  removes near-invisible edge residue, crops the masters and exports ICO/ICNS.
- Small archive icons have a thin blue-gray outline: Windows ICO frames up to
  32 pixels, macOS 16/32-point Retina frames, and embedded file-list images.
  Larger frames and application icons retain the unoutlined artwork.

The bear is inspired by 布布 from 黄小B's 一二和布布. Character rights remain
with their respective owner; image generation does not establish a character license.

## Design prompts

Preserve the reference canvas, silhouette, folded corner, zipper geometry and
bear pose. Apply restrained Fluent gradients, bevels and soft contact shadows.
For the application variant, use saturated azure blue with white zipper teeth.
For the archive variant, use pearl white with a blue zipper ribbon. Keep the
brown bear and transparent background in both variants.

## Historical attribution

The previous icons came from M2Team/NanaZip, revision
ddeb10e55f5a76863ce39bbcc3fcc7298cfa3b32, with artwork by Shomnipotence.
NanaZip also served as a palette reference during design.
Its CC BY-ND 4.0 license is retained in NanaZip.License.md for provenance;
that license does not describe the current generated artwork.
