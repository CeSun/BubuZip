param(
    [ValidateSet('x64', 'arm64')][string]$Architecture = 'x64',
    [string]$OutputDirectory,
    [string]$Publisher = 'CN=BubuZip',
    [string]$CertificateThumbprint
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (!$OutputDirectory) { $OutputDirectory = Join-Path $root "out/windows-$Architecture" }
$output = [IO.Path]::GetFullPath($OutputDirectory)
& dotnet publish (Join-Path $root 'src/BubuZip.Desktop') -c Release -r "win-$Architecture" --self-contained true -o $output
if ($LASTEXITCODE) { throw 'Windows application publish failed.' }
& (Join-Path $PSScriptRoot 'build-windows-shell.ps1') -Architecture $Architecture -OutputDirectory $output -Publisher $Publisher -CertificateThumbprint $CertificateThumbprint
if ($CertificateThumbprint) {
    & (Join-Path $PSScriptRoot 'sign-windows.ps1') -OutputDirectory $output -CertificateThumbprint $CertificateThumbprint
}
