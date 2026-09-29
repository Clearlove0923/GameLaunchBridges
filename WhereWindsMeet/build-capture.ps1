param(
    [string] $OutputDirectory = (Join-Path $PSScriptRoot 'Capture')
)

$ErrorActionPreference = 'Stop'
$projectFile = Join-Path $PSScriptRoot 'tools\LaunchCapture\WhereWindsMeetLaunchCapture.csproj'
$dotnetHome = Join-Path $PSScriptRoot '.dotnet-capture'

$env:DOTNET_CLI_HOME = $dotnetHome
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:NUGET_PACKAGES = Join-Path $PSScriptRoot '.dotnet\.nuget\packages'

dotnet publish $projectFile `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    -p:RestoreIgnoreFailedSources=true `
    -p:PublishSingleFile=true `
    -p:PublishTrimmed=false `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    --output $OutputDirectory

if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

Get-Item -LiteralPath (Join-Path $OutputDirectory 'WhereWindsMeetLaunchCapture.exe')
