param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$CertificateThumbprint,
    [string]$TimestampUrl = 'http://timestamp.digicert.com'
)
$ErrorActionPreference = 'Stop'
$output = [IO.Path]::GetFullPath($OutputDirectory)
$cert = Get-Item -LiteralPath "Cert:\CurrentUser\My\$CertificateThumbprint"
if (!$cert.HasPrivateKey -or $cert.NotAfter -le (Get-Date) -or $cert.NotBefore -gt (Get-Date)) {
    throw 'The signing certificate must have a private key and be currently valid.'
}
$sdkRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/bin'
$sdk = Get-ChildItem $sdkRoot -Directory |
    Where-Object { $_.Name -match '^10\.0\.' -and (Test-Path (Join-Path $_.FullName 'x64/signtool.exe')) } |
    Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (!$sdk) { throw 'Windows SDK signtool.exe is required.' }
$signtool = Join-Path $sdk.FullName 'x64/signtool.exe'
# Sign our own assemblies and native extension; preserve upstream signatures.
$files = @('BubuZip.exe', 'BubuZip.dll', 'BubuZip.Core.dll', 'BubuZip.UI.dll',
           'BubuZipShell.dll', 'BubuZip.ContextMenu.msix')
foreach ($name in $files) {
    if (!(Test-Path -LiteralPath (Join-Path $output $name) -PathType Leaf)) {
        throw "Missing signing input: $name"
    }
}
foreach ($name in $files) {
    $file = Join-Path $output $name
    & $signtool sign /fd SHA256 /sha1 $cert.Thumbprint /tr $TimestampUrl /td SHA256 $file
    if ($LASTEXITCODE) { throw "Code signing failed: $name" }
    & (Join-Path $PSScriptRoot 'verify-windows-signature.ps1') -Path $file -CertificateThumbprint $cert.Thumbprint
}
Export-Certificate -Cert $cert -FilePath (Join-Path $output 'BubuZip.Signing.cer') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../docs/windows-certificate-install.txt') `
    -Destination (Join-Path $output 'Windows-Certificate.txt') -Force
@(
    "Subject: $($cert.Subject)"
    "Thumbprint (SHA1): $($cert.Thumbprint)"
    "Certificate SHA256: $((Get-FileHash -LiteralPath (Join-Path $output 'BubuZip.Signing.cer') -Algorithm SHA256).Hash)"
    "Expires: $($cert.NotAfter.ToUniversalTime().ToString('u'))"
) | Set-Content -LiteralPath (Join-Path $output 'Windows-Certificate-Fingerprint.txt') -Encoding UTF8
Write-Host "Signed and timestamped $($files.Count) files. Public certificate: $($cert.Thumbprint)"
