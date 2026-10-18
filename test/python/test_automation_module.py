import contextlib
import importlib.util
import io
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest import mock
import xml.etree.ElementTree as ET


ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("automation_runner", ROOT / "libraries/onescript-automation/run-tests.py")
runner = importlib.util.module_from_spec(spec)
spec.loader.exec_module(runner)


class AutomationRunnerTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name)

    def tearDown(self):
        self.temporary.cleanup()

    def report(self, total, passed):
        report = ET.Element("TestRun")
        summary = ET.SubElement(report, "ResultSummary")
        ET.SubElement(summary, "Counters", total=str(total), passed=str(passed))
        ET.ElementTree(report).write(self.root / "results.trx", encoding="utf-8")

    def test_zero_discovered_tests_skips_partial_passes_and_failed_process_are_not_success(self):
        for total, passed, code in ((0, 0, 0), (37, 36, 0), (36, 36, 0), (37, 37, 1)):
            with self.subTest(total=total, passed=passed, code=code), contextlib.redirect_stdout(io.StringIO()):
                self.report(total, passed)
                with self.assertRaisesRegex(RuntimeError, "Incomplete"):
                    runner.verify_report(self.root, code, False)
                receipt = json.loads((self.root / "summary.json").read_text(encoding="utf-8"))
                self.assertEqual(receipt["status"], "failed")

    def test_headless_and_desktop_counters_are_checked_separately(self):
        for desktop, count in ((False, 37), (True, 4)):
            with contextlib.redirect_stdout(io.StringIO()):
                self.report(count, count)
                runner.verify_report(self.root, 0, desktop)
                self.assertEqual(json.loads((self.root / "summary.json").read_text())["status"], "passed")

    def test_desktop_is_opt_in_and_only_desktop_coverage_merges_existing_results(self):
        with mock.patch.object(runner, "ROOT", self.root):
            plain = runner.test_command(Path("tests.csproj"), self.root, self.root, False, False)
            self.assertIn("TestCategory!=Desktop", plain)
            self.assertIn("--no-build", plain)
            desktop = runner.test_command(Path("tests.csproj"), self.root, self.root, True, True)
            self.assertFalse(any(value.startswith("/p:MergeWith") for value in desktop))
            coverage = self.root / "artifacts/coverage/automation/coverage.json"
            coverage.write_text("{}", encoding="utf-8")
            desktop = runner.test_command(Path("tests.csproj"), self.root, self.root, True, True)
            self.assertIn("/p:MergeWith=" + str(coverage), desktop)
            headless = runner.test_command(Path("tests.csproj"), self.root, self.root, False, True)
            self.assertFalse(any(value.startswith("/p:MergeWith") for value in headless))

    def test_timeout_terminates_only_the_process_tree_owned_by_this_invocation(self):
        process = mock.MagicMock(pid=12345)
        process.wait.side_effect = [subprocess.TimeoutExpired("fixture", 1), 0]
        process.poll.return_value = None
        context = mock.MagicMock()
        context.__enter__.return_value = process
        with mock.patch.object(runner.subprocess, "Popen", return_value=context), \
                mock.patch.object(runner.subprocess, "run") as terminate, \
                mock.patch.object(runner.os, "name", "nt"):
            with self.assertRaisesRegex(RuntimeError, "deadline"):
                runner.run_owned(["fixture"], {}, io.StringIO(), 1)
            self.assertEqual(terminate.call_args.args[0], ["taskkill", "/PID", "12345", "/T", "/F"])
            process.kill.assert_called_once()

    def test_completed_process_does_not_trigger_termination(self):
        process = mock.MagicMock()
        process.wait.return_value = 0
        context = mock.MagicMock()
        context.__enter__.return_value = process
        with mock.patch.object(runner.subprocess, "Popen", return_value=context):
            self.assertEqual(runner.run_owned(["fixture"], {}, io.StringIO(), 1), 0)
            process.kill.assert_not_called()
