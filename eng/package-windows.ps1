param(
    [ValidateSet('x64')][string]$Architecture = 'x64',
    [string]$OutputDirectory,
    [string]$Publisher = 'CN=BubuZip',
    [string]$CertificateThumbprint,
    [switch]$Force
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (!$OutputDirectory) { $OutputDirectory = Join-Path $root "out/windows-$Architecture" }
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) {
    if (!$Force) { throw "Output already exists: $output. Use -Force to replace it after successful packaging." }
    if (!(Test-Path -LiteralPath $output -PathType Container) -or
        ((Get-Item -LiteralPath $output).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Output must be a directory, not a link.'
    }
}
$parent = Split-Path $output -Parent
New-Item -ItemType Directory -Force -Path $parent | Out-Null
$temporary = Join-Path $parent ('.bubuzip-aot-' + [Guid]::NewGuid().ToString('N'))
$publish = Join-Path $temporary 'publish'
$package = Join-Path $temporary 'package'
$previous = Join-Path $temporary 'previous'
New-Item -ItemType Directory -Path $publish, $package | Out-Null
$installed = $false
try {
    & dotnet publish (Join-Path $root 'src/BubuZip.Desktop') -c Release -r "win-$Architecture" `
        --self-contained true -p:BubuZipNativeAot=true -p:TrimmerSingleWarn=false -o $publish
    if ($LASTEXITCODE) { throw 'Windows NativeAOT publish failed.' }
    # Publish into a fresh directory so old JIT files can never enter an AOT package.
    Get-ChildItem -LiteralPath $publish -Force | Where-Object Extension -ne '.pdb' |
        Copy-Item -Destination $package -Recurse
    & (Join-Path $PSScriptRoot 'build-windows-shell.ps1') -Architecture $Architecture -OutputDirectory $package -Publisher $Publisher -CertificateThumbprint $CertificateThumbprint
    & (Join-Path $PSScriptRoot 'verify-windows-nativeaot.ps1') -OutputDirectory $package -Architecture $Architecture
    if ($CertificateThumbprint) {
        & (Join-Path $PSScriptRoot 'sign-windows.ps1') -OutputDirectory $package -CertificateThumbprint $CertificateThumbprint
    }
    if (Test-Path -LiteralPath $output) { Move-Item -LiteralPath $output -Destination $previous }
    try { Move-Item -LiteralPath $package -Destination $output }
    catch {
        if (Test-Path -LiteralPath $previous) { Move-Item -LiteralPath $previous -Destination $output }
        throw
    }
    $installed = $true
    Write-Host "Packaged Windows NativeAOT application: $output"
} finally {
    # All cleanup is confined to this invocation's sibling staging directory.
    if ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($temporary)) -ne [IO.Path]::GetFullPath($parent)) {
        throw 'Unexpected staging path.'
    }
    if (!$installed -and (Test-Path -LiteralPath $previous)) {
        Write-Warning "Previous output could not be restored; retained at $previous"
    } else {
        Remove-Item -LiteralPath $temporary -Recurse -Force
    }
}
