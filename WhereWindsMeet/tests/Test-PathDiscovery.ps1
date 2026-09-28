param(
    [string] $BridgeExe = (Join-Path $PSScriptRoot '..\Release\WhereWindsMeetLaunchBridge.exe')
)

$ErrorActionPreference = 'Stop'
$resolvedBridge = (Resolve-Path -LiteralPath $BridgeExe).Path
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ("WhereWindsMeetLaunchBridge-" + [Guid]::NewGuid().ToString('N'))
$cases = @(
    @{ Name = 'standard'; Launcher = 'launcher.exe'; GameFolder = 'yysls_medium'; Variant = 'Win64r' },
    @{ Name = 'fast'; Launcher = 'launcher.exe.lnk'; GameFolder = 'yysls_fast'; Variant = 'Win64rh' },
    @{ Name = 'future'; Launcher = 'launcher.exe'; GameFolder = 'yysls_future'; Variant = 'Win64r' }
)

try {
    foreach ($case in $cases) {
        $caseRoot = Join-Path $testRoot $case.Name
        $gameDirectory = Join-Path $caseRoot ($case.GameFolder + '\Engine\Binaries\' + $case.Variant)
        [IO.Directory]::CreateDirectory($gameDirectory) | Out-Null
        [IO.File]::WriteAllBytes((Join-Path $caseRoot $case.Launcher), [byte[]]::new(0))
        [IO.File]::WriteAllBytes((Join-Path $gameDirectory 'yysls.exe'), [byte[]]::new(0))
        $caseBridge = Join-Path $caseRoot 'WhereWindsMeetLaunchBridge.exe'
        [IO.File]::Copy($resolvedBridge, $caseBridge)

        $startInfo = [Diagnostics.ProcessStartInfo]::new($caseBridge)
        $startInfo.ArgumentList.Add('--dry-run')
        $startInfo.ArgumentList.Add('--no-dialog')
        $startInfo.UseShellExecute = $false
        $startInfo.RedirectStandardOutput = $true
        $startInfo.RedirectStandardError = $true
        $process = [Diagnostics.Process]::Start($startInfo)
        $standardOutput = $process.StandardOutput.ReadToEnd()
        $standardError = $process.StandardError.ReadToEnd()
        $process.WaitForExit()

        if ($process.ExitCode -ne 0) {
            throw "$($case.Name) discovery failed with exit code $($process.ExitCode): $standardError"
        }
        if (-not $standardOutput.Contains($case.GameFolder, [StringComparison]::OrdinalIgnoreCase)) {
            throw "$($case.Name) discovery did not select $($case.GameFolder). Output: $standardOutput"
        }

        $logPath = Join-Path $caseRoot 'WhereWindsMeetLaunchBridge-log\where-winds-meet-launch-bridge.log'
        if (-not [IO.File]::Exists($logPath)) {
            throw "$($case.Name) did not create the expected log file: $logPath"
        }

        Write-Output "PASS $($case.Name): $($case.GameFolder) / $($case.Variant)"
    }
}
finally {
    if ([IO.Directory]::Exists($testRoot)) {
        [IO.Directory]::Delete($testRoot, $true)
    }
}
