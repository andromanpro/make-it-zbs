"""Build the private OneScript 1.9.4 runtime with the fixed ZIP dependency."""
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parents[1]
REVISION = "04d903852c31337391ef47aee821aa8769725170"
SOURCE = ROOT / "artifacts/vendor/onescript"
VENDOR = ROOT / "vendor/onescript"
PROJECT_SUFFIX = ".csproj"
PROJECTS = {
    "OneScript.Language": "MakeItZBS.OneScript.Language",
    "ScriptEngine": "MakeItZBS.OneScript",
    "ScriptEngine.HostedScript": "MakeItZBS.OneScript.StandardLibrary",
}
PROPERTIES = ["/p:TargetFrameworkId=net472", "/p:ReleaseNumber=1.9.4", "/p:Suffix=-zbs.1",
              "/p:Platform=AnyCPU", "/p:ContinuousIntegrationBuild=true", "/p:Deterministic=true"]


def command(*args, cwd=ROOT):
    subprocess.run(list(args), cwd=cwd, check=True)


def prepare_projects(source):
    for name, package in PROJECTS.items():
        prepare_library_project(source / "src" / name / (name + PROJECT_SUFFIX), package)

    runner = source / "src/StandaloneRunner/StandaloneRunner.csproj"
    tree = ET.parse(runner)
    for group in tree.findall("ItemGroup"):
        resources = group.findall("EmbeddedResource")
        if resources:
            for item in resources:
                group.remove(item)
            ET.SubElement(group, "EmbeddedResource", Include="*.dll")
    tree.write(runner, encoding="utf-8", xml_declaration=True)

    host = source / "src/oscript/oscript.csproj"
    tree = ET.parse(host)
    for item in tree.findall(".//PackageReference"):
        if item.get("Include") == "Newtonsoft.Json":
            item.set("Version", "13.0.3")
    tree.write(host, encoding="utf-8", xml_declaration=True)


def prepare_library_project(path, package):
    tree = ET.parse(path)
    tree.find(".//PackageId").text = package
    for item in tree.findall(".//PackageReference"):
        if item.get("Include") in ("DotNetZip", "ProDotNetZip"):
            item.set("Include", "ProDotNetZip")
            item.set("Version", "1.20.0")
        if item.get("Include") == "Newtonsoft.Json":
            item.set("Version", "13.0.3")
    for item in tree.findall(".//CopyToStandalone"):
        if "DotNetZip.dll" in item.get("Include", ""):
            item.set("Include", "$(TargetDir)*.dll")
    tree.write(path, encoding="utf-8", xml_declaration=True)


def write_archive(path, files):
    path.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(path, "w", compression=zipfile.ZIP_DEFLATED) as archive:
        for name, content in sorted(files.items()):
            info = zipfile.ZipInfo(name, (2026, 9, 7, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            archive.writestr(info, content)


def main():
    if not SOURCE.exists():
        command("git", "clone", "--depth", "1", "--branch", "v1.9.4",
                "https://github.com/EvilBeaver/OneScript.git", str(SOURCE))
    actual = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=SOURCE, text=True).strip()
    if actual != REVISION:
        raise RuntimeError("Unexpected OneScript source revision; refusing to build.")
    prepare_projects(SOURCE)
    patch = VENDOR / "security.patch"
    applied = subprocess.run(["git", "apply", "--reverse", "--check", str(patch)],
                             cwd=SOURCE, capture_output=True, check=False)
    if applied.returncode != 0:
        command("git", "apply", str(patch), cwd=SOURCE)
    packages = ROOT / "vendor/nuget"
    packages.mkdir(parents=True, exist_ok=True)
    for name in PROJECTS:
        command("dotnet", "pack", str(SOURCE / "src" / name / (name + PROJECT_SUFFIX)),
                "-c", "Release", "-o", str(packages), "/p:TargetFrameworks=net472", *PROPERTIES)
    for name in ("StandaloneRunner", "oscript"):
        command("dotnet", "build", str(SOURCE / "src" / name / (name + PROJECT_SUFFIX)),
                "-c", "Release", "/p:SolutionDir=" + str(SOURCE / "src") + "/", *PROPERTIES)

    output = SOURCE / "src/oscript/bin/Release/net472"
    files = {"bin/" + file.name: file.read_bytes() for file in output.iterdir()
             if file.suffix in (".dll", ".exe", ".config", ".cfg")}
    files["bin/oscript.exe.config"] = (ROOT / "src/runtime/oscript.exe.config").read_bytes()
    if "bin/DotNetZip.dll" in files or "bin/ProDotNetZip.dll" not in files:
        raise RuntimeError("Unexpected ZIP implementation in runtime output.")
    runtime = VENDOR / "runtime.zip"
    write_archive(runtime, files)
    names = subprocess.check_output(["git", "ls-files", "-z"], cwd=SOURCE).decode("utf-8").split("\0")
    write_archive(VENDOR / "source.zip", {name: (SOURCE / name).read_bytes()
                  for name in names if name and (SOURCE / name).is_file()})
    shutil.copyfile(SOURCE / "LICENSE", VENDOR / "LICENSE.txt")
    manifest = {
        "upstream": "https://github.com/EvilBeaver/OneScript", "revision": REVISION,
        "version": "1.9.4-zbs.1", "framework": "net472", "zipDependency": "ProDotNetZip 1.20.0",
        "sha256": hashlib.sha256(runtime.read_bytes()).hexdigest(),
        "files": {name: hashlib.sha256(content).hexdigest() for name, content in sorted(files.items())},
    }
    (VENDOR / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    print("Private OneScript runtime built:", manifest["sha256"])


if __name__ == "__main__":
    main()
