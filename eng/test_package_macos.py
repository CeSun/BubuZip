#!/usr/bin/env python3
"""Metadata, signing-order and transactional packaging checks without changing OS registrations."""
import importlib.util
import json
from pathlib import Path
import plistlib
import re
import shutil
from types import SimpleNamespace
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("bubuzip_package_macos", Path(__file__).with_name("package-macos.py"))
packaging = importlib.util.module_from_spec(spec)
spec.loader.exec_module(packaging)


def options(root, **overrides):
    values = dict(project=root / "Project with spaces/Archive.csproj", name="BubuZip",
                  executable="BubuZip", bundle_id="io.bubuzip.app", version="0.1.0",
                  output=root / "BubuZip.app", configuration="Release", rid="osx-arm64",
                  minimum_system_version="13.0", sign_identity="-", publish_dir=root / "publish",
                  library_dir=[], sdl_library=None, notary_profile=None, force=False, verify=None)
    values.update(overrides)
    return SimpleNamespace(**values)


def write_app(app):
    (app / "Contents/Resources").mkdir(parents=True)
    with (app / "Contents/Info.plist").open("wb") as stream:
        plistlib.dump({"CFBundleIdentifier": "io.bubuzip.app"}, stream)


def write_plugin(plugin):
    native = plugin / "Contents/MacOS/Finder"
    native.parent.mkdir(parents=True)
    native.write_bytes(b"fixture")
    with (plugin / "Contents/Info.plist").open("wb") as stream:
        plistlib.dump({"CFBundleIdentifier": "io.bubuzip.app.finder", "CFBundleExecutable": "Finder",
                      "NSExtension": {"NSExtensionPointIdentifier": "com.apple.FinderSync",
                                      "NSExtensionPrincipalClass": "BubuZipFinderSync"}}, stream)


class MetadataTests(unittest.TestCase):
    def test_advertised_extensions_match_the_shared_reader_whitelist(self):
        source = (packaging.ROOT / "src/BubuZip/IShellIntegrationService.cs").read_text()
        whitelist = set(re.findall(r'"(\.[a-z0-9]+)"', source))
        mapping = packaging.archive_metadata()["BubuZipArchiveTypes"]
        self.assertEqual(set(mapping), whitelist)
        self.assertEqual(len(mapping), 25)
        self.assertFalse(any(identifier.startswith("dyn.") for identifier in mapping.values()))

    def test_every_extension_has_one_imported_type_and_non_default_document_capability(self):
        metadata = packaging.archive_metadata()
        declarations = {item["UTTypeIdentifier"]: item for item in metadata["UTImportedTypeDeclarations"]}
        documents = {item["LSItemContentTypes"][0]: item for item in metadata["CFBundleDocumentTypes"]}
        for extension, identifier in metadata["BubuZipArchiveTypes"].items():
            declaration = declarations[identifier]
            self.assertIn(extension[1:], declaration["UTTypeTagSpecification"]["public.filename-extension"])
            self.assertIn("public.archive", declaration["UTTypeConformsTo"])
            self.assertEqual(documents[identifier]["LSHandlerRank"], "Alternate")
            expected_role = "Editor" if extension in (".zip", ".7z") else "Viewer"
            self.assertEqual(documents[identifier]["CFBundleTypeRole"], expected_role)
        self.assertNotIn("UTExportedTypeDeclarations", metadata)

    def test_standard_types_and_aliases_remain_consistent(self):
        mapping = packaging.archive_metadata()["BubuZipArchiveTypes"]
        self.assertEqual(mapping[".zip"], "public.zip-archive")
        self.assertEqual(mapping[".tgz"], "org.gnu.gnu-zip-tar-archive")
        for first, alias in ((".gz", ".gzip"), (".bz2", ".bzip2"), (".tbz", ".tbz2"), (".lz", ".lzip"), (".zst", ".zstd")):
            self.assertEqual(mapping[first], mapping[alias])

    def test_private_finder_requests_are_owned_data_documents_not_archive_formats(self):
        archives = packaging.archive_metadata()
        metadata = packaging.bundle_metadata()
        self.assertEqual(metadata["BubuZipArchiveTypes"], archives["BubuZipArchiveTypes"])
        self.assertEqual(metadata["UTImportedTypeDeclarations"], archives["UTImportedTypeDeclarations"])
        self.assertNotIn("." + packaging.FINDER_REQUEST_EXTENSION, metadata["BubuZipArchiveTypes"])
        self.assertEqual(len(metadata["CFBundleDocumentTypes"]), len(archives["CFBundleDocumentTypes"]) + 1)
        request = metadata["CFBundleDocumentTypes"][-1]
        self.assertEqual(request["LSItemContentTypes"], [packaging.FINDER_REQUEST_TYPE])
        self.assertEqual(request["CFBundleTypeExtensions"], [packaging.FINDER_REQUEST_EXTENSION])
        self.assertEqual(request["CFBundleTypeRole"], "Viewer")
        self.assertEqual(request["LSHandlerRank"], "Alternate")
        exported = metadata["UTExportedTypeDeclarations"]
        self.assertEqual(len(exported), 1)
        self.assertEqual(exported[0]["UTTypeIdentifier"], packaging.FINDER_REQUEST_TYPE)
        self.assertEqual(exported[0]["UTTypeConformsTo"], ["public.data"])
        self.assertEqual(exported[0]["UTTypeTagSpecification"]["public.filename-extension"],
                         [packaging.FINDER_REQUEST_EXTENSION])
        self.assertNotIn("CFBundleURLTypes", metadata)

    def test_configure_and_verify_plugin_metadata(self):
        with tempfile.TemporaryDirectory() as directory:
            app = Path(directory) / "Archive.app"
            write_app(app)
            plugin = app / "Contents/PlugIns" / packaging.FINDER_NAME
            write_plugin(plugin)
            packaging.configure_metadata(app, "2.3.4", "13.0")
            self.assertEqual(packaging.verify_metadata(app), plugin)
            with (app / "Contents/Info.plist").open("rb") as stream:
                self.assertEqual(plistlib.load(stream)["UTExportedTypeDeclarations"],
                                 packaging.bundle_metadata()["UTExportedTypeDeclarations"])
            with (plugin / "Contents/Info.plist").open("rb") as stream:
                info = plistlib.load(stream)
            self.assertEqual(info["CFBundleVersion"], "2.3.4")
            (plugin / "Contents/MacOS/Finder").unlink()
            with self.assertRaisesRegex(ValueError, "executable is missing"):
                packaging.verify_metadata(app)

    def test_verify_rejects_incomplete_archive_declarations(self):
        with tempfile.TemporaryDirectory() as directory:
            app = Path(directory) / "Archive.app"
            write_app(app)
            write_plugin(app / "Contents/PlugIns" / packaging.FINDER_NAME)
            packaging.configure_metadata(app, "0.1.0", "13.0")
            path = app / "Contents/Info.plist"
            with path.open("rb") as stream:
                info = plistlib.load(stream)
            del info["BubuZipArchiveTypes"][".zip"]
            with path.open("wb") as stream:
                plistlib.dump(info, stream)
            with self.assertRaisesRegex(ValueError, "document metadata"):
                packaging.verify_metadata(app)

    def test_verify_requires_the_private_finder_document_and_its_exported_declaration(self):
        for missing in ("document", "exported type"):
            with self.subTest(missing=missing), tempfile.TemporaryDirectory() as directory:
                app = Path(directory) / "Archive.app"
                write_app(app)
                write_plugin(app / "Contents/PlugIns" / packaging.FINDER_NAME)
                packaging.configure_metadata(app, "0.1.0", "13.0")
                path = app / "Contents/Info.plist"
                with path.open("rb") as stream:
                    info = plistlib.load(stream)
                if missing == "document":
                    info["CFBundleDocumentTypes"] = [item for item in info["CFBundleDocumentTypes"]
                                                     if packaging.FINDER_REQUEST_TYPE not in item["LSItemContentTypes"]]
                else:
                    del info["UTExportedTypeDeclarations"]
                with path.open("wb") as stream:
                    plistlib.dump(info, stream)
                with self.assertRaisesRegex(ValueError, "document metadata"):
                    packaging.verify_metadata(app)


class OrchestrationTests(unittest.TestCase):
    def test_upstream_command_cannot_notarize_or_replace_final_output(self):
        root = Path("/tmp/BubuZip $(literal)")
        selected = options(root, sign_identity="Developer ID Application: A $(literal)",
                           notary_profile="notary-profile", force=True,
                           library_dir=[str(root / "native libs")], sdl_library=root / "libSDL3.dylib")
        stage = root / ".stage/Archive.app"
        arguments = packaging.base_command(selected, stage)
        self.assertEqual(arguments[1], packaging.ROOT / "eng/macos_bundle.py")
        self.assertEqual(arguments[arguments.index("--output") + 1], stage)
        self.assertNotIn("--notary-profile", arguments)
        self.assertNotIn("--force", arguments)
        self.assertNotIn(selected.output, arguments)
        self.assertIn("Developer ID Application: A $(literal)", arguments)
        self.assertIn((root / "native libs").resolve(), arguments)

    def test_plugin_is_signed_before_the_containing_app_with_correct_entitlements(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            app = root / "Archive.app"
            calls = []
            with patch.object(packaging, "run", side_effect=lambda *args: calls.append(args)):
                packaging.sign_completed_bundle(app, "Developer ID Application: Test", root)
            self.assertEqual(len(calls), 2)
            self.assertEqual(calls[0][-1], app / "Contents/PlugIns" / packaging.FINDER_NAME)
            self.assertIn(packaging.FINDER_SOURCE / "FinderExtension.entitlements", calls[0])
            self.assertEqual(calls[1][-1], app)
            self.assertIn("--timestamp", calls[0])
            self.assertIn("--timestamp", calls[1])
            with (root / "application-entitlements.plist").open("rb") as stream:
                self.assertTrue(plistlib.load(stream)["com.apple.security.cs.allow-jit"])

    def test_final_verification_and_notarization_precede_output_replacement(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            selected = options(root, force=True, sign_identity="Developer ID Application: Test", notary_profile="profile")
            selected.output.mkdir()
            sentinel = selected.output / "previous-version"
            sentinel.write_text("old app")
            events = []
            upstream = SimpleNamespace(Bundle=lambda *_: SimpleNamespace(verify=lambda: []))

            def fake_run(*arguments):
                if str(arguments[0]) == str(packaging.FINDER_SOURCE / "build.sh"):
                    write_plugin(Path(arguments[arguments.index("--output") + 1]))
                    events.append("plugin")
                else:
                    write_app(Path(arguments[arguments.index("--output") + 1]))
                    events.append("payload")

            def check_stage(app, *_):
                packaging.verify_metadata(app)
                self.assertEqual(sentinel.read_text(), "old app")

            def verify(_, app, arch):
                check_stage(app)
                self.assertEqual(arch, "arm64")
                events.append("verify")

            def replace(app, output, force):
                check_stage(app)
                self.assertTrue(force)
                events.append("replace")
                shutil.rmtree(output)
                app.rename(output)

            with patch.object(packaging, "run", side_effect=fake_run), \
                 patch.object(packaging, "sign_completed_bundle", side_effect=lambda app, *_: (check_stage(app), events.append("sign"))), \
                 patch.object(packaging, "verify_bundle", side_effect=verify), \
                 patch.object(packaging, "notarize_completed_bundle", side_effect=lambda app, *_: (check_stage(app), events.append("notarize"))), \
                 patch.object(packaging, "replace_bundle", side_effect=replace), \
                 patch.object(packaging.sys, "platform", "darwin"):
                packaging.package(upstream, selected)
            self.assertEqual(events, ["payload", "plugin", "sign", "verify", "notarize", "verify", "replace"])
            self.assertFalse(sentinel.exists())
            packaging.verify_metadata(selected.output)

    def test_plugin_build_failure_leaves_the_previous_application_intact(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            selected = options(root, force=True)
            selected.output.mkdir()
            sentinel = selected.output / "previous-version"
            sentinel.write_text("old app")

            def fail_plugin(*arguments):
                if str(arguments[0]) == str(packaging.FINDER_SOURCE / "build.sh"):
                    raise OSError("fixture compiler failure")
                write_app(Path(arguments[arguments.index("--output") + 1]))

            with patch.object(packaging, "run", side_effect=fail_plugin), \
                 patch.object(packaging.sys, "platform", "darwin"), \
                 self.assertRaisesRegex(OSError, "compiler failure"):
                packaging.package(SimpleNamespace(), selected)
            self.assertEqual(sentinel.read_text(), "old app")

    def test_verify_mode_does_not_compile_publish_or_replace(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            selected = options(root, verify=root / "Installed.app")
            with patch.object(packaging, "run") as run, \
                 patch.object(packaging, "verify_bundle", return_value=[]) as verify, \
                 patch.object(packaging, "replace_bundle") as replace, \
                 patch.object(packaging.sys, "platform", "darwin"):
                packaging.package(SimpleNamespace(), selected)
            run.assert_not_called()
            replace.assert_not_called()
            verify.assert_called_once()

    @unittest.skipUnless(packaging.sys.platform == "darwin", "Darwin directory exchange required")
    def test_force_atomically_exchanges_complete_bundles(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source, output = root / "New.app", root / "Existing.app"
            source.mkdir()
            output.mkdir()
            (source / "version").write_text("new")
            (output / "version").write_text("old")
            packaging.replace_bundle(source, output, force=True)
            self.assertEqual((output / "version").read_text(), "new")
            self.assertEqual((source / "version").read_text(), "old")


if __name__ == "__main__":
    unittest.main()
