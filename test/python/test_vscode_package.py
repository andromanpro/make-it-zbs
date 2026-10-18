import importlib.util
import pathlib
import tempfile
import unittest
import xml.etree.ElementTree as ET
import zipfile
from unittest.mock import patch


ROOT = pathlib.Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("zbs_build_vscode", ROOT / "tools" / "build-vscode.py")
builder = importlib.util.module_from_spec(spec)
spec.loader.exec_module(builder)


class VsCodePackageTests(unittest.TestCase):
    def test_git_checkout_line_endings_cannot_change_the_embedded_package(self):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            source = root / "integrations/vscode"
            source.mkdir(parents=True)
            names = ("package.json", "extension.js", "editor-sessions.js", "README.md", "LICENSE.txt")
            for name in names:
                (source / name).write_bytes((ROOT / "integrations/vscode" / name).read_text(encoding="utf-8").encode("utf-8"))
            with patch.object(builder, "ROOT", root):
                first = builder.build(root / "lf.vsix").read_bytes()
                for name in names:
                    path = source / name
                    path.write_bytes(path.read_bytes().replace(b"\n", b"\r\n"))
                second = builder.build(root / "crlf.vsix").read_bytes()
            self.assertEqual(first, second)

    def test_vsix_build_is_byte_reproducible_for_embedding_in_release_dll(self):
        with tempfile.TemporaryDirectory() as directory:
            first = builder.build(pathlib.Path(directory) / "one.vsix")
            second = builder.build(pathlib.Path(directory) / "two.vsix")
            self.assertEqual(first.read_bytes(), second.read_bytes())

    def test_vsix_has_installable_manifest_local_entry_point_and_only_declared_sources(self):
        with tempfile.TemporaryDirectory() as directory:
            output = builder.build(pathlib.Path(directory) / "adapter.vsix")
            with zipfile.ZipFile(output) as archive:
                self.assertEqual(set(archive.namelist()), {
                    "extension.vsixmanifest", "[Content_Types].xml", "extension/package.json",
                    "extension/extension.js", "extension/editor-sessions.js", "extension/README.md", "extension/LICENSE.txt"})
                manifest = ET.fromstring(archive.read("extension.vsixmanifest"))
                ns = {"v": builder.VSIX_NS}
                self.assertEqual(manifest.find("v:Installation/v:InstallationTarget", ns).get("Id"), "Microsoft.VisualStudio.Code")
                asset = manifest.find("v:Assets/v:Asset", ns)
                self.assertIn(asset.get("Path"), archive.namelist())
                for name in ("extension.js", "editor-sessions.js"):
                    self.assertEqual(archive.read("extension/" + name), (ROOT / "integrations" / "vscode" / name).read_text(encoding="utf-8").encode("utf-8"))
                self.assertIn(b"MIT License", archive.read("extension/LICENSE.txt"))
