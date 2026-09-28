param(
    [string] $BridgeExe = (Join-Path $PSScriptRoot '..\Release\InfinityNikkiLaunchBridge.exe')
)

$ErrorActionPreference = 'Stop'
$resolvedBridge = (Resolve-Path -LiteralPath $BridgeExe).Path
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ("InfinityNikkiLaunchBridge-" + [Guid]::NewGuid().ToString('N'))

try {
    $versionDirectory = Join-Path $testRoot '9.9.9'
    $gameDirectory = Join-Path $testRoot 'InfinityNikki\X6Game\Binaries\Win64'
    [IO.Directory]::CreateDirectory($versionDirectory) | Out-Null
    [IO.Directory]::CreateDirectory($gameDirectory) | Out-Null
    [IO.File]::WriteAllBytes((Join-Path $testRoot 'launcher.exe'), [byte[]]::new(0))
    [IO.File]::WriteAllBytes((Join-Path $versionDirectory 'xstarter.exe'), [byte[]]::new(0))
    [IO.File]::WriteAllBytes((Join-Path $gameDirectory 'X6Game-Win64-Shipping.exe'), [byte[]]::new(0))
    $caseBridge = Join-Path $testRoot 'InfinityNikkiLaunchBridge.exe'
    [IO.File]::Copy($resolvedBridge, $caseBridge)

    $process = [Diagnostics.Process]::Start($caseBridge, '--validate')
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) {
        throw "Published bridge validation failed with exit code $($process.ExitCode)."
    }

    $logPath = Join-Path $testRoot 'InfinityNikkiLaunchBridge-log\InfinityNikkiLaunchBridge.log'
    if (-not [IO.File]::Exists($logPath)) {
        throw "Published bridge did not create the expected log: $logPath"
    }

    $log = [IO.File]::ReadAllText($logPath)
    foreach ($expected in @(
        'version=1.0.0.0',
        'processArchitecture=X64',
        'starter=',
        'X6Game-Win64-Shipping.exe',
        '[Event=validation.success]',
        'exitCode=0'
    )) {
        if (-not $log.Contains($expected, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Published bridge log is missing '$expected'."
        }
    }

    Write-Output 'PASS published bridge: discovery, exact game path, version, architecture, log path, and validation exit code'
}
finally {
    if ([IO.Directory]::Exists($testRoot)) {
        [IO.Directory]::Delete($testRoot, $true)
    }
}
