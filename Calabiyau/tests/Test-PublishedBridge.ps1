param(
    [string] $BridgeExe = (Join-Path $PSScriptRoot '..\Release\CalabiyauLaunchBridge.exe')
)

$ErrorActionPreference = 'Stop'
$resolvedBridge = (Resolve-Path -LiteralPath $BridgeExe).Path
$bridgeDirectory = Split-Path -Parent $resolvedBridge
$bridgeStem = [IO.Path]::GetFileNameWithoutExtension($resolvedBridge)
foreach ($unexpectedSidecar in @(
    (Join-Path $bridgeDirectory "$bridgeStem.dll"),
    (Join-Path $bridgeDirectory "$bridgeStem.deps.json"),
    (Join-Path $bridgeDirectory "$bridgeStem.runtimeconfig.json")
)) {
    if ([IO.File]::Exists($unexpectedSidecar)) {
        throw "Native single-file bridge has an unexpected .NET sidecar: $unexpectedSidecar"
    }
}
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ("CalabiyauLaunchBridge-" + [Guid]::NewGuid().ToString('N'))
try {
    $gameDirectory = Join-Path $testRoot 'Calabiyau\CalabiyauGame\PM\Binaries\Win64'
    $wrapperPath = Join-Path $testRoot 'Calabiyau\CalabiyauGame\Calabiyau.exe'
    $logDirectory = Join-Path $testRoot 'log'
    [IO.Directory]::CreateDirectory($gameDirectory) | Out-Null
    [IO.Directory]::CreateDirectory((Join-Path $testRoot 'UpdateGUI')) | Out-Null
    [IO.Directory]::CreateDirectory($logDirectory) | Out-Null
    [IO.File]::WriteAllBytes((Join-Path $testRoot 'CalabiYau.exe'), [byte[]]::new(0))
    [IO.File]::WriteAllText((Join-Path $testRoot 'config.ini'), 'GamePath=Calabiyau/CalabiyauGame')
    [IO.File]::WriteAllBytes($wrapperPath, [byte[]]::new(0))
    [IO.File]::WriteAllBytes((Join-Path $gameDirectory 'Calabiyau-Win64-Shipping.exe'), [byte[]]::new(0))
    $logLine = '10-10 12:00:00.000: Debug/(0): start game: "' + $wrapperPath.Replace('\', '/') + '"  args: ("-Channel=fixture", "-dx12", "-Lanuch=IdsCn", "-AdsPath=fixture")'
    [IO.File]::WriteAllText((Join-Path $logDirectory 'launcher.log'), $logLine, [Text.Encoding]::GetEncoding(936))
    # The release attachment keeps its versioned file name. Users can copy it
    # anywhere below the game installation root without renaming or configuration.
    $bridgeDirectory = Join-Path $testRoot 'Calabiyau\CalabiyauGame\PM\BridgeTools\Nested'
    [IO.Directory]::CreateDirectory($bridgeDirectory) | Out-Null
    $caseBridge = Join-Path $bridgeDirectory 'CalabiyauLaunchBridge_v1.3.exe'
    [IO.File]::Copy($resolvedBridge, $caseBridge)

    $process = [Diagnostics.Process]::Start($caseBridge, '--unknown-option --validate --no-dialog')
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) { throw "Published bridge validation failed with exit code $($process.ExitCode)." }

    $logPath = Join-Path $bridgeDirectory 'CalabiyauLaunchBridge-log\CalabiyauLaunchBridge.log'
    if (-not [IO.File]::Exists($logPath)) { throw "Published bridge did not create the expected log: $logPath" }
    $log = [IO.File]::ReadAllText($logPath)
    foreach ($expected in @(
        'version=1.3.0.0',
        'processArchitecture=X64',
        'CalabiYau.exe',
        'launch_record.selected',
        'sdkLauncher=',
        'directEntry=',
        'Calabiyau-Win64-Shipping.exe',
        '[Event=command.arguments_ignored]',
        '--unknown-option',
        'source=bridge-ancestor',
        '[Event=validation.success]',
        'exitCode=0'
    )) {
        if ($log.IndexOf($expected, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
            throw "Published bridge log is missing '$expected'."
        }
    }

    $failure = [Diagnostics.Process]::Start($caseBridge, '--startup-timeout-seconds invalid --no-dialog')
    $failure.WaitForExit()
    if ($failure.ExitCode -eq 0) { throw 'Published bridge unexpectedly accepted an unknown option.' }
    $failureLog = [IO.File]::ReadAllText($logPath)
    foreach ($expectedFailureText in @('BridgeException', '--startup-timeout-seconds 必须为', ' at ')) {
        if ($failureLog.IndexOf($expectedFailureText, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
            throw "Published bridge error log is missing '$expectedFailureText'."
        }
    }
    Write-Output 'PASS portable published bridge: arbitrary in-install placement, ignored unknown arguments, versioned filename, exact paths, native architecture, log path, and validation exit code'
}
finally {
    if ([IO.Directory]::Exists($testRoot)) { [IO.Directory]::Delete($testRoot, $true) }
}
