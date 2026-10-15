param([string] $ProjectRoot = (Split-Path $PSScriptRoot -Parent), [string] $PublishedDirectory)
$ErrorActionPreference = 'Stop'
$ProjectRoot = (Resolve-Path -LiteralPath $ProjectRoot).Path
if (!$PublishedDirectory) { $PublishedDirectory = Join-Path $ProjectRoot 'Release' }
$exe = Join-Path $PublishedDirectory 'ArknightsEndfieldLaunchBridge.exe'
$native = Join-Path $ProjectRoot 'build/native-x64'
& (Join-Path $native 'Release/ArknightsEndfieldLaunchBridgeTests.exe') (Join-Path $native 'fixture/Release/Endfield.exe')
if ($LASTEXITCODE -ne 0) { throw 'Native regression failed.' }
if (@(Get-ChildItem -LiteralPath $PublishedDirectory -File).Count -ne 1) { throw 'Expected one standalone EXE.' }
if ((Get-Item -LiteralPath $exe).VersionInfo.FileVersion -ne '1.0.0') { throw 'Version mismatch.' }
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
$toolset = (Get-Content (Join-Path $vs 'VC/Auxiliary/Build/Microsoft.VCToolsVersion.default.txt')).Trim()
$dumpbin = Join-Path $vs "VC/Tools/MSVC/$toolset/bin/Hostx64/x64/dumpbin.exe"
$dependencies = (& $dumpbin /dependents $exe) -join "`n"
if ($LASTEXITCODE -ne 0) { throw 'Dependency inspection failed.' }
$dlls = [regex]::Matches($dependencies, '(?im)^\s+([\w.-]+\.dll)\s*$') | ForEach-Object { $_.Groups[1].Value.ToLowerInvariant() }
$allowed = @('shell32.dll','ole32.dll','dbghelp.dll','version.dll','advapi32.dll','kernel32.dll','user32.dll','gdi32.dll')
if (!$dlls -or @($dlls | Where-Object { $_ -notin $allowed }).Count) { throw 'Unexpected native dependency.' }
$testDirectory = Join-Path $native ('published-check-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testDirectory -Force | Out-Null
$copy = Join-Path $testDirectory 'ArknightsEndfieldLaunchBridge.exe'
Copy-Item -LiteralPath $exe -Destination $copy
$layout = Join-Path $testDirectory 'layout'
$game = Join-Path $layout 'Game/ChangedVersion/Endfield.exe'
$launcher = Join-Path $layout 'Official/Launcher.exe'
New-Item -ItemType Directory -Path (Split-Path $game -Parent),(Split-Path $launcher -Parent) -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $native 'fixture/Release/Endfield.exe') -Destination $game
Copy-Item -LiteralPath $exe -Destination $launcher
$config = Join-Path $testDirectory 'fixture.ini'
[IO.File]::WriteAllText($config, "[Paths]`r`nLauncherProductContains=ArknightsEndfieldLaunchBridge`r`n", [Text.Encoding]::Unicode)
$logDirectory = Join-Path $testDirectory 'ArknightsEndfieldLaunchBridge-log'
New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
$log = Join-Path $logDirectory 'ArknightsEndfieldLaunchBridge.log'
$old = [DateTimeOffset]::Now.AddDays(-7).AddHours(-1).ToString('yyyy-MM-ddTHH:mm:ss.fffzzz')
$recent = [DateTimeOffset]::Now.AddDays(-7).AddHours(1).ToString('yyyy-MM-ddTHH:mm:ss.fffzzz')
[IO.File]::WriteAllText($log, "[$old] old-record`r`n[$recent] keep-record`r`n", [Text.UTF8Encoding]::new($false))
$p = Start-Process -FilePath $copy -ArgumentList @('--validate','--no-dialog','--config',('"'+$config+'"'),'--install-root',('"'+$layout+'"'),'--','steam://run/1') -PassThru -Wait -WindowStyle Hidden
if ($p.ExitCode -ne 0) { throw 'Published discovery validation failed.' }
$text = [IO.File]::ReadAllText($log)
if ($text -match 'old-record' -or $text -notmatch 'keep-record') { throw 'Exact seven-day log retention failed.' }
if ($text -notmatch 'validation.success' -or $text -notmatch 'implementation=C\+\+20' -or $text -notmatch 'ignoredArgumentCount=2') { throw 'Native configuration or Steam-tail check failed.' }
$p = Start-Process -FilePath $copy -ArgumentList @('--validate','--no-dialog','--install-root',('"'+(Join-Path $testDirectory 'missing')+'"')) -PassThru -Wait -WindowStyle Hidden
if ($p.ExitCode -ne 3) { throw 'Missing root error code mismatch.' }
$text = [IO.File]::ReadAllText($log)
if ($text -notmatch 'bridge.failure' -or $text -notmatch 'type=' -or $text -notmatch 'at (0x|[A-Za-z_])') { throw 'Native exception stack missing.' }
$icon = Join-Path $testDirectory 'published.ico'
& (Join-Path $ProjectRoot 'tools/ExtractGameIcon.ps1') -SourceExe $copy -DestinationIco $icon
if ([BitConverter]::ToUInt16([IO.File]::ReadAllBytes($icon),4) -lt 2) { throw 'Multi-size icon missing.' }
if ((Get-FileHash -LiteralPath $icon).Hash -ne (Get-FileHash -LiteralPath (Join-Path $ProjectRoot 'tools/endfield.ico')).Hash) { throw 'Official icon mismatch.' }
Write-Host 'Native artifact checks passed: one EXE, system DLLs only, version, icon, discovery, INI, Steam tail, log location, seven-day retention and error stack.'
Get-FileHash -LiteralPath $exe -Algorithm SHA256
