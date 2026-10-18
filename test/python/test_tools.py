import contextlib
import hashlib
import importlib.util
import io
import json
import os
import pathlib
import stat
import subprocess
import sys
import tempfile
import unittest
import zipfile
import xml.etree.ElementTree as ET
from unittest import mock


ROOT = pathlib.Path(__file__).resolve().parents[2]


def load_tool(module_name, file_name):
    spec = importlib.util.spec_from_file_location(module_name, ROOT / "tools" / file_name)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


build_release = load_tool("zbs_build_release", "build-release.py")
check_secrets = load_tool("zbs_check_secrets", "check-secrets.py")
build_dist = load_tool("zbs_build_dist", "build-dist.py")


class DpiConfigTests(unittest.TestCase):
    def test_portable_oscript_enables_per_monitor_dpi(self):
        config = ROOT / "src" / "runtime" / "oscript.exe.config"
        root = ET.parse(config).getroot()
        settings = {
            item.attrib["key"]: item.attrib["value"]
            for item in root.findall(
                "./System.Windows.Forms.ApplicationConfigurationSection/add")
        }

        self.assertEqual(settings["DpiAwareness"], "PerMonitorV2")
        self.assertEqual(
            settings["ToolStrip.DisableHighDpiImprovements"], "false")


class PowerShellScriptTests(unittest.TestCase):
    def test_release_publisher_is_utf8_and_parses_in_windows_powershell(self):
        script = ROOT / "tools" / "publish-gitea-release.ps1"
        self.assertTrue(script.read_bytes().startswith(b"\xef\xbb\xbf"))
        source = script.read_text(encoding="utf-8-sig")
        self.assertIn(
            "$payloadBytes = [Text.Encoding]::UTF8.GetBytes($payload)", source)
        self.assertIn(
            '-ContentType "application/json; charset=utf-8" -Body $payloadBytes',
            source)
        escaped_path = str(script).replace("'", "''")
        command = (
            "$tokens=$null; $errors=$null; "
            "[System.Management.Automation.Language.Parser]::ParseFile("
            f"'{escaped_path}',[ref]$tokens,[ref]$errors) | Out-Null; "
            "if ($errors.Count) { $errors | ForEach-Object Message; exit 1 }")
        result = subprocess.run(
            ["powershell.exe", "-NoProfile", "-NonInteractive", "-Command", command],
            capture_output=True, text=True, encoding="utf-8", errors="replace")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)


class OpenRouterClientContractTests(unittest.TestCase):
    def test_api_paths_and_attribution_use_production_defaults(self):
        source = (ROOT / "src" / "onescript" / "modules" /
                  "openrouter-client.os").read_text(encoding="utf-8-sig")

        self.assertIn('БазовыйURL = "https://openrouter.ai/api/v1";', source)
        self.assertIn('SiteURL = "https://androman.pro";', source)
        self.assertIn('ПолныйURL = БазовыйURL + Путь;', source)
        self.assertIn('ВыполнитьВебЗапрос("/chat/completions"', source)
        self.assertIn('ВыполнитьВебЗапрос("/models", "GET")', source)
        self.assertIn('HTTPТранспорт = Новый ТранспортOpenRouterHTTP();', source)
        self.assertIn('Возврат HTTPТранспорт.Выполнить(', source)
        self.assertNotIn("github.com/yourusername", source)
        self.assertNotIn('ВыполнитьВебЗапрос("/api/v1/', source)


class BuildReleaseTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.repo = pathlib.Path(self.temp.name)
        self.dist = self.repo / "dist" / "MakeItZBS"
        self.releases = self.repo / "dist" / "releases"
        self.dist.mkdir(parents=True)
        self.globals = mock.patch.multiple(
            build_release, REPO=self.repo, DIST=self.dist, RELEASES=self.releases)
        self.globals.start()

    def tearDown(self):
        self.globals.stop()
        self.temp.cleanup()

    def test_hash_epoch_timestamp_and_build_info(self):
        source = self.dist / "data.bin"
        source.write_bytes(b"payload")
        self.assertEqual(build_release.sha256(source), hashlib.sha256(b"payload").hexdigest())

        with mock.patch.dict(os.environ, {"SOURCE_DATE_EPOCH": "123"}):
            self.assertEqual(build_release.source_epoch(), 123)
        with mock.patch.dict(os.environ, {}, clear=True), mock.patch.object(
                build_release, "run", return_value="456"):
            self.assertEqual(build_release.source_epoch(), 456)

        self.assertEqual(build_release.zip_timestamp(0), (1980, 1, 1, 0, 0, 0))
        with mock.patch.object(build_release, "run", return_value="abc123"):
            build_release.write_build_info(456)
        info = json.loads((self.dist / "BUILD-INFO.json").read_text(encoding="utf-8"))
        self.assertEqual(info, {"commit": "abc123", "sourceDateEpoch": 456, "schemaVersion": 1})
        for status, expected in (("", False), (" M README.md", True)):
            with mock.patch.object(build_release, "run", side_effect=["abc123", status]):
                build_release.write_build_info(456, "v0.5.0-beta.4")
            info = json.loads((self.dist / "BUILD-INFO.json").read_text(encoding="utf-8"))
            self.assertEqual(info["dirty"], expected)
            self.assertEqual(info["version"], "v0.5.0-beta.4")

    def test_manifest_and_zip_are_complete_and_deterministic(self):
        (self.dist / "nested").mkdir()
        (self.dist / "a.txt").write_text("a", encoding="utf-8")
        (self.dist / "nested" / "b.txt").write_text("b", encoding="utf-8")
        manifest = build_release.write_manifest()
        manifest_text = manifest.read_text(encoding="utf-8")
        self.assertIn("a.txt", manifest_text)
        self.assertIn("nested/b.txt", manifest_text)
        self.assertNotIn("MANIFEST.sha256  MANIFEST.sha256", manifest_text)

        archive, checksum = build_release.create_zip("1.2.3", 1_700_000_000)
        first_hash = build_release.sha256(archive)
        archive.unlink()
        checksum.unlink()
        second_archive, second_checksum = build_release.create_zip("1.2.3", 1_700_000_000)
        self.assertEqual(build_release.sha256(second_archive), first_hash)
        self.assertIn(first_hash, second_checksum.read_text(encoding="ascii"))
        with zipfile.ZipFile(second_archive) as package:
            self.assertIn("MakeItZBS/a.txt", package.namelist())
            self.assertEqual(package.getinfo("MakeItZBS/a.txt").external_attr, 0o100644 << 16)

    def test_clean_tree_rules_and_shortcut_removal(self):
        build_release.ensure_source_tree_is_clean(False, True)
        with mock.patch.object(build_release, "run", return_value=""):
            build_release.ensure_source_tree_is_clean(False, False)
        with mock.patch.object(
                build_release, "run", return_value=" M lib/net472/ZBSWindowsComponent.dll"):
            build_release.ensure_source_tree_is_clean(True, False)
        with mock.patch.object(build_release, "run", return_value=" M ui/settings.js"):
            with self.assertRaises(SystemExit):
                build_release.ensure_source_tree_is_clean(False, False)

        shortcut = self.dist / "Make it ZBS.lnk"
        build_release.remove_nondeterministic_shortcut()
        shortcut.write_bytes(b"link")
        build_release.remove_nondeterministic_shortcut()
        self.assertFalse(shortcut.exists())

    def test_signature_verification_and_main_orchestration(self):
        with mock.patch.object(build_release.subprocess, "run") as run:
            build_release.verify_signatures()
        command = run.call_args.args[0]
        self.assertEqual(command[0:4], ["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass"])

        checksum = self.repo / "release.sha256"
        checksum.write_text("hash  release.zip\n", encoding="ascii")
        archive = self.repo / "release.zip"
        manifest = self.repo / "manifest.sha256"
        with mock.patch.object(sys, "argv", ["build-release.py", "--version", "bad version!", "--require-signed"]), \
                mock.patch.object(build_release, "ensure_source_tree_is_clean") as clean, \
                mock.patch.object(build_release.subprocess, "run") as run_process, \
                mock.patch.object(build_release, "remove_nondeterministic_shortcut") as remove_shortcut, \
                mock.patch.object(build_release, "verify_signatures") as verify, \
                mock.patch.object(build_release, "source_epoch", return_value=123), \
                mock.patch.object(build_release, "write_build_info") as write_info, \
                mock.patch.object(build_release, "write_manifest", return_value=manifest), \
                mock.patch.object(build_release, "create_zip", return_value=(archive, checksum)) as create_zip, \
                contextlib.redirect_stdout(io.StringIO()):
            build_release.main()
        clean.assert_called_once_with(True, False)
        run_process.assert_called_once()
        remove_shortcut.assert_called_once()
        verify.assert_called_once()
        write_info.assert_called_once_with(123, "bad-version")
        create_zip.assert_called_once_with("bad-version", 123)


class CheckSecretsTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.repo = pathlib.Path(self.temp.name)
        self.repo_patch = mock.patch.object(check_secrets, "REPO", self.repo)
        self.repo_patch.start()

    def tearDown(self):
        self.repo_patch.stop()
        self.temp.cleanup()

    def test_reports_secrets_and_skips_allowed_or_binary_lines(self):
        (self.repo / "bad.txt").write_text("sk-or-v1-" + "x" * 24, encoding="utf-8")
        (self.repo / "allowed.txt").write_text(
            "gitea_" + "y" * 24 + "  # secret-scan: allow", encoding="utf-8")
        (self.repo / "binary.bin").write_bytes(b"\xff\xfe")
        files = b"bad.txt\0allowed.txt\0binary.bin\0missing.txt\0"
        with mock.patch.object(check_secrets.subprocess, "check_output", return_value=files), \
                contextlib.redirect_stdout(io.StringIO()) as output:
            result = check_secrets.main()
        self.assertEqual(result, 1)
        self.assertIn("bad.txt:1", output.getvalue())
        self.assertNotIn("allowed.txt", output.getvalue())

    def test_clean_scan_returns_success(self):
        (self.repo / "safe.txt").write_text("ordinary text", encoding="utf-8")
        with mock.patch.object(
                check_secrets.subprocess, "check_output", return_value=b"safe.txt\0"), \
                contextlib.redirect_stdout(io.StringIO()) as output:
            result = check_secrets.main()
        self.assertEqual(result, 0)
        self.assertIn("not found", output.getvalue())


class BuildDistTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.repo = pathlib.Path(self.temp.name) / "repo"
        self.ovm = pathlib.Path(self.temp.name) / "ovm"
        self.dist = self.repo / "dist" / "MakeItZBS"
        self.repo.mkdir()
        self.globals = mock.patch.multiple(
            build_dist, REPO=str(self.repo), DIST=str(self.dist))
        self.globals.start()

    def tearDown(self):
        self.globals.stop()
        self.temp.cleanup()

    def prepare_sources(self):
        launcher = self.repo / "src" / "Launcher" / "bin" / "Release" / "net472" / "MakeItZBS.exe"
        launcher.parent.mkdir(parents=True)
        launcher.write_bytes(b"exe")
        oscript = self.ovm / "bin" / "oscript.exe"
        oscript.parent.mkdir(parents=True)
        oscript.write_bytes(b"oscript")
        webview_profile = self.ovm / "bin" / "oscript.exe.WebView2"
        webview_profile.mkdir()
        (webview_profile / "user-data.bin").write_bytes(b"private cache")
        (self.ovm / "lib").mkdir()
        (self.ovm / "lib" / "library.os").write_text("library", encoding="utf-8")
        runtime_config = self.repo / "src" / "runtime" / "oscript.exe.config"
        runtime_config.parent.mkdir(parents=True)
        runtime_config.write_text("<configuration />", encoding="utf-8")
        for relative in ("src/onescript", "lib/net472", "ui"):
            directory = self.repo / relative
            directory.mkdir(parents=True, exist_ok=True)
            (directory / "content.txt").write_text(relative, encoding="utf-8")
        (self.repo / "lib" / "net472" / "ZBSWindowsComponent.dll").write_bytes(b"dll")
        vendor = self.repo / "vendor" / "onescript"
        vendor.mkdir(parents=True)
        files = {"bin/oscript.exe": b"private runtime", "bin/ProDotNetZip.dll": b"fixed zip",
                 "bin/oscript.exe.config": b"<configuration />"}
        archive = vendor / "runtime.zip"
        with zipfile.ZipFile(archive, "w") as stream:
            for name, content in files.items():
                stream.writestr(name, content)
        (vendor / "manifest.json").write_text(json.dumps({
            "sha256": hashlib.sha256(archive.read_bytes()).hexdigest(),
            "files": {name: hashlib.sha256(content).hexdigest() for name, content in files.items()}
        }), encoding="utf-8")
        (vendor / "LICENSE.txt").write_text("fixture license", encoding="utf-8")
        for name in ("LICENSE", "THIRD-PARTY-NOTICES.md", "PRIVACY.md", "INSTALL.md",
                     "README.md", "SECURITY.md", "TESTING.md", "CONTRIBUTING.md",
                     "docs/replacement-backups.md", "docs/images/wiki-hero-magic-button.png",
                     "integrations/vscode/README.md"):
            (self.repo / name).parent.mkdir(parents=True, exist_ok=True)
            (self.repo / name).write_text("fixture notice", encoding="utf-8")
        (self.repo / "vendor/licenses").mkdir()
        (self.repo / "vendor/licenses/index.json").write_text("{}", encoding="utf-8")

    def test_standalone_runtime_does_not_discover_or_copy_ovm(self):
        self.prepare_sources()
        with mock.patch.dict(os.environ, {"OSCRIPT_PATH": str(self.ovm / "bin/oscript.exe")}), \
                mock.patch.object(build_dist.shutil, "which", side_effect=AssertionError("PATH lookup")):
            prepared = pathlib.Path(build_dist.prepare_private_runtime())
        self.assertFalse((prepared / "lib").exists())
        self.assertEqual((prepared / "bin/oscript.exe").read_bytes(), b"private runtime")

    def test_private_runtime_rejects_corrupt_archive_before_writing(self):
        self.prepare_sources()
        archive = self.repo / "vendor/onescript/runtime.zip"
        archive.write_bytes(archive.read_bytes() + b"corruption")
        destination = self.repo / "private"
        with self.assertRaisesRegex(ValueError, "archive checksum"):
            build_dist.install_private_runtime(str(destination))
        self.assertFalse(destination.exists())

    def test_private_runtime_rejects_escaping_and_obsolete_entries(self):
        self.prepare_sources()
        vendor = self.repo / "vendor/onescript"
        for name in ("../outside.dll", "bin/../../outside.dll", "bin\\outside.dll",
                     "bin/C:payload.dll", "bin/dOtNeTzip.dll"):
            with self.subTest(name=name):
                with zipfile.ZipFile(vendor / "runtime.zip", "w") as archive:
                    archive.writestr(name, b"payload")
                manifest = {"sha256": hashlib.sha256((vendor / "runtime.zip").read_bytes()).hexdigest(),
                            "files": {name: hashlib.sha256(b"payload").hexdigest()}}
                (vendor / "manifest.json").write_text(json.dumps(manifest), encoding="utf-8")
                with self.assertRaises(ValueError):
                    build_dist.install_private_runtime(str(self.repo / "private"))
                self.assertFalse((self.repo / "private").exists())

    def test_private_runtime_rejects_incorrect_inventory_and_file_hash(self):
        self.prepare_sources()
        path = self.repo / "vendor/onescript/manifest.json"
        manifest = json.loads(path.read_text(encoding="utf-8"))
        manifest["files"]["bin/missing.dll"] = "0" * 64
        path.write_text(json.dumps(manifest), encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "inventory"):
            build_dist.install_private_runtime(str(self.repo / "private"))
        del manifest["files"]["bin/missing.dll"]
        manifest["files"]["bin/oscript.exe"] = "0" * 64
        path.write_text(json.dumps(manifest), encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "file checksum"):
            build_dist.install_private_runtime(str(self.repo / "private"))

    def test_private_runtime_is_isolated_idempotent_and_detects_modified_cache(self):
        self.prepare_sources()
        (self.ovm / "bin/DotNetZip.dll").write_bytes(b"obsolete")
        prepared = pathlib.Path(build_dist.prepare_private_runtime())
        self.assertEqual(build_dist.prepare_private_runtime(), str(prepared))
        self.assertEqual((self.ovm / "bin/oscript.exe").read_bytes(), b"oscript")
        self.assertFalse((prepared / "bin/DotNetZip.dll").exists())
        self.assertFalse((prepared / "lib").exists())
        self.assertTrue(prepared.name.endswith("-standalone"))
        (prepared / "bin/oscript.exe").write_bytes(b"tampered")
        with self.assertRaisesRegex(ValueError, "modified"):
            build_dist.prepare_private_runtime()

    def test_cache_rejects_unexpected_external_libraries(self):
        self.prepare_sources()
        prepared = pathlib.Path(build_dist.prepare_private_runtime())
        (prepared / "lib").mkdir()
        with self.assertRaisesRegex(ValueError, "external script libraries"):
            build_dist.prepare_private_runtime()

    def test_preconditions_fail_for_missing_inputs(self):
        with contextlib.redirect_stdout(io.StringIO()), self.assertRaises(SystemExit):
            build_dist.main()
        self.prepare_sources()
        (self.repo / "lib" / "net472" / "ZBSWindowsComponent.dll").unlink()
        with contextlib.redirect_stdout(io.StringIO()), self.assertRaises(SystemExit):
            build_dist.main()

    def test_builds_distribution_and_handles_optional_failures(self):
        self.prepare_sources()

        def git_show(command, cwd):
            if command[-1].endswith("prompts.json"):
                raise subprocess.CalledProcessError(1, command)
            return b"{}\n"

        with mock.patch.object(build_dist.subprocess, "check_output", side_effect=git_show), \
                mock.patch.object(build_dist.subprocess, "run") as run, \
                contextlib.redirect_stdout(io.StringIO()) as output:
            build_dist.main()
        run.assert_called_once()
        self.assertTrue((self.dist / "MakeItZBS.exe").is_file())
        self.assertEqual((self.dist / "runtime" / "bin" / "oscript.exe").read_bytes(), b"private runtime")
        self.assertFalse((self.dist / "runtime" / "bin" / "DotNetZip.dll").exists())
        self.assertFalse((self.dist / "runtime" / "lib").exists())
        self.assertTrue((self.dist / "LICENSE").is_file())
        self.assertTrue((self.dist / "licenses/nuget/index.json").is_file())
        self.assertEqual(
            (self.dist / "runtime" / "bin" / "oscript.exe.config").read_text(
                encoding="utf-8"),
            "<configuration />")
        self.assertFalse(
            (self.dist / "runtime" / "bin" / "oscript.exe.WebView2").exists())
        self.assertEqual((self.dist / "config" / ".env").read_text(encoding="utf-8"),
                         "OPENROUTER_API_KEY=\n")
        self.assertIn("пропуск config/prompts.json", output.getvalue())

        with mock.patch.object(build_dist.subprocess, "check_output", return_value=b"{}\n"), \
                mock.patch.object(build_dist.subprocess, "run", side_effect=OSError("COM unavailable")), \
                contextlib.redirect_stdout(io.StringIO()) as output:
            build_dist.main()
        self.assertIn("ярлык пропущен", output.getvalue())

    def test_file_helpers_cover_missing_and_read_only_paths(self):
        missing = self.repo / "missing"
        build_dist.rmtree_robust(str(missing))

        source = self.repo / "source"
        source.mkdir()
        (source / "keep.txt").write_text("keep", encoding="utf-8")
        (source / ".git").mkdir()
        destination = self.repo / "destination"
        build_dist.copytree(str(source), str(destination), ignore=[".git"])
        self.assertTrue((destination / "keep.txt").is_file())
        self.assertFalse((destination / ".git").exists())

        callback = mock.Mock(side_effect=OSError("still locked"))
        with mock.patch.object(build_dist.os, "chmod") as chmod:
            build_dist._force_rw(callback, str(source / "keep.txt"), None)
        chmod.assert_called_once_with(str(source / "keep.txt"), stat.S_IWRITE)


if __name__ == "__main__":
    unittest.main()
