param(
    [Parameter(Mandatory = $true)] [string]$ServerUrl,
    [Parameter(Mandatory = $true)] [string]$Repository,
    [Parameter(Mandatory = $true)] [string]$Tag,
    [Parameter(Mandatory = $true)] [string]$Token,
    [Parameter(Mandatory = $true)] [string[]]$Artifact,
    [switch]$Prerelease,
    [switch]$Unsigned
)

$ErrorActionPreference = "Stop"
$parts = $Repository.Split('/', 2)
if ($parts.Count -ne 2) {
    throw "Repository должен иметь формат owner/name."
}
if ($Unsigned -and -not $Prerelease) {
    throw "Неподписанный Windows release должен быть помечен как prerelease."
}

$api = $ServerUrl.TrimEnd('/') + "/api/v1/repos/$($parts[0])/$($parts[1])"
$headers = @{ Authorization = "token $Token" }
$releaseBody = if ($Unsigned) {
    @'
Неподписанная beta-версия Windows. Authenticode отсутствует, поэтому Windows
может показать «Неизвестный издатель» или предупреждение SmartScreen.

Перед запуском скачайте приложенный `.zip.sha256` и сравните SHA-256 архива.
Внутри ZIP также находится `MANIFEST.sha256` с контрольными суммами файлов.
'@
} else {
    "Подписанный Windows release. Проверьте SHA-256 по приложенному manifest."
}
$payload = @{
    tag_name = $Tag
    name = $Tag
    body = $releaseBody
    draft = $false
    prerelease = [bool]$Prerelease
} | ConvertTo-Json
$payloadBytes = [Text.Encoding]::UTF8.GetBytes($payload)

try {
    $release = Invoke-RestMethod -Method Post -Uri "$api/releases" `
        -Headers $headers -ContentType "application/json; charset=utf-8" -Body $payloadBytes
}
catch {
    $release = Invoke-RestMethod -Method Get -Uri "$api/releases/tags/$Tag" -Headers $headers
}

foreach ($path in $Artifact) {
    $resolved = (Resolve-Path -LiteralPath $path).Path
    $name = [Uri]::EscapeDataString([IO.Path]::GetFileName($resolved))
    & curl.exe --fail --silent --show-error `
        -H "Authorization: token $Token" `
        -F "attachment=@$resolved" `
        "$api/releases/$($release.id)/assets?name=$name" | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Не удалось загрузить release asset: $resolved"
    }
    Write-Host "[release] uploaded $resolved"
}
