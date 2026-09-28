param(
    [string] $GameExe
)

$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$sourceRoot = Join-Path $projectRoot 'src'
$projectFile = Join-Path $sourceRoot 'InfinityNikkiLaunchBridge.csproj'
$iconFile = Join-Path $sourceRoot 'InfinityNikki.ico'
$outputRoot = Join-Path $projectRoot 'Release'

if ($GameExe) {
    $resolvedGameExe = (Resolve-Path -LiteralPath $GameExe).Path
    & (Join-Path $projectRoot 'tools\ExtractGameIcon.ps1') `
        -SourceExe $resolvedGameExe `
        -DestinationIco $iconFile
}

$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.dotnet'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

dotnet publish $projectFile `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:PublishTrimmed=false `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    --output $outputRoot

if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

Get-Item -LiteralPath (Join-Path $outputRoot 'InfinityNikkiLaunchBridge.exe')
