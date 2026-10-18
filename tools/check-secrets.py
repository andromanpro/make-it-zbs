#!/usr/bin/env py
"""Fail when tracked files contain common credential formats."""

import pathlib
import re
import subprocess
import sys

sys.stdout.reconfigure(encoding="utf-8")
REPO = pathlib.Path(__file__).resolve().parent.parent
ALLOW_MARKER = "secret-scan: allow"
PATTERNS = [
    re.compile(r"sk-or-v1-[A-Za-z0-9_-]{20,}"),
    re.compile(r"\bgh[pousr]_[A-Za-z0-9_-]{20,}"),
    re.compile(r"\bgitea_[a-z0-9_-]{20,}", re.IGNORECASE),
    re.compile(r"-----BEGIN [^-]*PRIVATE KEY-----"),
]


def main():
    files = subprocess.check_output(
        ["git", "ls-files", "--cached", "--others", "--exclude-standard", "-z"],
        cwd=REPO).decode("utf-8").split("\0")
    findings = []
    for relative in files:
        if not relative:
            continue
        path = REPO / relative
        try:
            text = path.read_text(encoding="utf-8")
        except (UnicodeDecodeError, OSError):
            continue
        for number, line in enumerate(text.splitlines(), start=1):
            if ALLOW_MARKER in line:
                continue
            if any(pattern.search(line) for pattern in PATTERNS):
                findings.append(f"{relative}:{number}")
    if findings:
        print("Найдены строки, похожие на секреты:")
        print("\n".join(findings))
        return 1
    print("Secret scan: tracked credentials not found.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
