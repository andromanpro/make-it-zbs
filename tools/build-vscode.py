"""Build the local VS Code adapter without a registry upload or npm lifecycle scripts."""
import json
from pathlib import Path
import sys
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parents[1]
VSIX_NS = "http://schemas.microsoft.com/developer/vsx-schema/2011"


def write_entry(archive, name, content):
    entry = zipfile.ZipInfo(name, (2026, 10, 12, 0, 0, 0))
    entry.compress_type = zipfile.ZIP_DEFLATED
    archive.writestr(entry, content)


def build(destination):
    source = ROOT / "integrations" / "vscode"
    package = json.loads((source / "package.json").read_text(encoding="utf-8"))
    manifest = ET.Element("PackageManifest", Version="2.0.0", xmlns=VSIX_NS)
    metadata = ET.SubElement(manifest, "Metadata")
    ET.SubElement(metadata, "Identity", Language="en-US", Id=package["name"],
                  Version=package["version"], Publisher=package["publisher"])
    ET.SubElement(metadata, "DisplayName").text = package["displayName"]
    ET.SubElement(metadata, "Description", {"xml:space": "preserve"}).text = package["description"]
    ET.SubElement(metadata, "License").text = "extension/LICENSE.txt"
    properties = ET.SubElement(metadata, "Properties")
    ET.SubElement(properties, "Property", Id="Microsoft.VisualStudio.Code.Engine", Value=package["engines"]["vscode"])
    ET.SubElement(properties, "Property", Id="Microsoft.VisualStudio.Code.ExtensionKind", Value="ui")
    targets = ET.SubElement(manifest, "Installation")
    ET.SubElement(targets, "InstallationTarget", Id="Microsoft.VisualStudio.Code")
    ET.SubElement(manifest, "Dependencies")
    assets = ET.SubElement(manifest, "Assets")
    ET.SubElement(assets, "Asset", Type="Microsoft.VisualStudio.Code.Manifest",
                  Path="extension/package.json", Addressable="true")
    content = ET.Element("Types", xmlns="http://schemas.openxmlformats.org/package/2006/content-types")
    for suffix, kind in (("json", "application/json"), ("js", "application/javascript"),
                         ("md", "text/markdown"), ("txt", "text/plain"), ("vsixmanifest", "text/xml")):
        ET.SubElement(content, "Default", Extension=suffix, ContentType=kind)
    destination = Path(destination)
    destination.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(destination, "w", compression=zipfile.ZIP_DEFLATED) as archive:
        write_entry(archive, "extension.vsixmanifest", ET.tostring(manifest, encoding="utf-8", xml_declaration=True))
        write_entry(archive, "[Content_Types].xml", ET.tostring(content, encoding="utf-8", xml_declaration=True))
        for name in ("package.json", "extension.js", "editor-sessions.js", "README.md", "LICENSE.txt"):
            write_entry(archive, "extension/" + name, (source / name).read_text(encoding="utf-8").encode("utf-8"))
    return destination


if __name__ == "__main__":
    print(build(ROOT / "artifacts" / "adapters" / "make-it-zbs-editor.vsix"))
