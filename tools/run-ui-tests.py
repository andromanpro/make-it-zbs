"""Build and run isolated Windows UI tests; never use personal config or documents."""
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
import xml.etree.ElementTree as ET

REPO = Path(__file__).resolve().parents[1]
UNIT_FILTER = "FullyQualifiedName!~MakeItZBS.UiTests&TestCategory!=Desktop"


def run(command, log, env=None, timeout=300):
    with log.open("w", encoding="utf-8") as stream:
        with subprocess.Popen(command, cwd=REPO, env=env, stdout=stream, stderr=subprocess.STDOUT) as process:
            try:
                code = process.wait(timeout=timeout)
            except subprocess.TimeoutExpired:
                # Only the process tree created for this exact invocation is owned.
                try:
                    if os.name == "nt" and process.poll() is None:
                        subprocess.run(["taskkill", "/PID", str(process.pid), "/T", "/F"],
                                       stdout=stream, stderr=subprocess.STDOUT, check=False, timeout=30)
                finally:
                    process.kill()
                    process.wait(timeout=30)
                raise RuntimeError(f"Command timed out; see {log}") from None
    print(log.read_text(encoding="utf-8", errors="replace"), end="")
    if code:
        raise RuntimeError(f"Command failed ({code}); see {log}")


def copy_verified(source, target):
    """Deploy ordinary build artifacts, without modifying source ACLs or Windows policy."""
    target.mkdir(parents=True, exist_ok=True)
    inventory = {}
    for item in source.rglob("*"):
        if not item.is_file():
            continue
        relative = item.relative_to(source)
        destination = target / relative
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(item, destination)
        digest = hashlib.sha256(item.read_bytes()).hexdigest()
        if hashlib.sha256(destination.read_bytes()).hexdigest() != digest:
            raise RuntimeError(f"Deployment checksum mismatch: {relative}")
        inventory[relative.as_posix()] = digest
    return inventory


def baseline_config(target):
    target.mkdir(parents=True, exist_ok=True)
    for name in ("settings.json", "models.json", "prompts.json", "model-catalog.json"):
        result = subprocess.run(["git", "show", f"HEAD:config/{name}"], cwd=REPO,
                                check=True, stdout=subprocess.PIPE)
        content = json.loads(result.stdout.decode("utf-8-sig"))
        if name == "settings.json":
            content.setdefault("general", {})["language"] = "ru"
        (target / name).write_text(json.dumps(content, ensure_ascii=False, indent=2), encoding="utf-8")
    (target / ".env").write_text("OPENROUTER_API_KEY=ui-test-placeholder\n", encoding="ascii")


def installed_artifacts(root, stage):
    """Exercise installed binaries in a sandbox, without opening personal config."""
    component = root / "lib/net472"
    if not (component / "MakeItZBS.CaptureHost.exe").is_file() or not (root / "ui/settings.html").is_file():
        raise RuntimeError("Incomplete installed application")
    expected = hashlib.sha256((component / "ZBSWindowsComponent.dll").read_bytes()).digest()
    inventory = {}
    for name in ("unit", "host"):
        destination = stage / name
        if not destination.is_dir():
            continue
        built = destination / "ZBSWindowsComponent.dll"
        if hashlib.sha256(built.read_bytes()).digest() != expected:
            raise RuntimeError("Installed component differs from the component used to build the tests")
        inventory[name] = copy_verified(component, destination)
    inventory["ui"] = copy_verified(root / "ui", stage / "ui")
    return inventory


def check_result(path, minimum):
    root = ET.parse(path).getroot()
    counters = root.find("{*}ResultSummary/{*}Counters")
    if counters is None:
        raise RuntimeError("Missing test counters")
    total = int(counters.get("total", "0"))
    passed = int(counters.get("passed", "0"))
    failed = int(counters.get("failed", "0"))
    if total < minimum or passed != total or failed:
        raise RuntimeError(f"Incomplete test run: total={total}, passed={passed}, failed={failed}")
    return {"total": total, "passed": passed, "failed": failed}


def execute_tests(stage, output, env, name, test_filter, coverage, merge=False, minimum=1, timeout=300):
    if coverage:
        coverage_path = REPO / "artifacts/coverage/csharp/coverage"
        coverage_path.parent.mkdir(parents=True, exist_ok=True)
        command = ["dotnet", "test", "-c", "Release", "test/csharp/ZBSWindowsComponent.Tests.csproj", "--no-build",
                   f"/p:OutDir={stage / 'unit'}/", "/p:CollectCoverage=true", f"/p:CoverletOutput={coverage_path}",
                   "/p:CoverletOutputFormat=opencover%2Cjson", "--logger", f"trx;LogFileName={name}.trx",
                   "--results-directory", str(output), "--filter", test_filter]
        if merge:
            command += [f"/p:MergeWith={coverage_path}.json"]
    else:
        command = ["dotnet", "vstest", str(stage / "unit/ZBSWindowsComponent.Tests.dll"),
                   f"/TestAdapterPath:{stage / 'unit'}", f"/ResultsDirectory:{output}",
                   f"/Logger:trx;LogFileName={name}.trx", f"/TestCaseFilter:{test_filter}"]
    run(command, output / f"{name}.log", env, timeout=timeout)
    return check_result(output / f"{name}.trx", minimum)


def prepare_pipeline(package, destination):
    required = ["runtime/bin/oscript.exe", "src/onescript/zbs-run.os", "lib/net472/ZBSWindowsComponent.dll"]
    if any(not (package / name).is_file() for name in required):
        raise RuntimeError("Build the complete distribution before the Word pipeline check")
    inventory = copy_verified(package, destination)
    baseline_config(destination / "config")
    settings_path = destination / "config/settings.json"
    settings = json.loads(settings_path.read_text(encoding="utf-8"))
    settings["general"].update(hotkey="Ctrl+Shift+F24", background=True, codexOfferShown=True, operationFeedback="both")
    for profile in settings.get("aiChat", {}).get("profiles", {}).values():
        profile["enabled"] = False
    settings["preview"] = {"showPreview": True, "stream": False, "timeout": 180}
    settings["replace"] = {"enabled": True, "contexts": ["Word", "Text", "Code", "OneCCode"]}
    settings["logging"] = {"enabled": True, "diagnosticsOnly": True, "logRequests": False, "logResponses": False, "maxLogFiles": 5}
    settings_path.write_text(json.dumps(settings, ensure_ascii=False, indent=2), encoding="utf-8")
    models_path = destination / "config/models.json"
    models = json.loads(models_path.read_text(encoding="utf-8"))
    for context in ("Word", "Text", "Code", "Image", "OneCCode"):
        models.setdefault("contexts", {}).setdefault(context, {}).update(provider="codex", codexModel="", codexReasoningEffort="low")
    models_path.write_text(json.dumps(models, ensure_ascii=False, indent=2), encoding="utf-8")
    return inventory


def prepare_stage(args, stage, output):
    projects = [("unit", "test/csharp/ZBSWindowsComponent.Tests.csproj")]
    if not args.unit_only:
        projects += [("host", "test/ui-host/MakeItZBS.UiHost.csproj")]
    inventory = {}
    for name, project in projects:
        command = ["dotnet", "build", "-c", "Release", project]
        if name == "unit" and not args.unit_only:
            command += ["/p:IncludeDesktopUiTests=true"]
        run(command, output / f"build-{name}.log")
        directory = REPO / Path(project).parent / "bin/Release/net472"
        inventory[name] = copy_verified(directory, stage / name)
    if args.installed_root:
        inventory["installed"] = installed_artifacts(args.installed_root.resolve(), stage)
    else:
        shutil.copytree(REPO / "ui", stage / "ui", dirs_exist_ok=True)
    (output / "deployment-sha256.json").write_text(json.dumps(inventory, indent=2), encoding="utf-8")
    baseline_config(stage / "config")
    shutil.copyfile(REPO / "Make-it-ZBS.bat", stage / "Make-it-ZBS.bat")
    env = os.environ.copy()
    if not args.unit_only:
        env["ZBS_UI_ARTIFACT_DIR"] = str(output)
        env["ZBS_UI_HOST_PATH"] = str(stage / "host/MakeItZBS.UiHost.exe")
        env["ZBS_SMOKE_SAMPLES"] = str(REPO / "docs/manual-smoke/samples")
    if args.word_pipeline or args.p0_pipeline:
        inventory["pipeline"] = prepare_pipeline(REPO / "dist/MakeItZBS", stage / "pipeline-app")
        env["ZBS_WORD_PIPELINE_APP"] = str(stage / "pipeline-app")
        if args.p0_pipeline:
            env["ZBS_P0_PIPELINE_APP"] = str(stage / "pipeline-app")
        (output / "deployment-sha256.json").write_text(json.dumps(inventory, indent=2), encoding="utf-8")
    return env


def selected_ui_filter(args):
    result = "(FullyQualifiedName~MakeItZBS.UiTests|TestCategory=Desktop)"
    exclusions = [(args.word, "FullyQualifiedName!~WordUiTests"),
                  (args.p0_pipeline, "TestCategory!=P0Pipeline"),
                  (args.live_codex, "TestCategory!=LiveCodex"),
                  (args.live_image, "TestCategory!=LiveImage"),
                  (args.paint, "FullyQualifiedName!~PaintUiTests")]
    for enabled, exclusion in exclusions:
        if not enabled:
            result += f"&{exclusion}"
    if args.filter:
        result += f"&({args.filter})"
    return result


def ui_timeout(args):
    if args.p0_pipeline:
        return 900
    if args.live_image:
        return 600
    return 300


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--unit-only", action="store_true")
    mode.add_argument("--ui-only", action="store_true", help="Run only the selected UI checks, not the ordinary C# suite")
    parser.add_argument("--coverage", action="store_true", help="Collect C# coverage through the existing Coverlet MSBuild package")
    parser.add_argument("--filter", help="Optional VSTest filter for targeted diagnostics")
    parser.add_argument("--installed-root", type=Path, help="Verify installed binaries and UI in an isolated configuration")
    parser.add_argument("--word", action="store_true", help="Verify selection, replacement, Undo, clipboard preservation and stale-source rejection in a new unsaved Word document")
    parser.add_argument("--live-codex", action="store_true", help="Send only synthetic text/code and fixture pixels to the signed-in Codex CLI")
    parser.add_argument("--live-image", action="store_true", help="Request an image edit of the bundled synthetic Paint sketch through Codex")
    parser.add_argument("--paint", action="store_true", help="Capture the bundled sketch in an independently started Paint process")
    parser.add_argument("--word-pipeline", action="store_true", help="Run the complete OneScript/Codex/Preview/Apply/Undo route in a disposable Word document; the normal app must be stopped")
    parser.add_argument("--p0-pipeline", action="store_true", help="Run opt-in complete application routes in disposable editors; requires an idle unlocked desktop and the normal app stopped")
    args = parser.parse_args()
    if args.word_pipeline:
        if args.unit_only:
            parser.error("--word-pipeline requires desktop UI tests")
        args.word = True
    if args.p0_pipeline and args.unit_only:
        parser.error("--p0-pipeline requires desktop UI tests")
    if os.name != "nt":
        raise RuntimeError("An interactive Windows desktop is required")
    output = REPO / "artifacts" / "ui-tests" / uuid.uuid4().hex
    output.mkdir(parents=True)
    stage = Path(tempfile.gettempdir()) / "make-it-zbs-ui" / output.name
    stage.mkdir(parents=True)
    summary = {"stage": str(stage), "artifacts": str(output), "status": "running"}
    try:
        env = prepare_stage(args, stage, output)
        if args.installed_root:
            summary["installed_root"] = str(args.installed_root.resolve())
        if not args.ui_only:
            summary["unit"] = execute_tests(stage, output, env, "unit", UNIT_FILTER, args.coverage, minimum=380)
        if not args.unit_only:
            # WinForms caches process-wide DPI data. UIA tests run in a fresh
            # testhost, not before cold-form unit tests in the same process.
            summary["ui"] = execute_tests(stage, output, env, "ui", selected_ui_filter(args), args.coverage,
                                          merge=args.coverage and not args.ui_only,
                                          minimum=1 if args.filter else 25 + int(args.word) + int(args.paint) + int(args.live_image) + 2 * int(args.live_codex) + 6 * int(args.p0_pipeline),
                                          timeout=ui_timeout(args))
        summary["unit_enabled"] = not args.ui_only
        summary["desktop_ui_enabled"] = not args.unit_only
        summary["status"] = "passed"
    except Exception as error:
        summary["status"] = "failed"
        summary["error"] = str(error)
        raise
    finally:
        (output / "summary.json").write_text(json.dumps(summary, ensure_ascii=False, indent=2), encoding="utf-8")
        print(f"UI test report: {output / 'summary.json'}")


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    main()
