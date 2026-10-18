"""Package independent automation workers and a rebuildable source archive."""
import argparse
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import uuid

PROJECT = Path(__file__).resolve().parent
ROOT = PROJECT.parents[1]
VERSION = "0.1.0-beta.1"
PROJECT_FILE = "OneScript.Automation.csproj"
CORE_FILES = ("NativeMethods.cs", "Helpers/KeyConverter.cs", "OwnedProcessRunner.cs", "OperationControl.cs",
              "TemporaryFileStore.cs", "DesktopCaptureClient.cs", "CodexCliClient.cs", "SecretRedactor.cs",
              "OpenRouterHttpTransport.cs", "AiClientProfiles.cs")
spec = importlib.util.spec_from_file_location("windows_package", PROJECT.parent / "onescript-windows/build.py")
package = importlib.util.module_from_spec(spec)
spec.loader.exec_module(package)


def source_tree(destination):
    for name in (PROJECT_FILE, "packages.lock.json", "README.md"):
        package.copy(PROJECT / name, destination / name)
    for source in PROJECT.glob("Automation*.cs"):
        package.copy(source, destination / source.name)
    for name in CORE_FILES:
        package.copy(ROOT / "src/ZBSWindowsComponent" / name, destination / "core" / name)
    package.copy(ROOT / "src/CaptureHost/CaptureEngine.cs", destination / "capture/CaptureEngine.cs")
    for worker in ("CaptureHost", "ProcessHost"):
        for source in (ROOT / "src" / worker).iterdir():
            if source.is_file() and source.suffix in (".cs", ".csproj", ".json", ".config", ".txt"):
                package.copy(source, destination / "workers" / worker / source.name)
    for name in ("LICENSE", "global.json", "Directory.Build.props", "NuGet.Config"):
        package.copy(ROOT / name, destination / name)
    for source in (ROOT / "vendor/nuget").glob("*.nupkg"):
        package.copy(source, destination / "vendor/nuget" / source.name)
    for source in (ROOT / "vendor/onescript").iterdir():
        if source.is_file() and source.name != "runtime.zip":
            package.copy(source, destination / "vendor/onescript" / source.name)
    shutil.copytree(ROOT / "vendor/licenses", destination / "vendor/licenses")
    for source in (PROJECT / "examples").glob("*.os"):
        package.copy(source, destination / "examples" / source.name)


def binaries(directory):
    return {p.name: package.sha(p) for p in directory.iterdir()
            if p.is_file() and p.suffix in (".dll", ".exe", ".config")}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--allow-dirty", action="store_true")
    args = parser.parse_args()
    dirty = bool(subprocess.check_output(["git", "status", "--porcelain"], cwd=ROOT))
    if dirty and not args.allow_dirty:
        raise ValueError("Automation release requires a clean source tree")
    output = ROOT / "artifacts/automation-library" / uuid.uuid4().hex
    output.mkdir(parents=True)
    package.run(["dotnet", "build", "-c", "Release", str(PROJECT / PROJECT_FILE), "-p:RestoreLockedMode=true"],
                output / "build.log", ROOT)
    built = PROJECT / "bin/Release/net472"
    inventory = binaries(built)
    if any(name.lower().startswith(("scriptengine", "onescript", "zbswindowscomponent"))
           and name != "OneScript.Automation.dll" for name in inventory):
        raise ValueError("The binary package must not replace a host's OneScript engine")
    info = {"version": VERSION, "dirty": dirty, "buildLocale": "ru-RU",
            "commit": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip(),
            "binaries": inventory}
    releases = ROOT / "dist/libraries"
    releases.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="automation-source-") as temporary:
        tree = Path(temporary) / "source"
        source_tree(tree)
        package.run(["dotnet", "build", "-c", "Release", PROJECT_FILE, "-p:RestoreLockedMode=true"],
                    output / "source-build.log", tree, dict(os.environ, CI="true"))
        if binaries(tree / "bin/Release/net472") != inventory:
            raise ValueError("Independent source build differs from the module build")
        clean = Path(temporary) / "source-export"
        source_tree(clean)
        source_archive = package.archive(clean, releases / f"OneScript.Automation-v{VERSION}-source.zip", info)
        binary = Path(temporary) / "binary"
        for name in inventory:
            package.copy(built / name, binary / name)
        package.copy(PROJECT / "README.md", binary / "README.md")
        package.copy(ROOT / "LICENSE", binary / "LICENSE")
        shutil.copytree(ROOT / "vendor/licenses", binary / "licenses")
        shutil.copytree(PROJECT / "examples", binary / "examples")
        binary_archive = package.archive(binary, releases / f"OneScript.Automation-v{VERSION}-binary.zip", info)
    receipt = dict(info, source=source_archive, binary=binary_archive, status="passed", evidence=str(output))
    (output / "summary.json").write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(receipt))


if __name__ == "__main__":
    main()
