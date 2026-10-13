param(
    [string] $GameExe
)

$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$sourceRoot = Join-Path $projectRoot 'src'
$projectFile = Join-Path $sourceRoot 'CalabiyauLaunchBridge.csproj'
$iconFile = Join-Path $sourceRoot 'Calabiyau.ico'
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
    -p:PublishAot=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    --output $outputRoot

if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$publishedExe = Join-Path $outputRoot 'CalabiyauLaunchBridge.exe'
$productVersion = (Get-Item -LiteralPath $publishedExe).VersionInfo.ProductVersion.Split('+')[0]
$releaseVersion = if ($productVersion.EndsWith('.0')) {
    $productVersion.Substring(0, $productVersion.Length - 2)
} else {
    $productVersion
}
$versionedExe = Join-Path $outputRoot "CalabiyauLaunchBridge_v$releaseVersion.exe"
Copy-Item -LiteralPath $publishedExe -Destination $versionedExe -Force
$hash = (Get-FileHash -LiteralPath $versionedExe -Algorithm SHA256).Hash
$checksumPath = "$versionedExe.sha256"
[IO.File]::WriteAllText(
    $checksumPath,
    "$hash  $([IO.Path]::GetFileName($versionedExe))`n",
    [Text.UTF8Encoding]::new($false))
Get-Item -LiteralPath $publishedExe, $versionedExe, $checksumPath
