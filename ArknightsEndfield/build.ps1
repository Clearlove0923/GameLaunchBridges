param([string] $GameExe, [string] $OutputDirectory = (Join-Path $PSScriptRoot 'Release'))
$ErrorActionPreference = 'Stop'
if ($GameExe) {
    & (Join-Path $PSScriptRoot 'tools/ExtractGameIcon.ps1') -SourceExe (Resolve-Path -LiteralPath $GameExe).Path -DestinationIco (Join-Path $PSScriptRoot 'tools/endfield.ico')
}
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$vs) { throw 'Install Visual Studio C++ x64 Build Tools and Windows SDK.' }
$cmake = Join-Path $vs 'Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe'
if (!(Test-Path -LiteralPath $cmake)) { $cmake = (Get-Command cmake -ErrorAction Stop).Source }
$build = Join-Path $PSScriptRoot 'build/native-x64'
& $cmake -S $PSScriptRoot -B $build -A x64
if ($LASTEXITCODE -ne 0) { throw 'CMake configure failed.' }
& $cmake --build $build --config Release
if ($LASTEXITCODE -ne 0) { throw 'Native Release build failed.' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $build 'Release/ArknightsEndfieldLaunchBridge.exe') -Destination $OutputDirectory -Force
Get-Item -LiteralPath (Join-Path $OutputDirectory 'ArknightsEndfieldLaunchBridge.exe')
