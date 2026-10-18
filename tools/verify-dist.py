"""Verify a portable release inventory and smoke it without global script libraries."""
import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import subprocess
import tempfile


def read_manifest(root):
    expected = {}
    for line in (root / "MANIFEST.sha256").read_text(encoding="utf-8").splitlines():
        digest, relative = line.split("  ", 1)
        path = PurePosixPath(relative)
        if path.is_absolute() or ".." in path.parts or "\\" in relative or ":" in relative:
            raise ValueError("Unsafe manifest entry")
        if relative in expected:
            raise ValueError("Duplicate manifest entry")
        expected[relative] = digest
    return expected


def verify_contents(root, expected):
    actual = {p.relative_to(root).as_posix(): p for p in root.rglob("*") if p.is_file()}
    if set(actual) != set(expected) | {"MANIFEST.sha256"}:
        raise ValueError("Distribution inventory differs from manifest")
    for relative, digest in expected.items():
        path = actual[relative]
        if path.is_symlink() or not path.resolve().is_relative_to(root):
            raise ValueError("Distribution contains a filesystem link")
        if hashlib.sha256(path.read_bytes()).hexdigest() != digest:
            raise ValueError(f"Distribution checksum mismatch: {relative}")
    return actual


def verify_defaults(root, actual):
    required = ["MakeItZBS.exe", "runtime/bin/oscript.exe", "src/onescript/zbs-run.os",
                "src/onescript/modules/console-log.os", "lib/net472/ZBSWindowsComponent.dll",
                "lib/net472/MakeItZBS.ProcessHost.exe", "lib/net472/MakeItZBS.CaptureHost.exe",
                "licenses/onescript/source.zip", "licenses/nuget/index.json", "LICENSE", "PRIVACY.md"]
    if any(name not in actual for name in required):
        raise ValueError("Distribution is missing a required application or license file")
    for relative in actual:
        parts = PurePosixPath(relative).parts
        if any(part in (".git", "logs", "artifacts", "EBWebView") or part.endswith(".exe.WebView2") for part in parts):
            raise ValueError("Personal/runtime-generated data in release")
    if (root / "runtime/lib").exists():
        raise ValueError("Unexpected global script libraries")
    if (root / "config/.env").read_bytes() != b"OPENROUTER_API_KEY=\n":
        raise ValueError("Release must have an empty provider key")
    settings = json.loads((root / "config/settings.json").read_text(encoding="utf-8-sig"))
    if settings.get("dev", {}).get("mockAI") or settings.get("logging", {}).get("enabled"):
        raise ValueError("Release defaults enable test mode or content logging")


def verify_licenses(root, expected):
    index = json.loads((root / "licenses/nuget/index.json").read_text(encoding="utf-8"))
    for package in index["packages"]:
        for relative, digest in package["notices"].items():
            name = "licenses/nuget/" + relative
            if name not in expected or expected[name] != digest:
                raise ValueError("Dependency license inventory mismatch")
    return len(index["packages"])


def verify(root):
    root = root.resolve()
    expected = read_manifest(root)
    actual = verify_contents(root, expected)
    verify_defaults(root, actual)
    packages = verify_licenses(root, expected)
    return {"status": "passed", "files": len(expected), "packages": packages}


def probe(root, script):
    with tempfile.TemporaryDirectory(prefix="zbs-portable-probe-") as temporary:
        env = os.environ.copy()
        env.update(OSCRIPT_PATH="", OSLIB="", OSCRIPT_CONFIG="", HOME=temporary,
                   USERPROFILE=temporary)
        # Absolute runtime path; neither PATH nor a user profile supplies OneScript libraries.
        result = subprocess.run([str(root / "runtime/bin/oscript.exe"), "-encoding=utf-8",
                                 str(script), str(root)], cwd=temporary, env=env,
                                capture_output=True, text=True, encoding="utf-8", timeout=60)
        if result.returncode or "PORTABLE-SMOKE-PASS" not in result.stdout:
            raise ValueError("Portable runtime smoke failed: " + result.stdout + result.stderr)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("root", type=Path)
    parser.add_argument("--run-probe", action="store_true")
    args = parser.parse_args()
    root = args.root.resolve()
    summary = verify(root)
    if args.run_probe:
        probe(root, Path(__file__).resolve().parents[1] / "test/distribution/portable-smoke.os")
        summary["standaloneRuntime"] = "passed"
    print(json.dumps(summary))
