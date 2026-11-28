param(
    [string]$OutputDirectory = (Join-Path $env:LOCALAPPDATA 'BubuZip/signing'),
    [string]$Subject = 'CN=BubuZip',
    [Security.SecureString]$Password
)
$ErrorActionPreference = 'Stop'
$output = [IO.Path]::GetFullPath($OutputDirectory)
$pfx = Join-Path $output 'BubuZip.Signing.pfx'
$cer = Join-Path $output 'BubuZip.Signing.cer'
$passwordFile = Join-Path $output 'BubuZip.Signing.password.dpapi'
foreach ($path in @($pfx, $cer, $passwordFile)) {
    if (Test-Path -LiteralPath $path) { throw "Signing material already exists: $path. Reuse it for later releases." }
}
New-Item -ItemType Directory -Force -Path $output | Out-Null
if (!$Password) {
    $bytes = New-Object byte[] 48
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    $Password = ConvertTo-SecureString ([Convert]::ToBase64String($bytes)) -AsPlainText -Force
}
# Store the password encrypted with Windows DPAPI for this Windows user only.
$Password | ConvertFrom-SecureString | Set-Content -LiteralPath $passwordFile -Encoding ASCII
$cert = New-SelfSignedCertificate -Type Custom -Subject $Subject -FriendlyName 'BubuZip release signing' `
    -CertStoreLocation 'Cert:\CurrentUser\My' -KeyAlgorithm RSA -KeyLength 3072 `
    -HashAlgorithm SHA256 -KeyExportPolicy Exportable -KeyUsage DigitalSignature `
    -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}') `
    -NotAfter (Get-Date).AddYears(3)
Export-PfxCertificate -Cert $cert -FilePath $pfx -Password $Password | Out-Null
Export-Certificate -Cert $cert -FilePath $cer | Out-Null
Write-Host "Certificate subject: $($cert.Subject)"
Write-Host "Certificate thumbprint: $($cert.Thumbprint)"
Write-Host "Expires: $($cert.NotAfter.ToString('yyyy-MM-dd'))"
Write-Host "Private signing material: $output"
Write-Host 'No trust certificate was installed. See docs/windows-signing.md to configure GitHub Secrets.'
