param(
    [string] $BridgeExe = (Join-Path $PSScriptRoot '..\Release\WhereWindsMeetLaunchBridge.exe')
)

$ErrorActionPreference = 'Stop'
$resolvedBridge = (Resolve-Path -LiteralPath $BridgeExe).Path
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ("WhereWindsMeetLaunchBridge-" + [Guid]::NewGuid().ToString('N'))

function New-EmptyFile([string] $Path) {
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path)) | Out-Null
    [IO.File]::WriteAllBytes($Path, [byte[]]::new(0))
}

function Invoke-DryRun(
    [string] $CaseRoot,
    [string[]] $ExtraArguments = @(),
    [string] $BridgeRelativeDirectory = ''
) {
    $bridgeDirectory = if ($BridgeRelativeDirectory) { Join-Path $CaseRoot $BridgeRelativeDirectory } else { $CaseRoot }
    [IO.Directory]::CreateDirectory($bridgeDirectory) | Out-Null
    $caseBridge = Join-Path $bridgeDirectory 'WhereWindsMeetLaunchBridge.exe'
    [IO.File]::Copy($resolvedBridge, $caseBridge)
    $startInfo = [Diagnostics.ProcessStartInfo]::new($caseBridge)
    $startInfo.ArgumentList.Add('--dry-run')
    $startInfo.ArgumentList.Add('--no-dialog')
    foreach ($argument in $ExtraArguments) {
        $startInfo.ArgumentList.Add($argument)
    }
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $process = [Diagnostics.Process]::Start($startInfo)
    try {
        $standardOutput = $process.StandardOutput.ReadToEnd()
        $standardError = $process.StandardError.ReadToEnd()
        $process.WaitForExit()
        return [PSCustomObject]@{
            ExitCode = $process.ExitCode
            StandardOutput = $standardOutput
            StandardError = $standardError
        }
    }
    finally {
        $process.Dispose()
    }
}

function Assert-Success([string] $Name, $Result, [string] $ExpectedText) {
    if ($Result.ExitCode -ne 0) {
        throw "$Name discovery failed with exit code $($Result.ExitCode): $($Result.StandardError)"
    }
    if (-not $Result.StandardOutput.Contains($ExpectedText, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Name discovery did not select $ExpectedText. Output: $($Result.StandardOutput)"
    }
}

function Assert-Log([string] $Name, [string] $CaseRoot, [string] $BridgeRelativeDirectory = '') {
    $bridgeDirectory = if ($BridgeRelativeDirectory) { Join-Path $CaseRoot $BridgeRelativeDirectory } else { $CaseRoot }
    $logPath = Join-Path $bridgeDirectory 'WhereWindsMeetLaunchBridge-log\where-winds-meet-launch-bridge.log'
    if (-not [IO.File]::Exists($logPath)) {
        throw "$Name did not create the expected log file: $logPath"
    }
}

$cases = @(
    @{ Name = 'standard'; RelativeExe = 'yysls_medium\Engine\Binaries\Win64r\yysls.exe'; Expected = 'yysls_medium' },
    @{ Name = 'fast'; RelativeExe = 'yysls_fast\Engine\Binaries\Win64rh\yysls.exe'; Expected = 'yysls_fast' },
    @{ Name = 'relocated'; RelativeExe = 'GameData\2027.1\Runtime\Shipping\yysls.exe'; Expected = 'GameData' }
)

try {
    foreach ($case in $cases) {
        $caseRoot = Join-Path $testRoot $case.Name
        New-EmptyFile (Join-Path $caseRoot $case.RelativeExe)
        $result = Invoke-DryRun $caseRoot
        Assert-Success $case.Name $result $case.Expected
        Assert-Log $case.Name $caseRoot
        Write-Output "PASS $($case.Name): $($case.RelativeExe)"
    }

    $filterRoot = Join-Path $testRoot 'patch-filter'
    New-EmptyFile (Join-Path $filterRoot 'LocalData\Patch\BinPatch\Engine\Binaries\Win64r\yysls.exe')
    New-EmptyFile (Join-Path $filterRoot 'Live\Current\Client\yysls.exe')
    $filterResult = Invoke-DryRun $filterRoot
    Assert-Success 'patch-filter' $filterResult 'Live\Current\Client\yysls.exe'
    if ($filterResult.StandardOutput.Contains('BinPatch', [StringComparison]::OrdinalIgnoreCase)) {
        throw "patch-filter selected an update cache: $($filterResult.StandardOutput)"
    }
    Write-Output 'PASS patch-filter: update cache excluded'

    $renamedRoot = Join-Path $testRoot 'renamed'
    New-EmptyFile (Join-Path $renamedRoot 'Arbitrary\NewClient\future-yysls.exe')
    $configuration = @'
{
  "executableNames": ["future-yysls.exe"],
  "launchArguments": "--future-launch-mode",
  "preferredPathKeywords": ["NewClient"],
  "maxSearchDepth": 16
}
'@
    [IO.File]::WriteAllText(
        (Join-Path $renamedRoot 'WhereWindsMeetLaunchBridge.json'),
        $configuration,
        [Text.UTF8Encoding]::new($false))
    $renamedResult = Invoke-DryRun $renamedRoot
    Assert-Success 'renamed' $renamedResult 'future-yysls.exe'
    if (-not $renamedResult.StandardOutput.Contains('--future-launch-mode', [StringComparison]::Ordinal)) {
        throw "renamed did not apply configured launch arguments: $($renamedResult.StandardOutput)"
    }
    Write-Output 'PASS renamed: configurable executable name and launch arguments'

    $steamCommandRoot = Join-Path $testRoot 'steam-command-tail'
    New-EmptyFile (Join-Path $steamCommandRoot 'Live\yysls.exe')
    $steamCommandResult = Invoke-DryRun $steamCommandRoot @(
        'D:\STEAM\steamapps\common\Where Winds Meet\Engine\Binaries\Win64r\wwm.exe',
        '--install-root',
        'C:\SteamTailMustNotOverrideBridgeOptions')
    Assert-Success 'steam-command-tail' $steamCommandResult 'Live\yysls.exe'
    $steamCommandLog = [IO.File]::ReadAllText(
        (Join-Path $steamCommandRoot 'WhereWindsMeetLaunchBridge-log\where-winds-meet-launch-bridge.log'))
    if (-not $steamCommandLog.Contains('steamCommandTailIgnored=True', [StringComparison]::Ordinal)) {
        throw "steam-command-tail was not recorded as ignored: $steamCommandLog"
    }
    Write-Output 'PASS steam-command-tail: Steam-expanded original command ignored'

    $unknownRoot = Join-Path $testRoot 'unknown-arguments'
    New-EmptyFile (Join-Path $unknownRoot 'Live\yysls.exe')
    $unknownResult = Invoke-DryRun $unknownRoot @('--future-option', '--mystery-flag=enabled', '--variant', '--token=should-not-appear')
    Assert-Success 'unknown-arguments' $unknownResult 'Live\yysls.exe'
    $unknownLog = [IO.File]::ReadAllText(
        (Join-Path $unknownRoot 'WhereWindsMeetLaunchBridge-log\where-winds-meet-launch-bridge.log'))
    foreach ($expectedArgument in @('--future-option', '--mystery-flag=enabled', '--variant')) {
        if (-not $unknownLog.Contains($expectedArgument, [StringComparison]::Ordinal)) {
            throw "unknown-arguments did not log ignored argument ${expectedArgument}: $unknownLog"
        }
    }
    if (-not $unknownLog.Contains('--token=<redacted>', [StringComparison]::Ordinal) -or
        $unknownLog.Contains('should-not-appear', [StringComparison]::Ordinal)) {
        throw "unknown-arguments did not redact a sensitive ignored argument: $unknownLog"
    }
    Write-Output 'PASS unknown-arguments: ignored without failure and recorded in log'

    $nestedRoot = Join-Path $testRoot 'nested-bridge'
    New-EmptyFile (Join-Path $nestedRoot 'GameData\Runtime\Win64r\yysls.exe')
    $nestedBridgeDirectory = 'Utilities\Steam\Bridge\Deep01\Deep02\Deep03\Deep04\Deep05\Deep06\Deep07\Deep08\Deep09\Deep10\Deep11\Deep12'
    $nestedResult = Invoke-DryRun $nestedRoot @() $nestedBridgeDirectory
    Assert-Success 'nested-bridge' $nestedResult 'GameData\Runtime\Win64r\yysls.exe'
    Assert-Log 'nested-bridge' $nestedRoot $nestedBridgeDirectory
    $nestedLog = [IO.File]::ReadAllText(
        (Join-Path $nestedRoot "$nestedBridgeDirectory\WhereWindsMeetLaunchBridge-log\where-winds-meet-launch-bridge.log"))
    if (-not $nestedLog.Contains('Discovery scopes:', [StringComparison]::Ordinal) -or
        -not $nestedLog.Contains($nestedRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "nested-bridge did not record upward discovery scopes: $nestedLog"
    }
    Write-Output 'PASS nested-bridge: bridge discovered game from an arbitrary nested directory'

    $retentionRoot = Join-Path $testRoot 'retention'
    New-EmptyFile (Join-Path $retentionRoot 'Live\yysls.exe')
    $retentionLog = Join-Path $retentionRoot 'WhereWindsMeetLaunchBridge-log\where-winds-meet-launch-bridge.log'
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($retentionLog)) | Out-Null
    $oldTimestamp = [DateTimeOffset]::UtcNow.AddDays(-8).ToString('O')
    $currentTimestamp = [DateTimeOffset]::UtcNow.ToString('O')
    [IO.File]::WriteAllText(
        $retentionLog,
        "[$oldTimestamp] old-entry$([Environment]::NewLine)[$currentTimestamp] current-entry$([Environment]::NewLine)",
        [Text.UTF8Encoding]::new($false))
    $retentionResult = Invoke-DryRun $retentionRoot
    Assert-Success 'retention' $retentionResult 'Live\yysls.exe'
    $retainedLog = [IO.File]::ReadAllText($retentionLog)
    if ($retainedLog.Contains('old-entry', [StringComparison]::Ordinal) -or
        -not $retainedLog.Contains('current-entry', [StringComparison]::Ordinal)) {
        throw "retention did not remove only expired entries: $retainedLog"
    }
    Write-Output 'PASS retention: expired log entry removed and current entry retained'

    $errorRoot = Join-Path $testRoot 'error-stack'
    New-EmptyFile (Join-Path $errorRoot 'Live\yysls.exe')
    [IO.File]::WriteAllText(
        (Join-Path $errorRoot 'WhereWindsMeetLaunchBridge.json'),
        '{ invalid-json',
        [Text.UTF8Encoding]::new($false))
    $errorResult = Invoke-DryRun $errorRoot
    if ($errorResult.ExitCode -eq 0) {
        throw 'error-stack unexpectedly succeeded with invalid JSON'
    }
    $errorLog = [IO.File]::ReadAllText(
        (Join-Path $errorRoot 'WhereWindsMeetLaunchBridge-log\where-winds-meet-launch-bridge.log'))
    if (-not $errorLog.Contains('JsonParseError', [StringComparison]::Ordinal) -or
        -not $errorLog.Contains(' at ', [StringComparison]::Ordinal)) {
        throw "error-stack log did not include exception type and stack: $errorLog"
    }
    Write-Output 'PASS error-stack: exception type, message and stack logged'
}
finally {
    if ([IO.Directory]::Exists($testRoot)) {
        [IO.Directory]::Delete($testRoot, $true)
    }
}
