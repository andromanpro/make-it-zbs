#!/usr/bin/env py
# -*- coding: utf-8 -*-
"""
Сборка ПОРТИРУЕМОГО дистрибутива Make it ZBS (Phase 9).

Собирает self-contained папку dist/MakeItZBS/ : лаунчер MakeItZBS.exe +
бандленный portable-oscript (runtime/) + дерево приложения. Запускается
владельцем на Windows для выпуска релиза; dist/ — артефакт, в git НЕ
коммитится (.gitignore).

Запуск:  py -3.14 tools/build-dist.py
Пред-условие: собран лаунчер (dotnet build -c Release src/Launcher) и
              ZBSWindowsComponent (lib/net472/*.dll актуальны).

Раскладка dist/MakeItZBS/ (см. контракт лаунчера):
  MakeItZBS.exe
  runtime/bin/oscript.exe   (+ движок)   <- verified vendor archive
  src/onescript/...
  lib/net472/*.dll
  config/*.json  (baseline из git HEAD; .env — пустой плейсхолдер, БЕЗ секрета)
  ui/*
"""
import os, shutil, stat, subprocess, sys
import hashlib
import json
from pathlib import Path, PurePosixPath
import tempfile
import zipfile
import importlib.util

sys.stdout.reconfigure(encoding="utf-8")
REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
DIST = os.path.join(REPO, "dist", "MakeItZBS")

LAUNCHER_NAME = "MakeItZBS.exe"
OSCRIPT_NAME = "oscript.exe"


def fail(msg):
    print("ОШИБКА:", msg)
    sys.exit(1)


def _force_rw(func, path, _exc):
    # read-only (git pack) → снять флаг и повторить unlink/rmdir.
    try:
        os.chmod(path, stat.S_IWRITE)
        func(path)
    except OSError:
        pass


def rmtree_robust(path):
    if os.path.isdir(path):
        # onexc is Python 3.12+; this project explicitly requires Python 3.14.
        shutil.rmtree(path, onexc=_force_rw)  # NOSONAR


def copytree(src, dst, ignore=None):
    shutil.copytree(src, dst, dirs_exist_ok=True,
                     ignore=shutil.ignore_patterns(*ignore) if ignore else None)


def read_private_runtime():
    vendor = os.path.join(REPO, "vendor", "onescript")
    with open(os.path.join(vendor, "manifest.json"), encoding="utf-8") as stream:
        manifest = json.load(stream)
    archive_path = os.path.join(vendor, "runtime.zip")
    with open(archive_path, "rb") as stream:
        if hashlib.file_digest(stream, "sha256").hexdigest() != manifest["sha256"]:
            raise ValueError("Private runtime archive checksum mismatch")
    with zipfile.ZipFile(archive_path) as archive:
        names = archive.namelist()
        if len(names) != len(set(names)) or set(names) != set(manifest["files"]):
            raise ValueError("Private runtime inventory mismatch")
        contents = {}
        for name in names:
            parts = PurePosixPath(name).parts
            if len(parts) != 2 or parts[0] != "bin" or any(c in name for c in ("\\", ":")):
                raise ValueError("Invalid private runtime path")
            if parts[1].lower() in (".", "..", "dotnetzip.dll"):
                raise ValueError("Unsafe private runtime entry")
            content = archive.read(name)
            if hashlib.sha256(content).hexdigest() != manifest["files"][name]:
                raise ValueError("Private runtime file checksum mismatch")
            contents[name] = content
    return manifest, contents


def install_private_runtime(destination):
    manifest, contents = read_private_runtime()
    for name, content in contents.items():
        target = os.path.join(destination, *PurePosixPath(name).parts)
        if not Path(target).resolve().is_relative_to(Path(destination).resolve()):
            raise ValueError("Runtime destination contains an escaping filesystem link")
        if os.path.isfile(target):
            with open(target, "rb") as stream:
                if hashlib.file_digest(stream, "sha256").hexdigest() == manifest["files"][name]:
                    continue
            raise ValueError("Existing private runtime file was modified: " + name)
        os.makedirs(os.path.dirname(target), exist_ok=True)
        with tempfile.NamedTemporaryFile(dir=os.path.dirname(target), delete=False) as stream:
            temporary = stream.name
            stream.write(content)
        try:
            os.replace(temporary, target)
        finally:
            if os.path.exists(temporary):
                os.unlink(temporary)
    if os.path.exists(os.path.join(destination, "bin", "DotNetZip.dll")):
        raise ValueError("Obsolete DotNetZip found in destination runtime")


def prepare_private_runtime():
    with open(os.path.join(REPO, "vendor", "onescript", "manifest.json"), encoding="utf-8") as stream:
        manifest = json.load(stream)
    digest = manifest["sha256"]
    if len(digest) != 64 or any(c not in "0123456789abcdef" for c in digest):
        raise ValueError("Invalid runtime digest")
    destination = os.path.join(REPO, "artifacts", "runtime", digest[:16] + "-standalone")
    install_private_runtime(destination)
    if os.path.exists(os.path.join(destination, "lib")):
        raise ValueError("Standalone runtime must not contain external script libraries")
    return destination


def main():
    # 0. Пред-условия
    launcher = os.path.join(REPO, "src", "Launcher", "bin", "Release", "net472", LAUNCHER_NAME)
    if not os.path.isfile(launcher):
        fail("нет " + launcher + "\nСобери: dotnet build -c Release src/Launcher")
    dll = os.path.join(REPO, "lib", "net472", "ZBSWindowsComponent.dll")
    if not os.path.isfile(dll):
        fail("нет " + dll + "\nСобери ZBSWindowsComponent + скопируй в lib/net472")

    # Clean only the generated application, preserving previously built release archives.
    target = Path(DIST).resolve()
    repo = Path(REPO).resolve()
    if target != repo / "dist/MakeItZBS" and not target.is_relative_to(repo / "artifacts"):
        fail("Distribution target must stay inside the workspace output directories")
    if target == repo / "artifacts":
        fail("Refusing to replace the artifacts root")
    rmtree_robust(str(target))
    os.makedirs(DIST)

    # 2. Лаунчер в корень
    shutil.copy2(launcher, os.path.join(DIST, LAUNCHER_NAME))

    # No global OVM inputs: script JSON and console logging are self-contained.
    install_private_runtime(os.path.join(DIST, "runtime"))
    shutil.copy2(
        os.path.join(REPO, "src", "runtime", "oscript.exe.config"),
        os.path.join(DIST, "runtime", "bin", "oscript.exe.config"))

    # 4. Дерево приложения (структура как в репо — относительные пути
    #    zbs-run/ProjectPaths резолвятся от каталога скрипта вверх).
    copytree(os.path.join(REPO, "src", "onescript"), os.path.join(DIST, "src", "onescript"),
             ignore=["*.tmp"])
    copytree(os.path.join(REPO, "lib", "net472"), os.path.join(DIST, "lib", "net472"))
    copytree(os.path.join(REPO, "ui"), os.path.join(DIST, "ui"))
    spec = importlib.util.spec_from_file_location("zbs_vscode_build", Path(__file__).with_name("build-vscode.py"))
    adapter = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(adapter)
    adapter.build(Path(DIST) / "adapters" / "make-it-zbs-editor.vsix")
    copytree(os.path.join(REPO, "vendor", "onescript"), os.path.join(DIST, "licenses", "onescript"),
             ignore=["runtime.zip"])
    for name in ("LICENSE", "THIRD-PARTY-NOTICES.md", "PRIVACY.md", "INSTALL.md",
                 "README.md", "SECURITY.md", "TESTING.md", "CONTRIBUTING.md"):
        shutil.copy2(os.path.join(REPO, name), os.path.join(DIST, name))
    for relative in ("docs/replacement-backups.md", "docs/images/wiki-hero-magic-button.png",
                     "integrations/vscode/README.md"):
        target = Path(DIST) / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(Path(REPO) / relative, target)
    copytree(os.path.join(REPO, "vendor", "licenses"), os.path.join(DIST, "licenses", "nuget"))

    # 5. Конфиги — baseline из git HEAD (НЕ рабочая копия: не утечёт
    #    личный settings.json с dev.mockAI и т.п.). .env НЕ копируем
    #    (секрет!) — кладём пустой плейсхолдер, токен вводится в Настройках.
    os.makedirs(os.path.join(DIST, "config"))
    for name in ("settings.json", "models.json", "prompts.json", "model-catalog.json"):
        try:
            data = subprocess.check_output(
                ["git", "show", "HEAD:config/" + name], cwd=REPO)
        except subprocess.CalledProcessError:
            print("  пропуск config/" + name + " (нет в git HEAD)")
            continue
        with open(os.path.join(DIST, "config", name), "wb") as f:
            f.write(data)
    with open(os.path.join(DIST, "config", ".env"), "w", encoding="utf-8", newline="") as f:
        f.write("OPENROUTER_API_KEY=\n")  # пустой плейсхолдер, без секрета

    # 6. Ярлык «Make it ZBS.lnk» рядом (солидный двойной клик с иконкой).
    #    .lnk через WScript.Shell — единственный способ из коробки;
    #    best-effort, не валим сборку при сбое.
    lnk = os.path.join(DIST, "Make it ZBS.lnk")
    ps = (
        "$s=(New-Object -ComObject WScript.Shell).CreateShortcut('%s');"
        "$s.TargetPath='%s';$s.WorkingDirectory='%s';$s.IconLocation='%s';"
        "$s.Description='Make it ZBS';$s.Save()"
        % (lnk, os.path.join(DIST, LAUNCHER_NAME), DIST,
           os.path.join(DIST, LAUNCHER_NAME))
    )
    try:
        subprocess.run(["powershell", "-NoProfile", "-Command", ps],
                        check=True, capture_output=True)
        print("  ярлык создан: Make it ZBS.lnk")
    except Exception as e:
        print("  ярлык пропущен (best-effort):", e)

    # 7. Итог + размер
    total = sum(os.path.getsize(os.path.join(r, f))
                for r, _, fs in os.walk(DIST) for f in fs)
    print("ГОТОВО → " + DIST)
    print("Размер дистрибутива: %.1f MB" % (total / 1024 / 1024))
    print("Раздавать: всю папку dist/MakeItZBS/. Запуск — MakeItZBS.exe "
          "(или ярлык). Токен OpenRouter — в окне Настройки (трей).")
    print("Требования к машине: Windows 10/11, .NET Framework 4.8, "
          "WebView2 Evergreen runtime.")


if __name__ == "__main__":
    if sys.argv[1:] == ["--prepare-runtime"]:
        print(prepare_private_runtime())
    elif sys.argv[1:] == ["--run"]:
        runtime = prepare_private_runtime()
        sys.exit(subprocess.call([os.path.join(runtime, "bin", OSCRIPT_NAME), "-encoding=utf-8",
                                 os.path.join(REPO, "src", "onescript", "zbs-run.os")], cwd=REPO))
    elif sys.argv[1:] == ["--smoke-dist"]:
        DIST = os.path.join(REPO, "artifacts", "distribution-smoke", "MakeItZBS")
        main()
    else:
        main()
