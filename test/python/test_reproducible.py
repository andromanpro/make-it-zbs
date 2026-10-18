import importlib.util
from pathlib import Path
import unittest
import hashlib
import tempfile
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("reproducible", ROOT / "tools/check-reproducible.py")
BUILDER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(BUILDER)


class ReproducibilityTests(unittest.TestCase):
    def test_shipped_inventory_rejects_changed_or_missing_dependencies_and_writes_real_hash(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            build = root / "src/ZBSWindowsComponent/bin/Release/net472"
            shipped = root / "lib/net472"
            build.mkdir(parents=True)
            shipped.mkdir(parents=True)
            for name in ("ZBSWindowsComponent.dll", "helper.exe", "helper.exe.config"):
                (build / name).write_bytes(b"correct bytes")
                (shipped / name).write_bytes(b"correct bytes")
            digest = BUILDER.verify_shipped(root)
            self.assertEqual(digest, hashlib.sha256(b"correct bytes").hexdigest())
            self.assertIn(digest, (root / "artifacts/ZBSWindowsComponent.sha256").read_text())
            (shipped / "helper.exe").write_bytes(b"old version")
            with self.assertRaisesRegex(ValueError, "helper.exe"):
                BUILDER.verify_shipped(root)
            (shipped / "helper.exe").unlink()
            with self.assertRaisesRegex(ValueError, "Missing"):
                BUILDER.verify_shipped(root)
            self.assertEqual(BUILDER.verify_shipped(root, compare=False), digest)

    def test_commit_id_changes_are_exercised_even_when_the_worktree_does_not_change(self):
        builds = []
        with patch.object(BUILDER, "snapshot", return_value={"component": "identical"}):
            result = BUILDER.verify_builds(builds.append)
        self.assertEqual(builds, ["0" * 40, "f" * 40])
        self.assertEqual(len(result), 2)

    def test_changed_binary_or_failed_build_cannot_be_reported_as_reproducible(self):
        with patch.object(BUILDER, "snapshot", side_effect=[{"component": "old"}, {"component": "new"}]):
            with self.assertRaisesRegex(RuntimeError, "SourceRevisionId"):
                BUILDER.verify_builds(lambda revision: None)
        with self.assertRaisesRegex(OSError, "controlled failure"):
            BUILDER.verify_builds(lambda revision: (_ for _ in ()).throw(OSError("controlled failure")))
