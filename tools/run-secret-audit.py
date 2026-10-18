"""Scan only an exported public snapshot with a checksum-pinned Gitleaks binary."""
import argparse
import hashlib
import io
import json
from pathlib import Path
import subprocess
import urllib.request
import zipfile

ROOT = Path(__file__).resolve().parents[1]
VERSION = "8.30.1"
ARCHIVE_SHA256 = "d29144deff3a68aa93ced33dddf84b7fdc26070add4aa0f4513094c8332afc4e"


def executable(cache):
    cache.mkdir(parents=True, exist_ok=True)
    archive = cache / "gitleaks.zip"
    if archive.exists():
        content = archive.read_bytes()
    else:
        url = f"https://github.com/gitleaks/gitleaks/releases/download/v{VERSION}/gitleaks_{VERSION}_windows_x64.zip"
        with urllib.request.urlopen(url, timeout=120) as response:
            content = response.read()
    if hashlib.sha256(content).hexdigest() != ARCHIVE_SHA256:
        raise ValueError("Gitleaks download checksum mismatch")
    with zipfile.ZipFile(io.BytesIO(content)) as package:
        binary = package.read("gitleaks.exe")
    archive.write_bytes(content)
    target = cache / "gitleaks.exe"
    target.write_bytes(binary)
    return target


def scan(source, binary, output):
    if not (source / "PUBLIC-SOURCE-MANIFEST.json").is_file():
        raise ValueError("Secret audit requires a public export, never an installed or personal folder")
    output.mkdir(parents=True, exist_ok=True)
    report = output / "gitleaks-redacted.json"
    command = [str(binary), "dir", str(source), "--config", str(source / ".gitleaks.toml"),
               "--redact=100", "--no-banner", "--no-color", "--max-archive-depth=3",
               "--timeout=300", "--report-format=json", "--report-path=" + str(report)]
    result = subprocess.run(command, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=330)
    log = result.stdout + result.stderr
    (output / "gitleaks.log").write_text(log, encoding="utf-8")
    findings = json.loads(report.read_text(encoding="utf-8")) if report.is_file() else None
    if result.returncode or " ERR " in log or findings != []:
        raise ValueError("Secret audit did not pass; review its redacted report and log")
    receipt = {"status": "passed", "gitleaksVersion": VERSION, "archiveDepth": 3,
               "sourceManifestSha256": hashlib.sha256((source / "PUBLIC-SOURCE-MANIFEST.json").read_bytes()).hexdigest(),
               "findings": 0}
    (output / "summary.json").write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8")
    return receipt


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path)
    parser.add_argument("--output", type=Path, default=ROOT / "artifacts/public-secret-audit")
    args = parser.parse_args()
    print(json.dumps(scan(args.source.resolve(), executable(ROOT / "artifacts/tools/gitleaks"), args.output.resolve())))
