"""Run the independent module from an ordinary isolated deployment; desktop is opt-in."""
import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import uuid
import xml.etree.ElementTree as ET

PROJECT = Path(__file__).resolve().parent
ROOT = PROJECT.parents[1]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--desktop", action="store_true")
    parser.add_argument("--coverage", action="store_true")
    parser.add_argument("--runtime", type=Path)
    args = parser.parse_args()
    output = ROOT / "artifacts/automation-tests" / uuid.uuid4().hex
    output.mkdir(parents=True)
    environment = dict(os.environ, DOTNET_CLI_UI_LANGUAGE="ru-RU")
    project = PROJECT / "tests/OneScript.Automation.Tests.csproj"
    with (output / "run.log").open("w", encoding="utf-8") as log:
        subprocess.run(["dotnet", "build", "-c", "Release", str(project)], cwd=ROOT,
                       env=environment, stdout=log, stderr=subprocess.STDOUT, check=True, timeout=300)
        with tempfile.TemporaryDirectory(prefix="onescript-automation-tests-") as temporary:
            stage = Path(temporary) / "tests"
            shutil.copytree(PROJECT / "tests/bin/Release/net472", stage)
            command = ["dotnet", "test", "-c", "Release", str(project), "--no-build", "--no-restore",
                       "/p:OutDir=" + str(stage) + "/", "--logger", "trx;LogFileName=results.trx",
                       "--results-directory", str(output), "--filter",
                       "TestCategory=Desktop" if args.desktop else "TestCategory!=Desktop"]
            if args.coverage:
                coverage = ROOT / "artifacts/coverage/automation/coverage"
                coverage.parent.mkdir(parents=True, exist_ok=True)
                command += ["/p:CollectCoverage=true", "/p:CoverletOutput=" + str(coverage),
                            "/p:CoverletOutputFormat=opencover%2Cjson"]
                if args.desktop and coverage.with_suffix(".json").is_file():
                    command += ["/p:MergeWith=" + str(coverage.with_suffix(".json"))]
            result = subprocess.run(command, cwd=ROOT, env=environment, stdout=log,
                                    stderr=subprocess.STDOUT, timeout=180)
            if args.runtime and not args.desktop:
                shutil.copyfile(PROJECT / "tests/smoke.os", stage / "smoke.os")
                smoke_environment = dict(environment, OSCRIPT_PATH="", OSLIB="", OSCRIPT_CONFIG="",
                                         HOME=str(stage), USERPROFILE=str(stage))
                smoke = subprocess.run([str(args.runtime / "bin/oscript.exe"), "-encoding=utf-8", str(stage / "smoke.os"),
                                        str(stage / "OneScript.Automation.dll")], env=smoke_environment, cwd=stage,
                                       capture_output=True, encoding="utf-8", errors="replace", timeout=30)
                (output / "smoke.log").write_text(smoke.stdout + smoke.stderr, encoding="utf-8")
                if smoke.returncode or "AUTOMATION-SMOKE-PASS assertions=4" not in smoke.stdout:
                    raise RuntimeError("Independent OneScript loader smoke failed: " + str(output / "smoke.log"))
    report = output / "results.trx"
    counters = ET.parse(report).getroot().find("{*}ResultSummary/{*}Counters")
    total = int(counters.get("total", "0"))
    passed = int(counters.get("passed", "0"))
    receipt = {"total": total, "passed": passed, "desktop": args.desktop,
               "status": "passed" if result.returncode == 0 and total >= (3 if args.desktop else 30) and passed == total else "failed"}
    (output / "summary.json").write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(dict(receipt, evidence=str(output))))
    if receipt["status"] != "passed":
        raise RuntimeError("Incomplete independent module tests: " + str(output / "run.log"))


if __name__ == "__main__":
    main()
