param(
    [string] $GameExe,
    [string] $OutputDirectory = (Join-Path $PSScriptRoot 'Release')
)

$ErrorActionPreference = 'Stop'
$iconFile = Join-Path $PSScriptRoot 'tools\LaunchCapture\nte.ico'

if ($GameExe) {
    $resolvedGameExe = (Resolve-Path -LiteralPath $GameExe).Path
    & (Join-Path $PSScriptRoot 'tools\ExtractGameIcon.ps1') -SourceExe $resolvedGameExe -DestinationIco $iconFile
}
if (-not (Test-Path -LiteralPath $iconFile)) {
    throw "Missing icon: $iconFile"
}

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) {
    throw 'Visual Studio Build Tools with Desktop development with C++ is required.'
}
$vsRoot = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vsRoot) { throw 'MSVC x64 tools were not found.' }

$cmake = Get-Command cmake -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -First 1
if (-not $cmake) {
    $bundledCmake = Join-Path $vsRoot 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
    if (Test-Path -LiteralPath $bundledCmake) { $cmake = $bundledCmake }
}
if (-not $cmake) { throw 'CMake was not found. Install the Visual Studio CMake component.' }

$buildRoot = Join-Path $PSScriptRoot 'build\native-x64'
& $cmake -S $PSScriptRoot -B $buildRoot -A x64 -DCMAKE_BUILD_TYPE=Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $cmake --build $buildRoot --config Release --target NevernessToEvernessLaunchBridge NevernessToEvernessLaunchBridgeTestHost
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

[void](New-Item -ItemType Directory -Path $OutputDirectory -Force)
$builtExe = Join-Path $buildRoot 'Release\NevernessToEvernessLaunchBridge.exe'
if (-not (Test-Path -LiteralPath $builtExe)) {
    $builtExe = Join-Path $buildRoot 'NevernessToEvernessLaunchBridge.exe'
}
Copy-Item -LiteralPath $builtExe -Destination (Join-Path $OutputDirectory 'NevernessToEvernessLaunchBridge.exe') -Force
Get-Item -LiteralPath (Join-Path $OutputDirectory 'NevernessToEvernessLaunchBridge.exe')
