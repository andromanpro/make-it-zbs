param(
    [Parameter(Mandatory = $true)]
    [string[]]$Path
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($env:WINDOWS_SIGNING_CERT_BASE64) -or
    [string]::IsNullOrWhiteSpace($env:WINDOWS_SIGNING_CERT_PASSWORD)) {
    throw "Для подписанного release нужны WINDOWS_SIGNING_CERT_BASE64 и WINDOWS_SIGNING_CERT_PASSWORD."
}

$signtool = Get-ChildItem -Path "${env:ProgramFiles(x86)}\Windows Kits\10\bin" `
    -Filter signtool.exe -Recurse -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match '\\x64\\signtool\.exe$' } |
    Sort-Object FullName -Descending |
    Select-Object -First 1
if ($null -eq $signtool) {
    throw "signtool.exe не найден в Windows SDK."
}

$certificatePath = Join-Path ([System.IO.Path]::GetTempPath()) ("zbs-signing-" + [guid]::NewGuid().ToString("N") + ".pfx")
try {
    [System.IO.File]::WriteAllBytes(
        $certificatePath,
        [Convert]::FromBase64String($env:WINDOWS_SIGNING_CERT_BASE64))

    foreach ($item in $Path) {
        $resolved = (Resolve-Path -LiteralPath $item).Path
        & $signtool.FullName sign /fd SHA256 /td SHA256 `
            /tr "http://timestamp.digicert.com" /f $certificatePath `
            /p $env:WINDOWS_SIGNING_CERT_PASSWORD $resolved
        if ($LASTEXITCODE -ne 0) {
            throw "Не удалось подписать $resolved"
        }
    }
}
finally {
    if (Test-Path -LiteralPath $certificatePath) {
        Remove-Item -LiteralPath $certificatePath -Force
    }
}

& (Join-Path $PSScriptRoot "verify-signatures.ps1") @Path
