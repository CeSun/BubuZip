param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][ValidateSet('x64', 'arm64')][string]$Architecture
)
$ErrorActionPreference = 'Stop'
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (!(Test-Path -LiteralPath (Join-Path $output 'BubuZip.exe'))) { throw 'NativeAOT executable is missing.' }
$forbidden = Get-ChildItem -LiteralPath $output -Recurse -File | Where-Object {
    $_.Name -match '\.(runtimeconfig|deps)\.json$|\.pdb$' -or
    $_.Name -in @('coreclr.dll', 'clrjit.dll', 'hostfxr.dll', 'hostpolicy.dll')
}
if ($forbidden) { throw "NativeAOT package contains managed runtime/debug files: $($forbidden.Name -join ', ')" }
$expected = if ($Architecture -eq 'x64') { [Reflection.PortableExecutable.Machine]::Amd64 } else { [Reflection.PortableExecutable.Machine]::Arm64 }
foreach ($file in Get-ChildItem -LiteralPath $output -Recurse -File | Where-Object Extension -in @('.exe', '.dll')) {
    $stream = [IO.File]::OpenRead($file.FullName)
    $reader = [Reflection.PortableExecutable.PEReader]::new($stream)
    try {
        if ($reader.HasMetadata) { throw "Managed assembly in NativeAOT package: $($file.Name)" }
        if ($reader.PEHeaders.CoffHeader.Machine -ne $expected) { throw "Incorrect native architecture: $($file.Name)" }
    } finally { $reader.Dispose(); $stream.Dispose() }
}
Write-Host "Verified Windows $Architecture NativeAOT payload."
