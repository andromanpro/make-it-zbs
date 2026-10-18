#!/usr/bin/env py
"""Build a deterministic portable ZIP and SHA-256 manifests."""

import argparse
import datetime as dt
import hashlib
import json
import os
import pathlib
import re
import subprocess
import sys
import zipfile

sys.stdout.reconfigure(encoding="utf-8")

REPO = pathlib.Path(__file__).resolve().parent.parent
DIST = REPO / "dist" / "MakeItZBS"
RELEASES = REPO / "dist" / "releases"
SIGNED_RELEASE_ARTIFACTS = {"lib/net472/ZBSWindowsComponent.dll"}


def run(*args):
    return subprocess.check_output(args, cwd=REPO, text=True).strip()


def sha256(path):
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for chunk in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def source_epoch():
    configured = os.environ.get("SOURCE_DATE_EPOCH", "").strip()
    if configured:
        return int(configured)
    return int(run("git", "show", "-s", "--format=%ct", "HEAD"))


def zip_timestamp(epoch):
    value = dt.datetime.fromtimestamp(epoch, tz=dt.timezone.utc)
    if value.year < 1980:
        value = value.replace(year=1980, month=1, day=1, hour=0, minute=0, second=0)
    return value.year, value.month, value.day, value.hour, value.minute, value.second


def write_build_info(epoch, version=None):
    info = {
        "commit": run("git", "rev-parse", "HEAD"),
        "sourceDateEpoch": epoch,
        "schemaVersion": 1,
    }
    if version is not None:
        info.update(version=version, dirty=bool(run("git", "status", "--porcelain")))
    (DIST / "BUILD-INFO.json").write_text(
        json.dumps(info, ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")


def write_manifest():
    manifest_path = DIST / "MANIFEST.sha256"
    lines = []
    for path in sorted(item for item in DIST.rglob("*") if item.is_file() and item != manifest_path):
        relative = path.relative_to(DIST).as_posix()
        lines.append(f"{sha256(path)}  {relative}")
    manifest_path.write_text("\n".join(lines) + "\n", encoding="utf-8", newline="\n")
    return manifest_path


def create_zip(version, epoch):
    RELEASES.mkdir(parents=True, exist_ok=True)
    archive = RELEASES / f"MakeItZBS-{version}-windows-x64.zip"
    timestamp = zip_timestamp(epoch)
    with zipfile.ZipFile(archive, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as target:
        for path in sorted(item for item in DIST.rglob("*") if item.is_file()):
            relative = pathlib.PurePosixPath("MakeItZBS") / path.relative_to(DIST).as_posix()
            entry = zipfile.ZipInfo(str(relative), timestamp)
            entry.compress_type = zipfile.ZIP_DEFLATED
            entry.external_attr = 0o100644 << 16
            target.writestr(entry, path.read_bytes(), compress_type=zipfile.ZIP_DEFLATED, compresslevel=9)
    checksum = archive.with_suffix(archive.suffix + ".sha256")
    checksum.write_text(f"{sha256(archive)}  {archive.name}\n", encoding="ascii", newline="\n")
    return archive, checksum


def verify_signatures():
    script = REPO / "tools" / "verify-signatures.ps1"
    subprocess.run([
        "powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(script),
        str(DIST / "MakeItZBS.exe"), str(DIST / "lib" / "net472" / "ZBSWindowsComponent.dll")
    ], cwd=REPO, check=True)


def remove_nondeterministic_shortcut():
    # WScript.Shell embeds creation metadata in .lnk, so identical builds get
    # different bytes. The signed launcher remains the release entry point.
    shortcut = DIST / "Make it ZBS.lnk"
    if shortcut.exists():
        shortcut.unlink()


def ensure_source_tree_is_clean(require_signed, allow_dirty):
    if allow_dirty:
        return

    dirty_lines = [line for line in run("git", "status", "--porcelain").splitlines() if line]
    if require_signed:
        dirty_lines = [
            line for line in dirty_lines
            if line[3:].replace("\\", "/") not in SIGNED_RELEASE_ARTIFACTS
        ]
    if dirty_lines:
        raise SystemExit(
            "Рабочее дерево изменено: воспроизводимый release требует чистые исходники.\n"
            + "\n".join(dirty_lines)
        )


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--version", default="dev")
    parser.add_argument("--require-signed", action="store_true")
    parser.add_argument("--allow-dirty", action="store_true")
    args = parser.parse_args()

    version = re.sub(r"[^A-Za-z0-9._-]+", "-", args.version).strip("-") or "dev"
    ensure_source_tree_is_clean(args.require_signed, args.allow_dirty)

    subprocess.run([sys.executable, str(REPO / "tools" / "build-dist.py")], cwd=REPO, check=True)
    remove_nondeterministic_shortcut()
    if args.require_signed:
        verify_signatures()

    epoch = source_epoch()
    write_build_info(epoch, version)
    manifest = write_manifest()
    archive, checksum = create_zip(version, epoch)
    print("Manifest:", manifest)
    print("Release:", archive)
    print("SHA-256:", checksum.read_text(encoding="ascii").strip())


if __name__ == "__main__":
    main()
