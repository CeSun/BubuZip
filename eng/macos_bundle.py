#!/usr/bin/env python3
"""BubuUI bundler entry point with BubuZip's Mach-O preparation fix."""
import importlib.util
from pathlib import Path
import subprocess
import sys

UPSTREAM = Path(__file__).resolve().parent.parent / "external/BubuUI/eng/bundle_macos.py"


def load_upstream():
    if not UPSTREAM.is_file():
        raise ValueError("BubuUI submodule is missing. Run: git submodule update --init --recursive")
    spec = importlib.util.spec_from_file_location("bubuui_bundle_macos", UPSTREAM)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)

    class Bundle(module.Bundle):
        def prepare_binary(self, path):
            arches = self.check_architecture(path)
            # Do not strip signatures before install_name_tool. On Homebrew dylibs,
            # codesign --remove-signature can leave unaccounted-for __LINKEDIT padding
            # that install_name_tool rejects. Relocation invalidates the old signature;
            # upstream's final codesign --force pass replaces it after all edits.
            # Keep this override here until the pinned BubuUI bundler is fixed.
            if len(arches) > 1:
                thin = path.with_name(path.name + ".thin")
                module.run("lipo", path, "-thin", self.arch, "-output", thin)
                thin.replace(path)

    module.Bundle = Bundle
    return module


if __name__ == "__main__":
    try:
        load_upstream().main()
    except (ValueError, OSError, subprocess.CalledProcessError) as error:
        print(f"Packaging failed: {error}", file=sys.stderr)
        sys.exit(1)
