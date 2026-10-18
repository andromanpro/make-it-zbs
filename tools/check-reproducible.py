"""Verify shipped .NET binaries do not change solely because a commit was created."""
import argparse
import hashlib
import json
import os
import shutil
from pathlib import Path
import subprocess

ROOT = Path(__file__).resolve().parents[1]
BINARIES = {"component": "src/ZBSWindowsComponent/bin/Release/net472/ZBSWindowsComponent.dll",
            "processHost": "src/ProcessHost/bin/Release/net472/MakeItZBS.ProcessHost.exe",
            "captureHost": "src/CaptureHost/bin/Release/net472/MakeItZBS.CaptureHost.exe"}


def snapshot():
    return {name: hashlib.sha256((ROOT / relative).read_bytes()).hexdigest()
            for name, relative in BINARIES.items()}


def verify_builds(build):
    results = []
    for revision in ("0" * 40, "f" * 40):
        build(revision)
        results.append({"sourceRevisionId": revision, "sha256": snapshot()})
    if results[0]["sha256"] != results[1]["sha256"]:
        raise RuntimeError("Release binaries depend on SourceRevisionId; a commit would invalidate shipped files")
    return results


def save_diagnostics(root, build):
    diagnostics = root / "artifacts/reproducibility-diagnostics"
    diagnostics.mkdir(parents=True, exist_ok=True)
    for item in build.iterdir():
        if item.suffix in (".exe", ".dll", ".pdb") and item.name.startswith(("MakeItZBS.", "ZBSWindowsComponent")):
            shutil.copyfile(item, diagnostics / item.name)
    for project in ("CaptureHost", "ProcessHost", "ZBSWindowsComponent"):
        obj = root / "src" / project / "obj/Release/net472"
        for item in obj.glob("*Assembly*.cs"):
            shutil.copyfile(item, diagnostics / (project + "-" + item.name))


def verify_shipped(root, compare=True):
    build = root / "src/ZBSWindowsComponent/bin/Release/net472"
    if compare:
        for path in build.rglob("*"):
            if path.is_file() and path.suffix in (".dll", ".exe", ".config"):
                target = root / "lib/net472" / path.relative_to(build)
                if not target.is_file() or hashlib.sha256(path.read_bytes()).digest() != hashlib.sha256(target.read_bytes()).digest():
                    save_diagnostics(root, build)
                    raise ValueError("Missing or stale shipped binary: " + path.relative_to(build).as_posix())
    digest = hashlib.sha256((build / "ZBSWindowsComponent.dll").read_bytes()).hexdigest()
    output = root / "artifacts/ZBSWindowsComponent.sha256"
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(digest + "  ZBSWindowsComponent.dll\n", encoding="ascii", newline="\n")
    return digest


def main():
    sdk = subprocess.check_output(["dotnet", "--version"], cwd=ROOT, text=True).strip()

    def build(revision):
        environment = dict(os.environ, DOTNET_CLI_UI_LANGUAGE="ru-RU", DOTNET_ROLL_FORWARD="Disable")
        subprocess.run(["dotnet", "build", "-c", "Release", "src/ZBSWindowsComponent/ZBSWindowsComponent.csproj",
                        "--no-incremental", f"-p:SourceRevisionId={revision}"], cwd=ROOT, check=True, env=environment)

    results = verify_builds(build)
    output = ROOT / "artifacts/reproducible-build.json"
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps({"status": "passed", "sdk": sdk, "builds": results}, indent=2), encoding="utf-8")
    print(f"Release reproducibility verified across commit identities: {output}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--verify-shipped", action="store_true")
    parser.add_argument("--write-hash", action="store_true")
    args = parser.parse_args()
    if args.verify_shipped or args.write_hash:
        print(verify_shipped(ROOT, args.verify_shipped))
    else:
        main()
