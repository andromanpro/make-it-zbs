import contextlib
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest import mock
import zipfile


ROOT = Path(__file__).resolve().parents[2]


def load(name):
    spec = importlib.util.spec_from_file_location(name.replace("-", "_"), ROOT / "tools" / (name + ".py"))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


release = load("build-release")
portable = load("verify-dist")
public = load("export-public-source")
licenses = load("collect-licenses")
secrets = load("run-secret-audit")


class PublicationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)

    def tearDown(self):
        self.temp.cleanup()

    def write(self, relative, content=b"fixture"):
        target = self.root / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(content)
        return target

    def prepare_portable(self):
        for name in ("MakeItZBS.exe", "runtime/bin/oscript.exe", "src/onescript/zbs-run.os",
                     "src/onescript/modules/console-log.os", "lib/net472/ZBSWindowsComponent.dll",
                     "lib/net472/MakeItZBS.ProcessHost.exe", "lib/net472/MakeItZBS.CaptureHost.exe",
                     "licenses/onescript/source.zip", "LICENSE", "PRIVACY.md"):
            self.write(name)
        self.write("config/.env", b"OPENROUTER_API_KEY=\n")
        self.write("config/settings.json", b'{"logging":{"enabled":false}}')
        notice = b"MIT fixture"
        self.write("licenses/nuget/Example/1.0/LICENSE.txt", notice)
        index = {"packages": [{"notices": {"Example/1.0/LICENSE.txt": hashlib.sha256(notice).hexdigest()}}]}
        self.write("licenses/nuget/index.json", json.dumps(index).encode())
        self.manifest()

    def manifest(self):
        with mock.patch.object(release, "DIST", self.root):
            release.write_manifest()

    def test_portable_inventory_rejects_modified_missing_and_unlisted_files(self):
        self.prepare_portable()
        self.assertEqual(portable.verify(self.root)["packages"], 1)
        self.write("LICENSE", b"changed")
        with self.assertRaisesRegex(ValueError, "checksum"):
            portable.verify(self.root)
        self.manifest()
        self.write("private.txt")
        with self.assertRaisesRegex(ValueError, "inventory"):
            portable.verify(self.root)
        (self.root / "private.txt").unlink()
        (self.root / "LICENSE").unlink()
        with self.assertRaisesRegex(ValueError, "inventory"):
            portable.verify(self.root)

    def test_portable_rejects_secrets_mock_mode_and_foreign_libraries_even_with_valid_hashes(self):
        self.prepare_portable()
        self.write("config/.env", b"OPENROUTER_API_KEY=fixture-not-real\n")
        self.manifest()
        with self.assertRaisesRegex(ValueError, "empty provider"):
            portable.verify(self.root)
        self.write("config/.env", b"OPENROUTER_API_KEY=\n")
        for settings in (b'{"dev":{"mockAI":true}}', b'{"logging":{"enabled":true}}'):
            self.write("config/settings.json", settings)
            self.manifest()
            with self.assertRaisesRegex(ValueError, "defaults"):
                portable.verify(self.root)
        self.write("config/settings.json", b"{}")
        self.write("runtime/lib/unrelated.os")
        self.manifest()
        with self.assertRaisesRegex(ValueError, "global script"):
            portable.verify(self.root)

    def test_portable_rejects_personal_data_and_missing_license_payload(self):
        self.prepare_portable()
        self.write("logs/document.txt")
        self.manifest()
        with self.assertRaisesRegex(ValueError, "Personal"):
            portable.verify(self.root)
        (self.root / "logs/document.txt").unlink()
        self.write("licenses/nuget/Example/1.0/LICENSE.txt", b"different terms")
        self.manifest()
        with self.assertRaisesRegex(ValueError, "license inventory"):
            portable.verify(self.root)
        (self.root / "licenses/onescript/source.zip").unlink()
        self.manifest()
        with self.assertRaisesRegex(ValueError, "required"):
            portable.verify(self.root)

    def test_manifest_rejects_traversal_and_duplicate_paths(self):
        self.prepare_portable()
        for name in ("../outside", "/absolute", "C:stream", "a\\b"):
            self.write("MANIFEST.sha256", ("0" * 64 + "  " + name).encode())
            with self.assertRaisesRegex(ValueError, "Unsafe"):
                portable.verify(self.root)
        self.write("MANIFEST.sha256", ("0" * 64 + "  a\n") .encode() * 2)
        with self.assertRaisesRegex(ValueError, "Duplicate"):
            portable.verify(self.root)

    def test_probe_isolated_profile_checks_exit_and_success_marker(self):
        for code, output in ((0, "PORTABLE-SMOKE-PASS"), (1, "failure"), (0, "")):
            result = mock.Mock(returncode=code, stdout=output, stderr="")
            with mock.patch.object(portable.subprocess, "run", return_value=result) as run:
                if code == 0 and output:
                    portable.probe(self.root, Path("probe.os"))
                else:
                    with self.assertRaisesRegex(ValueError, "smoke failed"):
                        portable.probe(self.root, Path("probe.os"))
                kwargs = run.call_args.kwargs
                self.assertEqual(kwargs["env"]["OSCRIPT_PATH"], "")
                self.assertFalse(Path(kwargs["cwd"]).exists())

    def policy(self):
        policy = {"files": ["README.md", "config/public-source.json"],
                  "directories": ["src", "vendor"], "tools": ["build-dist.py"]}
        self.write("config/public-source.json", json.dumps(policy).encode())
        return policy

    def test_public_snapshot_has_only_explicit_sources_and_no_git_history(self):
        policy = self.policy()
        self.assertTrue(public.public_path("vendor/licenses/Microsoft.Web.WebView2/LICENSE.txt", policy))
        paths = ["README.md", "config/public-source.json", "src/main.cs", "src/bin/private.dll",
                 "config/.env", "private-report.md", "vendor/dependency.zip", "tools/build-dist.py"]
        for relative in paths:
            if relative != "config/public-source.json":
                self.write(relative)
        destination = self.root / "dist/public"
        receipt = public.export(self.root, destination, paths, "a" * 40)
        self.assertFalse(receipt["historyIncluded"])
        self.assertNotIn("config/.env", receipt["files"])
        self.assertNotIn("private-report.md", receipt["files"])
        self.assertNotIn("src/bin/private.dll", receipt["files"])
        self.assertFalse((destination / ".git").exists())
        self.assertEqual((destination / "src/main.cs").read_bytes(), b"fixture")
        self.assertEqual(json.loads((destination / "PUBLIC-SOURCE-MANIFEST.json").read_text())["sourceCommit"], "a" * 40)
        with self.assertRaisesRegex(ValueError, "must be new"):
            public.export(self.root, destination, paths, "b" * 40)

    def test_public_export_rejects_unsafe_missing_and_escaping_paths(self):
        policy = self.policy()
        for name in ("../secret", "C:/secret", "/secret", "src\\secret"):
            with self.assertRaisesRegex(ValueError, "Unsafe"):
                public.public_path(name, policy)
        for name in ("src/.env", "src/x.pdb", "src/a.exe.WebView2/cache", "src/.git/config"):
            self.assertFalse(public.public_path(name, policy))
        with self.assertRaisesRegex(ValueError, "within"):
            public.export(self.root, self.root.parent / "outside", [], "a")
        with self.assertRaisesRegex(ValueError, "missing"):
            public.export(self.root, self.root / "dist/new", [], "a")
        self.write("README.md")
        with mock.patch.object(Path, "is_symlink", return_value=True):
            with self.assertRaisesRegex(ValueError, "filesystem"):
                public.export(self.root, self.root / "dist/new", policy["files"], "a")

    def test_runtime_license_collection_covers_dlls_and_detects_staleness(self):
        package = self.root / "cache/example/1.0"
        self.write("cache/example/1.0/example.nuspec", b'<package><metadata><license type="expression">MIT</license><authors>Example</authors></metadata></package>')
        self.write("vendor/licenses/MIT.txt", b"MIT fixture")
        self.write("lib/net472/Example.dll")
        self.write("lib/net472/ZBSWindowsComponent.dll")
        assets = {"packageFolders": {str(self.root / "cache"): {}},
                  "libraries": {"Example/1.0": {"type": "package", "path": "example/1.0"},
                                "App/1.0": {"type": "project"}},
                  "targets": {"net472": {"Example/1.0": {"runtime": {"lib/net472/Example.dll": {}}}}}}
        self.write("src/ZBSWindowsComponent/obj/project.assets.json", json.dumps(assets).encode())
        self.assertEqual(licenses.collect(self.root), (1, 1))
        self.assertEqual(licenses.collect(self.root, check=True), (1, 1))
        self.write("vendor/licenses/Example/1.0/LICENSE.txt", b"tampered")
        with self.assertRaisesRegex(ValueError, "stale"):
            licenses.collect(self.root, check=True)
        self.write("lib/net472/Unmapped.dll")
        with self.assertRaisesRegex(ValueError, "Unmapped"):
            licenses.collect(self.root)
        package.rename(package.with_name("missing"))
        with self.assertRaisesRegex(ValueError, "not restored"):
            licenses.collect(self.root)

    def test_package_notices_preserve_embedded_terms_and_refuse_unknown_licenses(self):
        package = self.root / "package"
        self.write("package/example.nuspec", b'<package><metadata><license type="expression">Unspecified</license></metadata></package>')
        with self.assertRaisesRegex(ValueError, "missing"):
            licenses.package_notice(package, "Unknown", self.root)
        self.write("package/LICENSE.txt", b"original terms")
        _, notices = licenses.package_notice(package, "Unknown", self.root)
        self.assertEqual(notices["LICENSE.txt"], b"original terms")
        (package / "LICENSE.txt").unlink()
        self.write("package/example.nuspec", b'<package><metadata><license type="expression">MPL-2.0</license></metadata></package>')
        self.write("vendor/onescript/LICENSE.txt", b"MPL fixture")
        self.assertEqual(licenses.package_notice(package, "OneScript", self.root)[1]["LICENSE.txt"], b"MPL fixture")
        self.write("package/example.nuspec", b'<package><metadata><license type="expression">MIT</license></metadata></package>')
        self.write("vendor/licenses/MIT.txt", b"MIT fixture")
        self.write("src/CaptureHost/THIRD-PARTY-CAPTURE.txt", b"Original attribution")
        self.assertIn("ATTRIBUTION.txt", licenses.package_notice(package, "Silk.NET.Core", self.root)[1])

    def test_scanner_download_requires_pinned_checksum_and_restores_binary_from_archive(self):
        buffer = io.BytesIO()
        with zipfile.ZipFile(buffer, "w") as archive:
            archive.writestr("gitleaks.exe", b"scanner fixture")
        content = buffer.getvalue()
        cache = self.root / "cache"
        with mock.patch.object(secrets.urllib.request, "urlopen", return_value=io.BytesIO(content)), \
                mock.patch.object(secrets, "ARCHIVE_SHA256", hashlib.sha256(content).hexdigest()):
            binary = secrets.executable(cache)
            self.assertEqual(binary.read_bytes(), b"scanner fixture")
            binary.write_bytes(b"tampered executable")
            self.assertEqual(secrets.executable(cache).read_bytes(), b"scanner fixture")
        (cache / "gitleaks.zip").write_bytes(b"tampered archive")
        with self.assertRaisesRegex(ValueError, "checksum"):
            secrets.executable(cache)

    def test_secret_audit_rejects_wrong_source_findings_and_incomplete_scan(self):
        output = self.root / "audit"
        source = self.root / "public"
        with self.assertRaisesRegex(ValueError, "public export"):
            secrets.scan(source, Path("scanner.exe"), output)
        self.write("public/PUBLIC-SOURCE-MANIFEST.json", b"{}")
        for code, log, findings in ((0, "INF no leaks", []), (1, "WRN leaks", [{"Secret": "REDACTED"}]),
                                    (0, " ERR unreadable archive", []), (0, "", None)):
            def execute(command, **kwargs):
                self.assertIn("--redact=100", command)
                self.assertIn("--max-archive-depth=3", command)
                report = output / "gitleaks-redacted.json"
                if findings is None:
                    report.unlink(missing_ok=True)
                else:
                    report.write_text(json.dumps(findings), encoding="utf-8")
                return mock.Mock(returncode=code, stdout=log, stderr="")
            with mock.patch.object(secrets.subprocess, "run", side_effect=execute):
                if code == 0 and findings == [] and "ERR" not in log:
                    self.assertEqual(secrets.scan(source, Path("scanner.exe"), output)["findings"], 0)
                else:
                    with self.assertRaisesRegex(ValueError, "did not pass"):
                        secrets.scan(source, Path("scanner.exe"), output)


if __name__ == "__main__":
    unittest.main()
