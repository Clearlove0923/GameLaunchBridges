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
    return Invoke-BridgeExecutable -Executable $testHost -Arguments $Arguments
}

function Invoke-BridgeExecutable([string] $Executable, [string[]] $Arguments) {
    $process = Start-Process -FilePath $Executable -ArgumentList $Arguments -Wait -PassThru
    return $process.ExitCode
}

try {
    & (Join-Path $ProjectRoot 'build\native-x64\Release\NevernessToEvernessRecoveryTests.exe')
    if ($LASTEXITCODE -ne 0) { throw "Launcher failure recovery tests failed: $LASTEXITCODE" }
    [void](New-Item -ItemType Directory -Path $testRoot)
    $standard = Join-Path $testRoot 'standard'
    New-Stub (Join-Path $standard 'NTELauncher\NTEGame.exe')
    New-Stub (Join-Path $standard 'Client\WindowsNoEditor\HT\Binaries\Win64\HTGame.exe')
    if ((Invoke-Bridge @('--validate', '--no-dialog', '--install-root', $standard)) -ne 0) { throw 'Standard installation discovery failed.' }
    if ((Invoke-Bridge @('--validate', '--no-dialog', '--install-root', $standard, (Join-Path $standard 'OriginalSteamCommand.exe'), '-steam-flag', 'steam-value')) -ne 0) { throw 'Steam command tail isolation failed.' }
    if ((Invoke-Bridge @('--validate', '--no-dialog', '--install-root', $standard, '--', (Join-Path $standard 'OriginalSteamCommand.exe'), '--steam-owned-option')) -ne 0) { throw 'Explicit Steam command separator handling failed.' }
    if ((Invoke-Bridge @('--unknown-bridge-option', '--custom-value=visible', '--access-token=secret', '--session', 'private-session', '--validate', '--no-dialog', '--install-root', $standard)) -ne 0) { throw 'Unknown bridge options must be ignored.' }
    $logFile = Join-Path (Split-Path $testHost -Parent) "$bridgeName-log\$bridgeName.log"
    $ignoredLog = [IO.File]::ReadAllText($logFile)
    if ($ignoredLog -notmatch 'arguments\.ignored' -or $ignoredLog -notmatch '--unknown-bridge-option' -or $ignoredLog -notmatch 'custom-value=visible' -or $ignoredLog -notmatch 'access-token=<redacted>' -or $ignoredLog -match 'access-token=secret' -or $ignoredLog -match 'private-session') { throw 'Ignored argument logging or selective redaction failed.' }

    $nestedBridgeDirectory = Join-Path $standard 'Client\WindowsNoEditor\HT\Binaries\Win64\Tools\Steam\Nested\Bridge'
    [void](New-Item -ItemType Directory -Path $nestedBridgeDirectory -Force)
    $nestedBridge = Join-Path $nestedBridgeDirectory (Split-Path $testHost -Leaf)
    Copy-Item -LiteralPath $testHost -Destination $nestedBridge
    if ((Invoke-BridgeExecutable -Executable $nestedBridge -Arguments @('--validate', '--no-dialog')) -ne 0) { throw 'Discovery from an arbitrary nested installation directory failed.' }

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
    $releaseFiles = Get-ChildItem (Split-Path $bridgeExe -Parent) -File
    $releaseExecutables = @($releaseFiles | Where-Object Extension -eq '.exe')
    $runtimeSidecars = @($releaseFiles | Where-Object Extension -in @('.dll', '.pdb', '.json', '.deps.json', '.runtimeconfig.json'))
    if ($releaseExecutables.Count -ne 1 -or $releaseExecutables[0].Name -ne "$bridgeName.exe" -or $runtimeSidecars.Count -ne 0) { throw 'Release must contain one standalone bridge EXE without runtime sidecars.' }

    Write-Host 'NTE bridge tests passed: nested discovery, Steam tail isolation, ignored argument logging, exclusions, config overrides, logs, retention, single-file publish and icon.'
} finally {
    if (Test-Path -LiteralPath $resolvedTest) { Remove-Item -LiteralPath $resolvedTest -Recurse -Force }
}
