#!/usr/bin/env python3
"""Check Mach-O preparation and real Homebrew SDL relocation/signing on macOS."""
import hashlib
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

from macos_bundle import load_upstream


class PreparationTests(unittest.TestCase):
    def test_thin_binary_preserves_existing_signature_bytes(self):
        upstream = load_upstream()
        with tempfile.TemporaryDirectory() as directory:
            binary = Path(directory) / "libSDL3.dylib"
            original = b"Mach-O fixture including its signature and link-edit padding"
            binary.write_bytes(original)
            bundle = upstream.Bundle(directory, "arm64")
            with patch.object(bundle, "architectures", return_value=["arm64"]), \
                 patch.object(upstream.subprocess, "run", side_effect=AssertionError("Unexpected binary mutation")):
                bundle.prepare_binary(binary)
            self.assertEqual(binary.read_bytes(), original)

    def test_universal_binary_keeps_only_requested_slice_without_stripping(self):
        upstream = load_upstream()
        for arch in ("arm64", "x86_64"):
            with self.subTest(arch=arch), tempfile.TemporaryDirectory() as directory:
                binary = Path(directory) / "universal.dylib"
                binary.write_bytes(b"universal fixture")
                bundle = upstream.Bundle(directory, arch)

                def thin(*args, **kwargs):
                    self.assertEqual(args[0][:4], ["lipo", str(binary), "-thin", arch])
                    Path(args[0][-1]).write_bytes(b"target slice with signature")

                with patch.object(bundle, "architectures", return_value=["arm64", "x86_64"]), \
                     patch.object(upstream.subprocess, "run", side_effect=thin):
                    bundle.prepare_binary(binary)
                self.assertEqual(binary.read_bytes(), b"target slice with signature")
                self.assertFalse(binary.with_name(binary.name + ".thin").exists())

    def test_wrong_architecture_is_rejected_before_mutation(self):
        upstream = load_upstream()
        bundle = upstream.Bundle("Fixture.app", "arm64")
        with patch.object(bundle, "architectures", return_value=["x86_64"]), \
             patch.object(upstream.subprocess, "run", side_effect=AssertionError("Unexpected binary mutation")):
            with self.assertRaisesRegex(ValueError, "expected arm64"):
                bundle.prepare_binary(Path("intel.dylib"))


@unittest.skipUnless(sys.platform == "darwin", "macOS, Xcode and Homebrew SDL3 required")
class NativeBundleTests(unittest.TestCase):
    def test_homebrew_sdl_can_be_relocated_signed_and_loaded(self):
        upstream = load_upstream()
        arch = "arm64" if os.uname().machine == "arm64" else "x86_64"
        rid = "osx-arm64" if arch == "arm64" else "osx-x64"
        prefix = Path(subprocess.check_output(["brew", "--prefix", "sdl3"], text=True).strip())
        sdl = (prefix / "lib/libSDL3.0.dylib").resolve(strict=True)
        original_hash = hashlib.sha256(sdl.read_bytes()).digest()
        with tempfile.TemporaryDirectory(prefix="bubuzip-native-test-") as directory:
            root = Path(directory)
            publish = root / "publish"
            publish.mkdir()
            source = root / "fixture.c"
            # Load the app-local SDL alias after relocation without starting a GUI.
            source.write_text('#include <dlfcn.h>\nint main(void) {\n'
                              '  void *sdl = dlopen("@executable_path/libSDL3.dylib", RTLD_NOW);\n'
                              '  if (!sdl) return 1;\n'
                              '  int (*version)(void) = (int (*)(void))dlsym(sdl, "SDL_GetVersion");\n'
                              '  return version && version() > 0 ? 0 : 2;\n}\n')
            subprocess.run(["clang", "-arch", arch, "-Wl,-headerpad_max_install_names",
                            str(source), "-o", str(publish / "Fixture")], check=True)
            app = root / "Fixture.app"
            subprocess.run([sys.executable, str(Path(__file__).with_name("macos_bundle.py")),
                            "--publish-dir", str(publish), "--output", str(app),
                            "--name", "Fixture", "--executable", "Fixture", "--rid", rid,
                            "--sdl-library", str(sdl), "--library-dir", str(prefix / "lib")], check=True)
            manifest = upstream.Bundle(app, arch).verify()
            self.assertTrue(any(item["path"].endswith(sdl.name) for item in manifest))
            subprocess.run(["codesign", "--verify", "--deep", "--strict", str(app)], check=True)
            environment = {key: value for key, value in os.environ.items() if not key.startswith("DYLD_")}
            subprocess.run([str(app / "Contents/MacOS/Fixture")], env=environment, check=True)
        self.assertEqual(hashlib.sha256(sdl.read_bytes()).digest(), original_hash)


if __name__ == "__main__":
    unittest.main(verbosity=2)
