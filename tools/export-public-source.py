"""Export an explicit, auditable public source snapshot without private Git history."""
import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
import subprocess

ROOT = Path(__file__).resolve().parents[1]
DENIED_PARTS = {".git", "bin", "obj", "node_modules", "logs", "artifacts", "__pycache__"}


def public_path(relative, policy):
    path = PurePosixPath(relative)
    if path.is_absolute() or ".." in path.parts or ":" in relative or "\\" in relative:
        raise ValueError("Unsafe source path")
    if (set(path.parts) & DENIED_PARTS or path.name == ".env"
            or path.suffix in (".pdb", ".pyc", ".lnk")
            or any(p.endswith(".exe.WebView2") or p == "EBWebView" for p in path.parts)):
        return False
    return (relative in policy["files"] or relative in ["tools/" + p for p in policy["tools"]]
            or any(relative.startswith(directory + "/") for directory in policy["directories"]))


def export(root, destination, paths, revision, dirty=False):
    root = root.resolve()
    destination = destination.resolve()
    if not any(destination.is_relative_to(root / area) for area in ("dist", "artifacts")):
        raise ValueError("Public export must stay within dist or artifacts")
    if destination in (root / "dist", root / "artifacts") or destination.exists():
        raise ValueError("Public export destination must be new")
    policy = json.loads((root / "config/public-source.json").read_text(encoding="utf-8"))
    selected = sorted(path for path in paths if public_path(path, policy))
    missing = set(policy["files"]) - set(selected)
    if missing:
        raise ValueError("Required public sources missing: " + ", ".join(sorted(missing)))
    contents = {}
    for relative in selected:
        path = root / relative
        if path.is_symlink() or not path.resolve().is_relative_to(root) or not path.is_file():
            raise ValueError("Public source contains an invalid filesystem entry")
        contents[relative] = path.read_bytes()
    inventory = {name: hashlib.sha256(data).hexdigest() for name, data in contents.items()}
    destination.mkdir(parents=True)
    for relative, data in contents.items():
        target = destination / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(data)
    receipt = {"schemaVersion": 1, "sourceCommit": revision, "dirty": dirty,
               "historyIncluded": False, "files": inventory}
    (destination / "PUBLIC-SOURCE-MANIFEST.json").write_text(
        json.dumps(receipt, ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")
    return receipt


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("destination", type=Path)
    parser.add_argument("--allow-dirty", action="store_true", help="Inspection only; not a public release snapshot")
    args = parser.parse_args()
    dirty = bool(subprocess.check_output(["git", "status", "--porcelain"], cwd=ROOT))
    if dirty and not args.allow_dirty:
        parser.error("Public source requires a clean committed version")
    paths = subprocess.check_output(["git", "ls-files", "--cached", "--others", "--exclude-standard", "-z"],
                                    cwd=ROOT).decode("utf-8").strip("\0").split("\0")
    revision = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip()
    receipt = export(ROOT, args.destination.resolve(), paths, revision, dirty)
    print(json.dumps({"destination": str(args.destination), "files": len(receipt["files"]),
                      "historyIncluded": False, "dirty": dirty}))
