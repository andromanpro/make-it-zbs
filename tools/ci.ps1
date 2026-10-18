param(
    [switch]$VerifyReleaseDll,
    [switch]$RunUiTests,
    [switch]$RunWordSmoke,
    [switch]$RunPaintSmoke
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$pythonSources = "tools,libraries/onescript-windows"
Set-Location $root
if (($RunWordSmoke -or $RunPaintSmoke) -and -not $RunUiTests) {
    throw "Word/Paint checks require -RunUiTests and an interactive desktop."
}

function Invoke-Checked {
    param([scriptblock]$Command, [string]$Name)
    & $Command
    if ($LASTEXITCODE -ne 0) {
        throw "$Name failed with exit code $LASTEXITCODE"
    }
}

function Invoke-Python {
    if ($env:ZbsPythonCommand -eq "python") {
        & python @args
    } else {
        & py -3.14 @args
    }
}

$runtimeRoot = (Invoke-Python tools/build-dist.py --prepare-runtime)
if ($LASTEXITCODE -ne 0) { throw "Private OneScript runtime preparation failed" }
$oscriptPath = Join-Path $runtimeRoot.Trim() "bin/oscript.exe"

Invoke-Checked { npm ci --ignore-scripts } "npm ci"
Invoke-Checked { npm audit --audit-level=high } "JavaScript dependency audit"
Invoke-Checked { npm run lint:js } "JavaScript lint"
Invoke-Checked { npm run coverage:js } "JavaScript tests"
Invoke-Checked {
    Invoke-Python -m pip install --disable-pip-version-check -r requirements-dev.txt
} "Python test dependencies"
Invoke-Checked { Invoke-Python -m coverage erase } "Python coverage reset"
Invoke-Checked {
    Invoke-Python -m coverage run --branch --source=$pythonSources -m unittest discover `
        -s test/python -p "test_*.py"
} "Python tests"
New-Item -ItemType Directory -Path artifacts/coverage/python -Force | Out-Null
Invoke-Checked {
    Invoke-Python -m coverage xml -o artifacts/coverage/python/coverage.xml
} "Python coverage report"
Invoke-Checked {
    $uiArguments = @()
    if (-not $RunUiTests) { $uiArguments += "--unit-only" }
    if ($RunWordSmoke) { $uiArguments += "--word" }
    if ($RunPaintSmoke) { $uiArguments += "--paint" }
    Invoke-Python -m coverage run --branch --source=$pythonSources --parallel-mode tools/run-ui-tests.py --coverage @uiArguments
} "C# tests"

Invoke-Checked {
    Invoke-Python -m coverage run --branch --source=$pythonSources --parallel-mode tools/run-onescript-tests.py --runtime $runtimeRoot.Trim()
} "OneScript tests"
Invoke-Checked {
    dotnet build -c Release src/ZBSWindowsComponent/ZBSWindowsComponent.csproj --no-incremental
} "C# Release build"
Invoke-Checked {
    Invoke-Python -m coverage run --branch --source=$pythonSources --parallel-mode tools/check-reproducible.py
} "Release reproducibility across commit identities"
Invoke-Checked {
    dotnet build -c Release src/Launcher/MakeItZBS.Launcher.csproj --no-incremental
} "Launcher Release build"
Invoke-Checked { Invoke-Python tools/check-secrets.py } "Secret scan"
Invoke-Checked { Invoke-Python tools/collect-licenses.py --check } "Runtime license inventory"

$hash = Invoke-Checked {
    $hashArguments = @('--write-hash')
    if ($VerifyReleaseDll) { $hashArguments += '--verify-shipped' }
    Invoke-Python -m coverage run --branch --source=$pythonSources --parallel-mode tools/check-reproducible.py @hashArguments
} "Shipped binary inventory"
Invoke-Checked {
    $libraryArguments = @('--allow-dirty')
    if ($RunUiTests) { $libraryArguments += '--test' }
    Invoke-Python -m coverage run --branch --source=$pythonSources --parallel-mode libraries/onescript-windows/build.py @libraryArguments
} "Independent Windows library source package and optional desktop smoke"
Invoke-Checked { Invoke-Python -m coverage combine --append } "Isolated test runner coverage merge"
Invoke-Checked {
    Invoke-Python -m coverage xml -o artifacts/coverage/python/coverage.xml
} "Combined Python coverage report"
Write-Host "CI checks passed. DLL SHA-256: $hash"
