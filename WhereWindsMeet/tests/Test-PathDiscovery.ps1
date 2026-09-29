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

function Invoke-DryRun([string] $CaseRoot) {
    $caseBridge = Join-Path $CaseRoot 'WhereWindsMeetLaunchBridge.exe'
    [IO.File]::Copy($resolvedBridge, $caseBridge)
    $startInfo = [Diagnostics.ProcessStartInfo]::new($caseBridge)
    $startInfo.ArgumentList.Add('--dry-run')
    $startInfo.ArgumentList.Add('--no-dialog')
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

function Assert-Log([string] $Name, [string] $CaseRoot) {
    $logPath = Join-Path $CaseRoot 'WhereWindsMeetLaunchBridge-log\where-winds-meet-launch-bridge.log'
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
    if (-not $errorLog.Contains('System.Text.Json.JsonException', [StringComparison]::Ordinal) -or
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
