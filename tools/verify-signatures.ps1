param(
    [Parameter(Mandatory = $true, ValueFromRemainingArguments = $true)]
    [string[]]$Path
)

$ErrorActionPreference = "Stop"
foreach ($item in $Path) {
    $signature = Get-AuthenticodeSignature -LiteralPath $item
    if ($signature.Status -ne "Valid") {
        throw "Недействительная Authenticode-подпись: $item ($($signature.Status))"
    }
    Write-Host "[signed] $item :: $($signature.SignerCertificate.Subject)"
}
