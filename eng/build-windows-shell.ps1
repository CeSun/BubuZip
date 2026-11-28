param(
    [ValidateSet('x64', 'arm64')][string]$Architecture = 'x64',
    [Parameter(Mandatory)][string]$OutputDirectory,
    # Must match the signing certificate's subject. Unsigned output is build-only, not installable.
    [string]$Publisher = 'CN=BubuZip',
    [string]$CertificateThumbprint
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$output = [IO.Path]::GetFullPath($OutputDirectory)
$build = Join-Path $root "out/windows-shell-build/$Architecture"
$layout = Join-Path $build 'package'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$visualStudio = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$visualStudio) { throw 'Install Visual Studio C++ build tools and the Windows SDK first.' }
$cmake = Join-Path $visualStudio 'Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe'
$sdkRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/bin'
$sdk = Get-ChildItem $sdkRoot -Directory | Where-Object { $_.Name -match '^10\.0\.' -and (Test-Path (Join-Path $_.FullName 'x64/makeappx.exe')) } | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (!$sdk) { throw 'Windows SDK makeappx.exe is required.' }
$makepri = Join-Path $sdk.FullName 'x64/makepri.exe'
if (!(Test-Path -LiteralPath $makepri)) { throw 'Windows SDK makepri.exe is required for taskbar icon resources.' }
$cmakeArchitecture = if ($Architecture -eq 'arm64') { 'ARM64' } else { 'x64' }
& $cmake -S (Join-Path $PSScriptRoot 'windows-shell') -B $build -A $cmakeArchitecture
if ($LASTEXITCODE) { throw 'Shell extension configuration failed.' }
& $cmake --build $build --config Release
if ($LASTEXITCODE) { throw 'Shell extension build failed.' }
if ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString() -ieq $Architecture) {
    & (Join-Path $build 'Release/ShellTests.exe')
    if ($LASTEXITCODE) { throw 'Shell extension tests failed.' }
}
New-Item -ItemType Directory -Force -Path $output, (Join-Path $layout 'Assets') | Out-Null
$manifest = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'windows-shell/AppxManifest.xml'))
$manifest = $manifest.Replace('@ARCH@', $Architecture).Replace('@PUBLISHER@', [Security.SecurityElement]::Escape($Publisher))
[IO.File]::WriteAllText((Join-Path $layout 'AppxManifest.xml'), $manifest)
Add-Type -AssemblyName System.Drawing
$icon = [Drawing.Icon]::new((Join-Path $root 'assets/icons/BubuZip.ico'), 256, 256)
$bitmap = $icon.ToBitmap()
try {
    $assets = @(@('StoreLogo', 50), @('Square44x44Logo', 44), @('Square150x150Logo', 150))
    # Packaged windows use these variants instead of the EXE icon. Without both
    # unplated forms Windows adds an accent-color backplate, even with transparent BackgroundColor.
    foreach ($size in @(16, 20, 24, 30, 32, 36, 40, 44, 48, 60, 64, 72, 80, 96, 256)) {
        foreach ($form in @('', '_altform-unplated', '_altform-lightunplated')) {
            $assets += ,@("Square44x44Logo.targetsize-$size$form", $size)
        }
    }
    foreach ($asset in $assets) {
        $scaled = [Drawing.Bitmap]::new($bitmap, [int]$asset[1], [int]$asset[1])
        try { $scaled.Save((Join-Path $layout "Assets/$($asset[0]).png"), [Drawing.Imaging.ImageFormat]::Png) }
        finally { $scaled.Dispose() }
    }
} finally { $bitmap.Dispose(); $icon.Dispose() }
# Qualified filenames must be indexed by MRT; just placing the PNGs in the MSIX
# is insufficient. Keep all language/scale candidates in this one sparse package.
$priConfig = Join-Path $build 'priconfig.xml'
& $makepri createconfig /cf $priConfig /dq en-US /o | Out-Null
if ($LASTEXITCODE) { throw 'Resource index configuration failed.' }
[xml]$config = Get-Content -LiteralPath $priConfig -Raw
foreach ($packaging in @($config.SelectNodes('/resources/packaging'))) {
    [void]$packaging.ParentNode.RemoveChild($packaging)
}
$config.Save($priConfig)
$pri = Join-Path $build 'resources.pri'
# Use an assets-only input tree so a previous resources.pri cannot be reindexed
# on incremental builds. Keep the Assets prefix used by the manifest.
$priInput = Join-Path $build 'pri-input'
New-Item -ItemType Directory -Force -Path (Join-Path $priInput 'Assets') | Out-Null
Copy-Item -Path (Join-Path $layout 'Assets/*.png') -Destination (Join-Path $priInput 'Assets') -Force
& $makepri new /pr $priInput /cf $priConfig /mn (Join-Path $layout 'AppxManifest.xml') /of $pri /o | Out-Null
if ($LASTEXITCODE) { throw 'Taskbar icon resource indexing failed.' }
Copy-Item -LiteralPath $pri -Destination (Join-Path $layout 'resources.pri') -Force
# Sparse packages reference the EXE and DLL in ExternalLocation; /nv permits their absence from the package.
& (Join-Path $sdk.FullName 'x64/makeappx.exe') pack /d $layout /p (Join-Path $output 'BubuZip.ContextMenu.msix') /nv /o
if ($LASTEXITCODE) { throw 'Sparse package creation failed.' }
if ($CertificateThumbprint) {
    & (Join-Path $sdk.FullName 'x64/signtool.exe') sign /fd SHA256 /sha1 $CertificateThumbprint (Join-Path $output 'BubuZip.ContextMenu.msix')
    if ($LASTEXITCODE) { throw 'Sparse package signing failed. Publisher must match a trusted signing certificate.' }
}
Copy-Item -LiteralPath (Join-Path $build 'Release/BubuZipShell.dll') -Destination $output -Force
# Sparse packages resolve the manifest logos against the external location, so the
# generated assets must sit next to the exe or the taskbar button renders blank.
$assetOutput = Join-Path $output 'Assets'
New-Item -ItemType Directory -Force -Path $assetOutput | Out-Null
Copy-Item -Path (Join-Path $layout 'Assets/*.png') -Destination $assetOutput -Force
Copy-Item -LiteralPath $pri -Destination (Join-Path $output 'resources.pri') -Force
Write-Host "Windows 11 shell extension: $output"
if (!$CertificateThumbprint) {
    Write-Warning 'The MSIX is not signed yet. Sign it with a trusted certificate before registering the Windows 11 menu.'
}
