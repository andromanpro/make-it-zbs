"""Run the unchanged OneScript suites from an isolated, checksum-verified deployment."""
import argparse
import importlib.util
import json
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import uuid

ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location("isolated_test_deployment", ROOT / "tools/run-ui-tests.py")
DEPLOY = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(DEPLOY)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--runtime", type=Path, required=True)
    args = parser.parse_args()
    if not (args.runtime / "bin/oscript.exe").is_file():
        raise RuntimeError("Prepared private OneScript runtime is required")
    output = ROOT / "artifacts/onescript-tests" / uuid.uuid4().hex
    output.mkdir(parents=True)
    stage = Path(tempfile.gettempdir()) / "make-it-zbs-os" / output.name
    inventory = {"runtime": DEPLOY.copy_verified(args.runtime, stage / "runtime"),
                 "component": DEPLOY.copy_verified(ROOT / "lib/net472", stage / "lib/net472"),
                 "fixtures": DEPLOY.copy_verified(ROOT / "test/fixtures", stage / "test/fixtures")}
    for relative in ("test", "src/onescript"):
        for source in (ROOT / relative).rglob("*.os"):
            destination = stage / source.relative_to(ROOT)
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(source, destination)
            if source.read_bytes() != destination.read_bytes():
                raise RuntimeError("OneScript source deployment mismatch")
    DEPLOY.baseline_config(stage / "config")
    (output / "deployment-sha256.json").write_text(json.dumps(inventory, indent=2), encoding="utf-8")
    executable = stage / "runtime/bin/oscript.exe"
    result = subprocess.run([str(executable), "-encoding=utf-8", "run-tests.os"], cwd=stage / "test",
                            stdout=subprocess.PIPE, stderr=subprocess.STDOUT, encoding="utf-8", errors="replace", timeout=180)
    (output / "tests.log").write_text(result.stdout, encoding="utf-8")
    print(result.stdout, end="")
    suites = len(re.findall(r"^<<< \[PASS\]", result.stdout, re.MULTILINE))
    assertions = [tuple(map(int, match)) for match in re.findall(r"Итого:\s*(\d+)\s*\|\s*Pass:\s*(\d+)\s*\|\s*Fail:\s*(\d+)", result.stdout)]
    passed = result.returncode == 0 and suites == 4 and len(assertions) == 4 and all(total == ok and failed == 0 for total, ok, failed in assertions)
    summary = {"status": "passed" if passed else "failed", "suites": suites,
               "assertions": sum(total for total, _, _ in assertions), "exit_code": result.returncode, "stage": str(stage)}
    (output / "summary.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")
    print(f"OneScript test report: {output / 'summary.json'}")
    if not passed:
        raise RuntimeError("OneScript suites did not all pass; inspect the saved log")


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    main()
