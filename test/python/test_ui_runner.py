import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import MagicMock, patch
import subprocess
import sys
from types import SimpleNamespace

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("ui_runner", ROOT / "tools/run-ui-tests.py")
RUNNER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(RUNNER)


class UiRunnerTests(unittest.TestCase):
    def test_desktop_uia_is_excluded_from_headless_and_required_in_desktop_suite(self):
        self.assertIn("TestCategory!=Desktop", RUNNER.UNIT_FILTER)
        args = SimpleNamespace(word=False, live_codex=False, live_image=False, paint=False, p0_pipeline=False, filter=None)
        self.assertIn("|TestCategory=Desktop", RUNNER.selected_ui_filter(args))

    def test_long_image_and_full_pipeline_timeouts_have_explicit_priority(self):
        args = SimpleNamespace(p0_pipeline=False, live_image=False)
        self.assertEqual(RUNNER.ui_timeout(args), 300)
        args.live_image = True
        self.assertEqual(RUNNER.ui_timeout(args), 600)
        args.p0_pipeline = True
        self.assertEqual(RUNNER.ui_timeout(args), 900)

    def test_complete_p0_routes_require_explicit_opt_in(self):
        args = SimpleNamespace(word=False, live_codex=False, live_image=False, paint=False, p0_pipeline=False, filter=None)
        self.assertIn("TestCategory!=P0Pipeline", RUNNER.selected_ui_filter(args))
        args.p0_pipeline = True
        self.assertNotIn("TestCategory!=P0Pipeline", RUNNER.selected_ui_filter(args))
        args.filter = "FullyQualifiedName~RealPaint"
        self.assertIn("FullyQualifiedName~RealPaint", RUNNER.selected_ui_filter(args))

    def test_word_pipeline_refuses_an_incomplete_distribution(self):
        with tempfile.TemporaryDirectory() as directory:
            with self.assertRaisesRegex(RuntimeError, "complete distribution"):
                RUNNER.prepare_pipeline(Path(directory), Path(directory) / "stage")

    def test_word_pipeline_uses_synthetic_settings_and_subscription_without_changing_package(self):
        with tempfile.TemporaryDirectory() as directory:
            package = Path(directory) / "package"
            for name in ("runtime/bin/oscript.exe", "src/onescript/zbs-run.os", "lib/net472/ZBSWindowsComponent.dll"):
                path = package / name
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(b"fixture")
            destination = Path(directory) / "stage"
            RUNNER.prepare_pipeline(package, destination)
            settings = json.loads((destination / "config/settings.json").read_text(encoding="utf-8"))
            models = json.loads((destination / "config/models.json").read_text(encoding="utf-8"))
            self.assertEqual(settings["general"]["hotkey"], "Ctrl+Shift+F24")
            self.assertTrue(settings["general"]["codexOfferShown"])
            self.assertTrue(settings["replace"]["enabled"])
            self.assertFalse(settings["preview"]["stream"])
            self.assertTrue(all(not profile["enabled"] for profile in settings["aiChat"]["profiles"].values()))
            self.assertEqual(models["contexts"]["Word"]["provider"], "codex")
            self.assertEqual(models["contexts"]["Word"]["codexReasoningEffort"], "low")
            self.assertEqual((destination / "config/.env").read_text(encoding="ascii"), "OPENROUTER_API_KEY=ui-test-placeholder\n")
            self.assertFalse((package / "config").exists())

    def test_command_failure_retains_the_diagnostic_log(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "failure.log"
            with self.assertRaisesRegex(RuntimeError, "failed \\(2\\)"):
                RUNNER.run([sys.executable, "-c", "print('controlled-failure');raise SystemExit(2)"], path)
            self.assertIn("controlled-failure", path.read_text(encoding="utf-8"))

    def test_timeout_terminates_only_the_started_process_tree(self):
        with tempfile.TemporaryDirectory() as directory:
            process = MagicMock(pid=12345)
            process.wait.side_effect = [subprocess.TimeoutExpired(["fixture"], 300), 0]
            process.poll.return_value = None
            process.__enter__.return_value = process
            with patch.object(RUNNER.subprocess, "Popen", return_value=process), \
                    patch.object(RUNNER.subprocess, "run") as terminate:
                with self.assertRaisesRegex(RuntimeError, "timed out"):
                    RUNNER.run(["fixture"], Path(directory) / "timeout.log")
                if RUNNER.os.name == "nt":
                    self.assertEqual(terminate.call_args.args[0], ["taskkill", "/PID", "12345", "/T", "/F"])
                process.kill.assert_called_once()

    @unittest.skipUnless(RUNNER.os.name == "nt", "Windows process-tree termination")
    def test_tree_termination_failure_still_kills_the_owned_root(self):
        with tempfile.TemporaryDirectory() as directory:
            process = MagicMock(pid=12345)
            process.wait.side_effect = [subprocess.TimeoutExpired(["fixture"], 300), 0]
            process.poll.return_value = None
            process.__enter__.return_value = process
            with patch.object(RUNNER.subprocess, "Popen", return_value=process), \
                    patch.object(RUNNER.subprocess, "run", side_effect=OSError("controlled termination failure")):
                with self.assertRaisesRegex(OSError, "controlled termination failure"):
                    RUNNER.run(["fixture"], Path(directory) / "timeout.log")
                process.kill.assert_called_once()

    def test_zero_tests_or_skipped_tests_are_not_success(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "result.trx"
            for total, passed in ((0, 0), (3, 2), (1, 1)):
                path.write_text(f'<TestRun xmlns="urn:test"><ResultSummary><Counters total="{total}" '
                                f'passed="{passed}" failed="0" /></ResultSummary></TestRun>', encoding="utf-8")
                if total == passed == 1:
                    self.assertEqual(RUNNER.check_result(path, 1)["passed"], 1)
                else:
                    with self.assertRaises(RuntimeError):
                        RUNNER.check_result(path, 1)

    def test_missing_counters_are_not_success(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "result.trx"
            path.write_text("<TestRun />", encoding="utf-8")
            with self.assertRaises(RuntimeError):
                RUNNER.check_result(path, 1)

    def test_verified_deployment_preserves_relative_paths_and_bytes(self):
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / "source"
            (source / "runtimes/win-x64").mkdir(parents=True)
            (source / "runtimes/win-x64/native.dll").write_bytes(b"owned-build-fixture")
            destination = Path(directory) / "deployment"
            inventory = RUNNER.copy_verified(source, destination)
            self.assertEqual(set(inventory), {"runtimes/win-x64/native.dll"})
            self.assertEqual((destination / "runtimes/win-x64/native.dll").read_bytes(), b"owned-build-fixture")

    def test_installed_overlay_rejects_stale_binary_and_never_copies_personal_config(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / "installed"
            stage = Path(directory) / "stage"
            with self.assertRaisesRegex(RuntimeError, "Incomplete"):
                RUNNER.installed_artifacts(root, stage)
            (root / "lib/net472").mkdir(parents=True)
            (root / "ui").mkdir()
            (root / "config").mkdir()
            (root / "config/.env").write_text("private-fixture", encoding="ascii")
            (root / "lib/net472/MakeItZBS.CaptureHost.exe").write_bytes(b"worker")
            (root / "lib/net472/ZBSWindowsComponent.dll").write_bytes(b"current")
            (root / "ui/settings.html").write_text("settings", encoding="ascii")
            (stage / "host").mkdir(parents=True)
            binary = stage / "host/ZBSWindowsComponent.dll"
            binary.write_bytes(b"stale")
            with self.assertRaisesRegex(RuntimeError, "differs"):
                RUNNER.installed_artifacts(root, stage)
            binary.write_bytes(b"current")
            inventory = RUNNER.installed_artifacts(root, stage)
            self.assertEqual(inventory["ui"].keys(), {"settings.html"})
            self.assertFalse((stage / "config").exists())
            self.assertEqual(binary.read_bytes(), b"current")
