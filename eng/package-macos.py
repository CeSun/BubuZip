#!/usr/bin/env python3
"""Package BubuZip with archive document declarations and its Finder Sync extension."""
import ctypes
import importlib.util
import json
import os
from pathlib import Path
import plistlib
import shutil
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parent.parent
UPSTREAM = ROOT / "external/EclipseUI/eng/bundle_macos.py"
FINDER_SOURCE = ROOT / "eng/macos-finder-extension"
FINDER_NAME = "BubuZipFinder.appex"
FINDER_REQUEST_TYPE = "io.bubuzip.finder-request"
FINDER_REQUEST_EXTENSION = "bubuzip-request"
ICON_ASSETS = ROOT / "assets/icons"
ICON_NAME = "BubuZip.icns"
ARCHIVE_ICON_NAME = "BubuZip.Archive.icns"

# Imported declarations advertise existing formats; BubuZip never claims to own them.
# Standard identifiers match CoreServices. Aliases share one identifier and one declaration.
ARCHIVE_TYPES = (
    ("public.zip-archive", "ZIP archive", ("zip",), "application/zip"),
    ("io.bubuzip.archive.zipx", "ZIPX archive", ("zipx",), "application/x-zipx"),
    ("org.7-zip.7-zip-archive", "7z archive", ("7z",), "application/x-7z-compressed"),
    ("com.rarlab.rar-archive", "RAR archive", ("rar",), "application/vnd.rar"),
    ("public.tar-archive", "TAR archive", ("tar",), "application/x-tar"),
    ("org.gnu.gnu-zip-archive", "GZip archive", ("gz", "gzip"), "application/gzip"),
    ("org.gnu.gnu-zip-tar-archive", "GZip TAR archive", ("tgz",), "application/x-compressed-tar"),
    ("public.bzip2-archive", "BZip2 archive", ("bz2", "bzip2"), "application/x-bzip2"),
    ("public.tar-bzip2-archive", "BZip2 TAR archive", ("tbz", "tbz2"), "application/x-bzip2-compressed-tar"),
    ("org.tukaani.xz-archive", "XZ archive", ("xz",), "application/x-xz"),
    ("org.tukaani.tar-xz-archive", "XZ TAR archive", ("txz",), "application/x-xz-compressed-tar"),
    ("io.bubuzip.archive.lzip", "LZip archive", ("lz", "lzip"), "application/x-lzip"),
    ("io.bubuzip.archive.tar-lzip", "LZip TAR archive", ("tlz",), "application/x-lzip-compressed-tar"),
    ("io.bubuzip.archive.zstandard", "Zstandard archive", ("zst", "zstd"), "application/zstd"),
    ("io.bubuzip.archive.tar-zstandard", "Zstandard TAR archive", ("tzst",), "application/x-zstd-compressed-tar"),
    ("public.z-archive", "Unix compress archive", ("z",), "application/x-compress"),
    ("io.bubuzip.archive.tar-compress", "Unix compress TAR archive", ("taz",), "application/x-compressed-tar"),
    ("io.bubuzip.archive.arc", "ARC archive", ("arc",), "application/x-arc"),
    ("io.bubuzip.archive.arj", "ARJ archive", ("arj",), "application/x-arj"),
    ("io.bubuzip.archive.ace", "ACE archive", ("ace",), "application/x-ace-compressed"),
)


def load_upstream():
    if not UPSTREAM.is_file():
        raise ValueError("EclipseUI submodule is missing. Run: git submodule update --init --recursive")
    spec = importlib.util.spec_from_file_location("eclipseui_bundle_macos", UPSTREAM)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def run(*arguments):
    subprocess.run([str(argument) for argument in arguments], check=True)


def archive_metadata():
    mapping, documents, imported = {}, [], []
    for identifier, description, extensions, mime in ARCHIVE_TYPES:
        mapping.update({"." + extension: identifier for extension in extensions})
        documents.append({
            "CFBundleTypeName": description,
            "CFBundleTypeRole": "Editor" if identifier in ("public.zip-archive", "org.7-zip.7-zip-archive") else "Viewer",
            "LSHandlerRank": "Alternate",
            "LSItemContentTypes": [identifier],
            "CFBundleTypeExtensions": list(extensions),
            "CFBundleTypeIconFile": ARCHIVE_ICON_NAME,
        })
        imported.append({
            "UTTypeIdentifier": identifier,
            "UTTypeDescription": description,
            "UTTypeConformsTo": ["public.archive"],
            "UTTypeTagSpecification": {"public.filename-extension": list(extensions), "public.mime-type": [mime]},
        })
    return {"CFBundleDocumentTypes": documents, "UTImportedTypeDeclarations": imported,
            "BubuZipArchiveTypes": mapping}


def bundle_metadata():
    metadata = archive_metadata()
    # Finder sends this private document through Launch Services. It is not an
    # archive format or a URL command; the app validates the request's origin.
    metadata["CFBundleDocumentTypes"].append({
        "CFBundleTypeName": "BubuZip Finder request",
        "CFBundleTypeRole": "Viewer",
        "LSHandlerRank": "Alternate",
        "LSItemContentTypes": [FINDER_REQUEST_TYPE],
        "CFBundleTypeExtensions": [FINDER_REQUEST_EXTENSION],
    })
    metadata["UTExportedTypeDeclarations"] = [{
        "UTTypeIdentifier": FINDER_REQUEST_TYPE,
        "UTTypeDescription": "BubuZip Finder request",
        "UTTypeConformsTo": ["public.data"],
        "UTTypeTagSpecification": {"public.filename-extension": [FINDER_REQUEST_EXTENSION]},
    }]
    return metadata


def configure_metadata(app, version, minimum_system_version):
    resources = app / "Contents/Resources"
    resources.mkdir(parents=True, exist_ok=True)
    shutil.copy2(ICON_ASSETS / ICON_NAME, resources / ICON_NAME)
    shutil.copy2(ICON_ASSETS / ARCHIVE_ICON_NAME, resources / ARCHIVE_ICON_NAME)
    licenses = resources / "app/licenses/BubuZip"
    licenses.mkdir(parents=True, exist_ok=True)
    for filename in ("NOTICE.md", "NanaZip.License.md"):
        shutil.copy2(ICON_ASSETS / filename, licenses / filename)
    info_path = app / "Contents/Info.plist"
    with info_path.open("rb") as stream:
        info = plistlib.load(stream)
    info.update(bundle_metadata())
    info["CFBundleIconFile"] = ICON_NAME
    info["BubuZipFinderExtensionIdentifier"] = info["CFBundleIdentifier"] + ".finder"
    with info_path.open("wb") as stream:
        plistlib.dump(info, stream)
    extension_info = app / "Contents/PlugIns" / FINDER_NAME / "Contents/Info.plist"
    with extension_info.open("rb") as stream:
        plugin = plistlib.load(stream)
    plugin["CFBundleIdentifier"] = info["BubuZipFinderExtensionIdentifier"]
    plugin["CFBundleVersion"] = plugin["CFBundleShortVersionString"] = version
    plugin["LSMinimumSystemVersion"] = minimum_system_version
    with extension_info.open("wb") as stream:
        plistlib.dump(plugin, stream)


def verify_metadata(app):
    with (app / "Contents/Info.plist").open("rb") as stream:
        info = plistlib.load(stream)
    expected = bundle_metadata()
    for key, value in expected.items():
        if info.get(key) != value:
            raise ValueError(f"Bundle has missing or inconsistent document metadata: {key}")
    icon_path = app / "Contents/Resources" / ICON_NAME
    if info.get("CFBundleIconFile") != ICON_NAME or not icon_path.is_file():
        raise ValueError("Bundle application icon is missing or not declared")
    icon = icon_path.read_bytes()
    if len(icon) <= 8 or icon[:4] != b"icns" or int.from_bytes(icon[4:8], "big") != len(icon):
        raise ValueError("Bundle application icon is not a complete ICNS file")
    plugin_path = app / "Contents/PlugIns" / FINDER_NAME
    with (plugin_path / "Contents/Info.plist").open("rb") as stream:
        plugin = plistlib.load(stream)
    identifier = info["CFBundleIdentifier"] + ".finder"
    if plugin.get("CFBundleIdentifier") != identifier or info.get("BubuZipFinderExtensionIdentifier") != identifier:
        raise ValueError("Finder extension identifier does not match its containing application")
    extension = plugin.get("NSExtension", {})
    if extension.get("NSExtensionPointIdentifier") != "com.apple.FinderSync" or extension.get("NSExtensionPrincipalClass") != "BubuZipFinderSync":
        raise ValueError("Bundle is missing the expected Finder Sync extension entry point")
    executable = plugin.get("CFBundleExecutable", "")
    if not executable or Path(executable).name != executable or not (plugin_path / "Contents/MacOS" / executable).is_file():
        raise ValueError("Finder extension executable is missing")
    return plugin_path


def verify_bundle(upstream, app, arch):
    plugin = verify_metadata(app)
    manifest = upstream.Bundle(app, arch).verify()
    run("codesign", "--verify", "--strict", plugin)
    run("codesign", "--verify", "--deep", "--strict", app)
    return manifest


def base_command(options, staged_app):
    # The upstream packager signs the ordinary payload, but cannot notarize or replace the final
    # application until our metadata and nested extension are in the signed result.
    command = [sys.executable, UPSTREAM, "--project", Path(options.project).expanduser().resolve(),
               "--name", options.name, "--executable", options.executable, "--bundle-id", options.bundle_id,
               "--version", options.version, "--output", staged_app, "--configuration", options.configuration,
               "--rid", options.rid, "--minimum-system-version", options.minimum_system_version,
               "--sign-identity", options.sign_identity]
    if options.publish_dir:
        command.extend(["--publish-dir", options.publish_dir.expanduser().resolve()])
    for directory in options.library_dir:
        command.extend(["--library-dir", Path(directory).expanduser().resolve()])
    if options.sdl_library:
        command.extend(["--sdl-library", options.sdl_library.expanduser().resolve()])
    return command


def sign_completed_bundle(app, identity, temporary):
    timestamp = "--timestamp=none" if identity == "-" else "--timestamp"
    plugin = app / "Contents/PlugIns" / FINDER_NAME
    run("codesign", "--force", "--sign", identity, "--options", "runtime", timestamp,
        "--entitlements", FINDER_SOURCE / "FinderExtension.entitlements", plugin)
    entitlements = temporary / "application-entitlements.plist"
    with entitlements.open("wb") as stream:
        plistlib.dump({"com.apple.security.cs.allow-jit": True,
                      "com.apple.security.cs.disable-library-validation": True}, stream)
    run("codesign", "--force", "--sign", identity, "--options", "runtime", timestamp,
        "--entitlements", entitlements, app)


def notarize_completed_bundle(app, profile, temporary):
    archive = temporary / "notarize.zip"
    run("ditto", "-c", "-k", "--keepParent", app, archive)
    run("xcrun", "notarytool", "submit", archive, "--keychain-profile", profile, "--wait")
    run("xcrun", "stapler", "staple", app)
    run("xcrun", "stapler", "validate", app)


def replace_bundle(staged_app, output, force):
    if output.is_symlink():
        raise ValueError("Refusing to replace a symbolic-link application output")
    if not output.exists():
        staged_app.rename(output)
        return
    if not force:
        raise ValueError(f"Output already exists: {output}; use --force to replace it")
    # Darwin RENAME_SWAP atomically exchanges the complete, verified new bundle and old output.
    # If it fails the previous application remains untouched; cleanup later removes the old app.
    libc = ctypes.CDLL(None, use_errno=True)
    rename = libc.renamex_np
    rename.argtypes = [ctypes.c_char_p, ctypes.c_char_p, ctypes.c_uint]
    rename.restype = ctypes.c_int
    if rename(os.fsencode(staged_app), os.fsencode(output), 0x00000002) != 0:
        error = ctypes.get_errno()
        raise OSError(error, os.strerror(error), str(output))


def package(upstream, options):
    if sys.platform != "darwin":
        raise ValueError("macOS packaging requires macOS with Xcode Command Line Tools")
    arch = "arm64" if options.rid == "osx-arm64" else "x86_64"
    if options.verify:
        print(json.dumps(verify_bundle(upstream, options.verify.expanduser().resolve(), arch), indent=2))
        return
    if options.notary_profile and options.sign_identity == "-":
        raise ValueError("--notary-profile requires --sign-identity with a Developer ID Application certificate")
    output = options.output.expanduser().absolute()
    if output.suffix != ".app":
        raise ValueError("--output must end in .app")
    if output.is_symlink():
        raise ValueError("Refusing to replace a symbolic-link application output")
    if output.exists() and not options.force:
        raise ValueError(f"Output already exists: {output}; use --force to replace it")
    if options.publish_dir and output.parent.resolve().is_relative_to(options.publish_dir.expanduser().resolve()):
        raise ValueError("--output must be outside --publish-dir")
    output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix=".bubuzip-package-", dir=output.parent) as directory:
        temporary = Path(directory)
        app = temporary / output.name
        run(*base_command(options, app))
        plugin = app / "Contents/PlugIns" / FINDER_NAME
        run(FINDER_SOURCE / "build.sh", "--rid", options.rid, "--output", plugin)
        configure_metadata(app, options.version, options.minimum_system_version)
        # Include the extension's native dependencies in the final dependency manifest.
        manifest = upstream.Bundle(app, arch).verify()
        (app / "Contents/Resources/native-dependencies.json").write_text(json.dumps(manifest, indent=2) + "\n")
        sign_completed_bundle(app, options.sign_identity, temporary)
        verify_bundle(upstream, app, arch)
        if options.notary_profile:
            notarize_completed_bundle(app, options.notary_profile, temporary)
            verify_bundle(upstream, app, arch)
        replace_bundle(app, output, options.force)
    print(f"Packaged {output} ({arch}, Finder extension and {len(archive_metadata()['BubuZipArchiveTypes'])} archive extensions)")


def main(arguments=None):
    upstream = load_upstream()
    parser = upstream.parser()
    parser.description = __doc__
    parser.set_defaults(project=str(ROOT / "src/BubuZip.Desktop/BubuZip.Desktop.csproj"),
                        name="BubuZip", executable="BubuZip", bundle_id="io.bubuzip.app",
                        version="0.1.0", output=ROOT / "out/macos/BubuZip.app")
    package(upstream, parser.parse_args(arguments))


if __name__ == "__main__":
    try:
        main()
    except (ValueError, OSError, subprocess.CalledProcessError) as error:
        print(f"Packaging failed: {error}", file=sys.stderr)
        sys.exit(1)
