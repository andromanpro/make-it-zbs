import contextlib
import io
import json
import pathlib
import tempfile
import unittest
from unittest import mock
import xml.etree.ElementTree as ET
import zipfile
from test_tools import load_tool

builder = load_tool("zbs_private_runtime_build", "build-onescript.py")


class PrivateRuntimeBuildTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = pathlib.Path(self.temp.name)
        self.source = self.root / "source"
        self.vendor = self.root / "vendor/onescript"
        for name in (*builder.PROJECTS, "StandaloneRunner", "oscript"):
            path = self.source / "src" / name / (name + ".csproj")
            path.parent.mkdir(parents=True)
            path.write_text('<Project><PropertyGroup><PackageId>old</PackageId></PropertyGroup>'
                            '<ItemGroup><PackageReference Include="DotNetZip" Version="1.13.3" />'
                            '<PackageReference Include="Newtonsoft.Json" Version="13.0.2" />'
                            '<EmbeddedResource Include="DotNetZip.dll" />'
                            '<CopyToStandalone Include="$(TargetDir)DotNetZip.dll" /></ItemGroup></Project>', encoding="utf-8")
        config = self.root / "src/runtime/oscript.exe.config"
        config.parent.mkdir(parents=True)
        config.write_bytes(b"<configuration />")
        (self.source / "LICENSE").write_bytes(b"MPL fixture")
        (self.source / ".env").write_bytes(b"must not be archived")
        self.output = self.source / "src/oscript/bin/Release/net472"
        self.output.mkdir(parents=True)
        (self.output / "oscript.exe").write_bytes(b"runtime")
        (self.output / "ProDotNetZip.dll").write_bytes(b"fixed")
        self.globals = mock.patch.multiple(builder, ROOT=self.root, SOURCE=self.source, VENDOR=self.vendor)
        self.globals.start()

    def tearDown(self):
        self.globals.stop()
        self.temp.cleanup()

    def test_project_update_is_idempotent_and_removes_original_zip(self):
        builder.prepare_projects(self.source)
        builder.prepare_projects(self.source)
        for name, package in builder.PROJECTS.items():
            tree = ET.parse(self.source / "src" / name / (name + ".csproj"))
            self.assertEqual(tree.find(".//PackageId").text, package)
            references = {item.get("Include"): item.get("Version") for item in tree.findall(".//PackageReference")}
            self.assertNotIn("DotNetZip", references)
            self.assertEqual(references["ProDotNetZip"], "1.20.0")
        tree = ET.parse(self.source / "src/StandaloneRunner/StandaloneRunner.csproj")
        self.assertEqual([item.get("Include") for item in tree.findall(".//EmbeddedResource")], ["*.dll"])

    def test_unexpected_revision_stops_before_modifying_or_building(self):
        with mock.patch.object(builder.subprocess, "check_output", return_value="another-revision"), \
                mock.patch.object(builder, "prepare_projects") as prepare, \
                mock.patch.object(builder, "command") as command:
            with self.assertRaisesRegex(RuntimeError, "revision"):
                builder.main()
            prepare.assert_not_called()
            command.assert_not_called()

    def test_build_keeps_license_and_archives_only_tracked_source(self):
        for applied in (0, 1):
            with self.subTest(patch_applied=applied), \
                    mock.patch.object(builder.subprocess, "check_output", side_effect=[builder.REVISION, b"LICENSE\0"]), \
                    mock.patch.object(builder.subprocess, "run", return_value=mock.Mock(returncode=applied)), \
                    mock.patch.object(builder, "command") as command, contextlib.redirect_stdout(io.StringIO()):
                builder.main()
                self.assertEqual(command.call_count, 5 + applied)
            manifest = json.loads((self.vendor / "manifest.json").read_text(encoding="utf-8"))
            self.assertEqual(manifest["revision"], builder.REVISION)
            self.assertIn("bin/ProDotNetZip.dll", manifest["files"])
            with zipfile.ZipFile(self.vendor / "source.zip") as source:
                self.assertEqual(source.namelist(), ["LICENSE"])
            self.assertEqual((self.vendor / "LICENSE.txt").read_bytes(), b"MPL fixture")

    def test_builder_refuses_original_zip_left_in_output(self):
        (self.output / "DotNetZip.dll").write_bytes(b"obsolete")
        with mock.patch.object(builder.subprocess, "check_output", return_value=builder.REVISION), \
                mock.patch.object(builder.subprocess, "run", return_value=mock.Mock(returncode=0)), \
                mock.patch.object(builder, "command"):
            with self.assertRaisesRegex(RuntimeError, "ZIP implementation"):
                builder.main()
        self.assertFalse((self.vendor / "runtime.zip").exists())

    def test_archives_are_deterministic_regardless_of_input_order(self):
        first, second = self.root / "first.zip", self.root / "second.zip"
        builder.write_archive(first, {"b": b"B", "a": b"A"})
        builder.write_archive(second, {"a": b"A", "b": b"B"})
        self.assertEqual(first.read_bytes(), second.read_bytes())
