param([string] $ProjectRoot = (Split-Path $PSScriptRoot -Parent))

$ErrorActionPreference = 'Stop'
$bridgeName = 'NevernessToEvernessLaunchBridge'
$bridgeExe = Join-Path $ProjectRoot "Release\$bridgeName.exe"
$testHost = Join-Path $ProjectRoot "build\native-x64\Release\${bridgeName}TestHost.exe"
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ("nte-bridge-test-" + [Guid]::NewGuid().ToString('N'))
$resolvedTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$resolvedTest = [IO.Path]::GetFullPath($testRoot)
if (-not $resolvedTest.StartsWith(($resolvedTemp.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar), [StringComparison]::OrdinalIgnoreCase) -or
    -not (Split-Path $resolvedTest -Leaf).StartsWith('nte-bridge-test-', [StringComparison]::Ordinal)) {
    throw "Test directory is outside the system temporary directory: $resolvedTest"
}

function New-Stub([string] $Path) {
    [void](New-Item -ItemType Directory -Path (Split-Path $Path -Parent) -Force)
    [IO.File]::WriteAllBytes($Path, [byte[]](1, 2, 3))
}

function Invoke-Bridge([string[]] $Arguments) {
    $process = Start-Process -FilePath $testHost -ArgumentList $Arguments -Wait -PassThru
    return $process.ExitCode
}

try {
    [void](New-Item -ItemType Directory -Path $testRoot)
    $standard = Join-Path $testRoot 'standard'
    New-Stub (Join-Path $standard 'NTELauncher\NTEGame.exe')
    New-Stub (Join-Path $standard 'Client\WindowsNoEditor\HT\Binaries\Win64\HTGame.exe')
    if ((Invoke-Bridge @('--validate', '--no-dialog', '--install-root', $standard)) -ne 0) { throw 'Standard installation discovery failed.' }
    if ((Invoke-Bridge @('--validate', '--no-dialog', '--install-root', $standard, (Join-Path $standard 'OriginalSteamCommand.exe'), '-steam-flag', 'steam-value')) -ne 0) { throw 'Steam command tail isolation failed.' }
    if ((Invoke-Bridge @('--validate', '--no-dialog', '--install-root', $standard, '--', (Join-Path $standard 'OriginalSteamCommand.exe'), '--steam-owned-option')) -ne 0) { throw 'Explicit Steam command separator handling failed.' }
    if ((Invoke-Bridge @('--validate', '--no-dialog', '--install-root', $standard, '--unknown-bridge-option')) -eq 0) { throw 'Unknown bridge options must still fail.' }

    $compact = Join-Path $testRoot 'compact'
    New-Stub (Join-Path $compact 'Runtime\NTEGame.exe')
    New-Stub (Join-Path $compact 'Game\Bin\HTGame.exe')
    if ((Invoke-Bridge @('--validate', '--no-dialog', '--install-root', $compact)) -ne 0) { throw 'Compact installation discovery failed.' }

    $decoy = Join-Path $testRoot 'decoy'
    New-Stub (Join-Path $decoy 'Backup\NTEGame.exe')
    New-Stub (Join-Path $decoy 'Backup\HTGame.exe')
    $decoyExit = Invoke-Bridge @('--validate', '--no-dialog', '--install-root', $decoy)
    if ($decoyExit -eq 0) { throw 'Backup decoys must not pass validation.' }

    $custom = Join-Path $testRoot 'custom'
    New-Stub (Join-Path $custom 'A\CustomLauncher.exe')
    New-Stub (Join-Path $custom 'B\CustomGame.exe')
    $config = Join-Path $testRoot 'custom.ini'
    [IO.File]::WriteAllText($config, "[Paths]`r`nLauncherFileName=CustomLauncher.exe`r`nGameFileName=CustomGame.exe`r`nLauncherCandidates=A/CustomLauncher.exe`r`nGameCandidates=B/CustomGame.exe`r`n")
    if ((Invoke-Bridge @('--validate', '--no-dialog', '--install-root', $custom, '--config', $config)) -ne 0) { throw 'Config override validation failed.' }

    $logFile = Join-Path (Split-Path $testHost -Parent) "$bridgeName-log\$bridgeName.log"
    $old = "[$((Get-Date).AddDays(-9).ToString('o'))] old-entry"
    $recent = "[$((Get-Date).AddDays(-1).ToString('o'))] recent-entry"
    [IO.File]::WriteAllLines($logFile, [string[]]@($old, $recent))
    if ((Invoke-Bridge @('--validate', '--no-dialog', '--install-root', $standard)) -ne 0) { throw 'Retention validation run failed.' }
    $log = [IO.File]::ReadAllText($logFile)
    if ($log.Contains('old-entry') -or -not $log.Contains('recent-entry')) { throw 'Seven-day retention failed.' }

    $missingExit = Invoke-Bridge @('--validate', '--no-dialog', '--install-root', (Join-Path $testRoot 'missing'))
    if ($missingExit -eq 0) { throw 'Missing installation root must fail.' }
    $log = [IO.File]::ReadAllText($logFile)
    if ($log -notmatch 'bridge.failure' -or $log -notmatch ' at ') { throw 'Native error log lacks the failure event or stack trace.' }

    $iconCopy = Join-Path $testRoot 'bridge.ico'
    & (Join-Path $ProjectRoot 'tools\ExtractGameIcon.ps1') -SourceExe $bridgeExe -DestinationIco $iconCopy
    $bytes = [IO.File]::ReadAllBytes($iconCopy)
    if ($bytes.Length -lt 6 -or [BitConverter]::ToUInt16($bytes, 4) -lt 2) { throw 'Published EXE lacks a multi-size icon.' }
    if ((Get-ChildItem (Split-Path $bridgeExe -Parent) -File).Count -ne 1) { throw 'Release must contain only the single-file EXE.' }

    Write-Host 'NTE bridge tests passed: discovery, Steam tail isolation, strict bridge options, exclusions, config overrides, logs, retention, single-file publish and icon.'
} finally {
    if (Test-Path -LiteralPath $resolvedTest) { Remove-Item -LiteralPath $resolvedTest -Recurse -Force }
}
