param(
    [string] $GameExe,
    [string] $OutputDirectory = (Join-Path $PSScriptRoot 'Capture')
)

$ErrorActionPreference = 'Stop'
$captureProjectFile = Join-Path $PSScriptRoot 'tools\LaunchCapture\NevernessToEvernessLaunchCapture.csproj'
$captureIconFile = Join-Path $PSScriptRoot 'tools\LaunchCapture\nte.ico'

if ($GameExe) {
    $resolvedGameExe = (Resolve-Path -LiteralPath $GameExe).Path
    & (Join-Path $PSScriptRoot 'tools\ExtractGameIcon.ps1') -SourceExe $resolvedGameExe -DestinationIco $captureIconFile
}

if (-not (Test-Path -LiteralPath $captureIconFile)) {
    throw "Missing icon: $captureIconFile. Use -GameExe to specify the official NTELauncher.exe."
}

$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.dotnet-capture'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:NUGET_PACKAGES = Join-Path $PSScriptRoot '.dotnet\.nuget\packages'

dotnet publish $captureProjectFile `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    -p:RestoreIgnoreFailedSources=true `
    -p:PublishSingleFile=true `
    -p:PublishTrimmed=false `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    --output $OutputDirectory

if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Get-Item -LiteralPath (Join-Path $OutputDirectory 'NevernessToEvernessLaunchCapture.exe')
