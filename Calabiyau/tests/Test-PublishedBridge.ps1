param(
    [string] $BridgeExe = (Join-Path $PSScriptRoot '..\Release\CalabiyauLaunchBridge.exe')
)

$ErrorActionPreference = 'Stop'
$resolvedBridge = (Resolve-Path -LiteralPath $BridgeExe).Path
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
    $caseBridge = Join-Path $testRoot 'CalabiyauLaunchBridge.exe'
    [IO.File]::Copy($resolvedBridge, $caseBridge)

    $process = [Diagnostics.Process]::Start($caseBridge, '--validate --no-dialog')
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) { throw "Published bridge validation failed with exit code $($process.ExitCode)." }

    $logPath = Join-Path $testRoot 'CalabiyauLaunchBridge-log\CalabiyauLaunchBridge.log'
    if (-not [IO.File]::Exists($logPath)) { throw "Published bridge did not create the expected log: $logPath" }
    $log = [IO.File]::ReadAllText($logPath)
    foreach ($expected in @(
        'version=1.0.0.0',
        'processArchitecture=X64',
        'CalabiYau.exe',
        'launch_record.selected',
        'sdkLauncher=',
        'directEntry=',
        'Calabiyau-Win64-Shipping.exe',
        '[Event=validation.success]',
        'exitCode=0'
    )) {
        if ($log.IndexOf($expected, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
            throw "Published bridge log is missing '$expected'."
        }
    }
    Write-Output 'PASS published bridge: discovery, exact paths, version, architecture, log path, and validation exit code'
}
finally {
    if ([IO.Directory]::Exists($testRoot)) { [IO.Directory]::Delete($testRoot, $true) }
}
