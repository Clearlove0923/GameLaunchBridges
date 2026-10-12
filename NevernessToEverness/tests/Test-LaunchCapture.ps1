param(
    [string] $ProjectRoot = (Split-Path $PSScriptRoot -Parent)
)

$ErrorActionPreference = 'Stop'
$captureName = 'NevernessToEvernessLaunchCapture'
$captureBuild = Join-Path $ProjectRoot 'tools\LaunchCapture\bin\Release\net8.0-windows\win-x64'
$captureExe = Join-Path $ProjectRoot "Capture\$captureName.exe"
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ("nte-capture-test-" + [Guid]::NewGuid().ToString('N'))
$resolvedTempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
if (-not $resolvedTestRoot.StartsWith(($resolvedTempRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar), [StringComparison]::OrdinalIgnoreCase) -or
    -not (Split-Path $resolvedTestRoot -Leaf).StartsWith('nte-capture-test-', [StringComparison]::Ordinal)) {
    throw "Test directory is outside the system temporary directory: $resolvedTestRoot"
}

try {
    [void](New-Item -ItemType Directory -Path $testRoot)
    Get-ChildItem -LiteralPath $captureBuild -File | Copy-Item -Destination $testRoot
    $testDll = Join-Path $testRoot "$captureName.dll"
    $logFile = Join-Path $testRoot "$captureName-log\$captureName.log"

    function New-Stub([string] $path) {
        [void](New-Item -ItemType Directory -Path (Split-Path $path -Parent) -Force)
        [IO.File]::WriteAllBytes($path, [byte[]](1, 2, 3))
    }

    $standard = Join-Path $testRoot 'standard'
    New-Stub (Join-Path $standard 'NTELauncher\NTEGame.exe')
    New-Stub (Join-Path $standard 'Client\WindowsNoEditor\HT\Binaries\Win64\HTGame.exe')
    $output = & dotnet $testDll --validate --no-dialog --install-root $standard 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Standard installation discovery failed: $output" }

    $compact = Join-Path $testRoot 'compact'
    New-Stub (Join-Path $compact 'LauncherRuntime\NTEGame.exe')
    New-Stub (Join-Path $compact 'Game\Bin\HTGame.exe')
    $output = & dotnet $testDll --validate --no-dialog --install-root $compact 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Compact installation discovery failed: $output" }

    $decoy = Join-Path $testRoot 'decoy'
    New-Stub (Join-Path $decoy 'Backup\NTEGame.exe')
    New-Stub (Join-Path $decoy 'Backup\HTGame.exe')
    $ErrorActionPreference = 'Continue'
    $null = & dotnet $testDll --validate --no-dialog --install-root $decoy 2>&1
    $ErrorActionPreference = 'Stop'
    if ($LASTEXITCODE -eq 0) { throw 'Backup decoys must not pass validation.' }

    $old = "[$((Get-Date).AddDays(-9).ToString('o'))] old-entry"
    $recent = "[$((Get-Date).AddDays(-1).ToString('o'))] recent-entry"
    [IO.File]::WriteAllLines($logFile, [string[]]@($old, $recent))
    $null = & dotnet $testDll --validate --no-dialog --install-root $standard 2>&1
    if ($LASTEXITCODE -ne 0) { throw 'Log retention validation run failed.' }
    $logText = [IO.File]::ReadAllText($logFile)
    if ($logText.Contains('old-entry') -or -not $logText.Contains('recent-entry')) { throw 'Seven-day log retention failed.' }

    $ErrorActionPreference = 'Continue'
    $null = & dotnet $testDll --validate --no-dialog --install-root (Join-Path $testRoot 'missing') 2>&1
    $ErrorActionPreference = 'Stop'
    if ($LASTEXITCODE -eq 0) { throw 'A missing install root must return an error.' }
    $logText = [IO.File]::ReadAllText($logFile)
    if ($logText -notmatch 'DirectoryNotFoundException' -or $logText -notmatch '\bat NevernessToEvernessLaunchCapture\.Program') {
        throw 'Error log lacks the exception type or stack trace.'
    }

    if (-not (Test-Path -LiteralPath $captureExe)) { throw "Missing single-file publish artifact: $captureExe" }
    $iconCopy = Join-Path $testRoot 'published.ico'
    & (Join-Path $ProjectRoot 'tools\ExtractGameIcon.ps1') -SourceExe $captureExe -DestinationIco $iconCopy
    $iconBytes = [IO.File]::ReadAllBytes($iconCopy)
    if ($iconBytes.Length -lt 6 -or [BitConverter]::ToUInt16($iconBytes, 2) -ne 1 -or [BitConverter]::ToUInt16($iconBytes, 4) -lt 2) {
        throw 'Published EXE lacks multi-size icon resources.'
    }

    Write-Host 'NTE capture tests passed: discovery, decoys, logs, retention, exception stack, single-file publish and icon.'
} finally {
    if (Test-Path -LiteralPath $resolvedTestRoot) {
        Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
    }
}
