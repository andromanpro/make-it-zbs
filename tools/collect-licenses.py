"""Collect runtime-package notices from resolved NuGet metadata, never from OVM."""
import argparse
import hashlib
import json
from pathlib import Path
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]


def package_notice(directory, name, root):
    metadata = ET.parse(next(directory.glob("*.nuspec"))).getroot().find("{*}metadata")
    license_node = metadata.find("{*}license")
    expression = license_node.text if license_node is not None else "License file"
    notices = {p.name: p.read_bytes() for p in directory.iterdir()
               if p.is_file() and any(word in p.name.lower() for word in ("license", "notice"))}
    if not notices:
        if expression == "MPL-2.0":
            notices["LICENSE.txt"] = (root / "vendor/onescript/LICENSE.txt").read_bytes()
        elif expression == "MIT":
            notices["LICENSE.txt"] = (root / "vendor/licenses/MIT.txt").read_bytes()
            if name.startswith(("Silk.NET.", "Ultz.")):
                notices["ATTRIBUTION.txt"] = (root / "src/CaptureHost/THIRD-PARTY-CAPTURE.txt").read_bytes()
        else:
            raise ValueError(f"License text is missing: {name}")
    return {"license": expression,
            "copyright": metadata.findtext("{*}copyright", ""),
            "authors": metadata.findtext("{*}authors", ""),
            "licenseUrl": metadata.findtext("{*}licenseUrl", "")}, notices


def store(root, relative, content, check):
    destination = root / "vendor/licenses" / relative
    if check:
        if not destination.is_file() or destination.read_bytes() != content:
            raise ValueError(f"License inventory is stale: {relative}")
    else:
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_bytes(content)


def collect(root, check=False):
    assets = json.loads((root / "src/ZBSWindowsComponent/obj/project.assets.json").read_text(encoding="utf-8"))
    shipped = {p.name.lower() for p in (root / "lib/net472").rglob("*.dll")}
    index, covered = [], set()
    for identity, package in sorted(assets["libraries"].items()):
        if package["type"] != "package":
            continue
        runtime = {Path(path).name.lower() for target in assets["targets"].values()
                   for path in target.get(identity, {}).get("runtime", {})}
        if identity.startswith("Microsoft.Web.WebView2/"):
            runtime.add("webview2loader.dll")
        binaries = sorted(runtime & shipped)
        if not binaries:
            continue
        directory = next((Path(folder) / package["path"] for folder in assets["packageFolders"]
                          if (Path(folder) / package["path"]).is_dir()), None)
        if directory is None:
            raise ValueError(f"Package not restored: {identity}")
        name, version = identity.split("/", 1)
        entry, notices = package_notice(directory, name, root)
        entry.update(name=name, version=version, binaries=binaries, notices={})
        for filename, content in sorted(notices.items()):
            relative = f"{name}/{version}/{filename}"
            store(root, relative, content, check)
            entry["notices"][relative] = hashlib.sha256(content).hexdigest()
        index.append(entry)
        covered.update(binaries)
    unknown = shipped - covered - {"zbswindowscomponent.dll"}
    if unknown:
        raise ValueError("Unmapped shipped DLLs: " + ", ".join(sorted(unknown)))
    content = json.dumps({"schemaVersion": 1, "packages": index}, ensure_ascii=False, indent=2) + "\n"
    store(root, "index.json", content.encode("utf-8"), check)
    return len(index), len(covered)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()
    print("Runtime package licenses / DLLs:", collect(ROOT, args.check))
