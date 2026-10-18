"""Build independent binary/source archives; interactive smoke is explicit opt-in."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import uuid
import zipfile

PROJECT = Path(__file__).resolve().parent
REPO = PROJECT.parents[1]
VERSION = "0.1.0-beta.1"
CORE = ("ClipboardDispatcher.cs", "NativeClipboardSnapshot.cs", "NativeMethods.cs")
PROJECT_FILENAME = "OneScript.Windows.csproj"
README_FILENAME = "README.md"
BINARY_RELATIVE = "bin/Release/net472/OneScript.Windows.dll"


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def copy(source, target):
    target.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(source, target)
    if sha(source) != sha(target):
        raise ValueError("Copy checksum mismatch")


def run(command, log, cwd, env=None):
    env = dict(os.environ if env is None else env, DOTNET_CLI_UI_LANGUAGE="ru-RU")
    with log.open("w", encoding="utf-8") as output:
        result = subprocess.run(command, cwd=cwd, env=env, stdout=output, stderr=subprocess.STDOUT, timeout=300)
    if result.returncode:
        raise RuntimeError(f"Command failed ({result.returncode}); see {log}")


def source_tree(destination):
    for name in (PROJECT_FILENAME, "packages.lock.json", README_FILENAME):
        copy(PROJECT/name, destination/name)
    for path in PROJECT.glob("Windows*.cs"):
        copy(path, destination/path.name)
    for directory in ("examples", "tests"):
        for path in (PROJECT/directory).rglob("*.os"):
            copy(path, destination/directory/path.relative_to(PROJECT/directory))
    for name in CORE:
        copy(REPO/"src/ZBSWindowsComponent"/name, destination/"core"/name)
    for name in ("LICENSE", "global.json", "Directory.Build.props", "NuGet.Config"):
        copy(REPO/name, destination/name)
    for name in ("MakeItZBS.OneScript.1.9.4-zbs.1.nupkg", "MakeItZBS.OneScript.Language.1.9.4-zbs.1.nupkg"):
        copy(REPO/"vendor/nuget"/name, destination/"vendor/nuget"/name)
    for name in ("source.zip", "LICENSE.txt", "ProDotNetZip-LICENSE.txt", "security.patch", "manifest.json"):
        copy(REPO/"vendor/onescript"/name, destination/"vendor/onescript"/name)


def archive(source, destination, info):
    files = {p.relative_to(source).as_posix(): sha(p) for p in sorted(source.rglob("*")) if p.is_file()}
    (source/"MANIFEST.json").write_text(json.dumps(dict(info, files=files), indent=2)+"\n", encoding="utf-8")
    with zipfile.ZipFile(destination, "w") as zipped:
        for path in sorted(source.rglob("*")):
            if path.is_file():
                item = zipfile.ZipInfo(path.relative_to(source).as_posix(), (1980, 1, 1, 0, 0, 0))
                item.external_attr = 0o100644 << 16
                zipped.writestr(item, path.read_bytes(), compress_type=zipfile.ZIP_DEFLATED, compresslevel=9)
    destination.with_suffix(".zip.sha256").write_text(sha(destination)+"  "+destination.name+"\n", encoding="ascii")
    return {"name": destination.name, "sha256": sha(destination), "bytes": destination.stat().st_size,
            "files": len(files)}


def smoke(binary, output):
    runtime = REPO/"dist/MakeItZBS/runtime/bin/oscript.exe"
    if not runtime.is_file():
        raise ValueError("Build the verified portable runtime first; the global OneScript is not used")
    with tempfile.TemporaryDirectory(prefix="onescript-windows-smoke-") as temporary:
        stage = Path(temporary)
        copy(binary, stage/binary.name)
        copy(PROJECT/"tests/smoke.os", stage/"smoke.os")
        env = os.environ.copy()
        env.update(OSCRIPT_PATH="", OSLIB="", OSCRIPT_CONFIG="", HOME=str(stage), USERPROFILE=str(stage))
        log = output/"smoke.log"
        run([str(runtime), "-encoding=utf-8", str(stage/"smoke.os"), str(stage/binary.name)], log, stage, env)
        text = log.read_text(encoding="utf-8")
        if "WINDOWS-LIBRARY-SMOKE-PASS assertions=" not in text:
            raise ValueError("Incomplete Windows library smoke")
        return text.strip()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--test", action="store_true", help="Temporarily use the clipboard; requires an idle interactive desktop")
    parser.add_argument("--allow-dirty", action="store_true", help="Inspection only; archives record dirty=true")
    args = parser.parse_args()
    dirty = bool(subprocess.check_output(["git", "status", "--porcelain"], cwd=REPO))
    if dirty and not args.allow_dirty:
        raise ValueError("Library release requires a clean source tree")
    output = REPO/"artifacts/windows-library"/uuid.uuid4().hex
    output.mkdir(parents=True)
    run(["dotnet", "build", "-c", "Release", str(PROJECT/PROJECT_FILENAME), "-p:RestoreLockedMode=true"],
        output/"build.log", REPO)
    binary = PROJECT/BINARY_RELATIVE
    result = {"version": VERSION, "dirty": dirty, "core": {name: sha(REPO/"src/ZBSWindowsComponent"/name) for name in CORE},
              "commit": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=REPO, text=True).strip(),
              "binarySha256": sha(binary), "desktopSmoke": "not requested"}
    build_info = {key: value for key, value in result.items() if key != "desktopSmoke"}
    if args.test:
        result["desktopSmoke"] = smoke(binary, output)
    releases = REPO/"dist/libraries"
    releases.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="onescript-windows-source-") as temporary:
        source = Path(temporary)/"source"
        source_tree(source)
        env = os.environ.copy()
        env["CI"] = "true"
        run(["dotnet", "build", "-c", "Release", PROJECT_FILENAME, "-p:RestoreLockedMode=true"],
            output/"source-build.log", source, env)
        if sha(source/BINARY_RELATIVE) != result["binarySha256"]:
            for relative in (BINARY_RELATIVE, "bin/Release/net472/OneScript.Windows.pdb",
                             "obj/Release/net472/OneScript.Windows.AssemblyInfo.cs"):
                copy(source/relative, output/"different-source-build"/Path(relative).name)
            raise ValueError("Independent source build differs")
        # Build products are not source inputs and never enter the source archive.
        clean_source = Path(temporary)/"export"
        source_tree(clean_source)
        result["source"] = archive(clean_source, releases/f"OneScript.Windows-v{VERSION}-source.zip", build_info)
        package = Path(temporary)/"binary"
        copy(binary, package/binary.name)
        copy(PROJECT/README_FILENAME, package/README_FILENAME)
        copy(REPO/"LICENSE", package/"LICENSE")
        for directory in ("examples", "tests"):
            for path in (PROJECT/directory).rglob("*.os"):
                copy(path, package/directory/path.relative_to(PROJECT/directory))
        result["binary"] = archive(package, releases/f"OneScript.Windows-v{VERSION}-binary.zip", build_info)
    result["status"] = "passed"
    (output/"summary.json").write_text(json.dumps(result, indent=2)+"\n", encoding="utf-8")
    print(json.dumps(dict(result, evidence=str(output)), ensure_ascii=True))


if __name__ == "__main__":
    main()
