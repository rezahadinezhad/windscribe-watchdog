# Builds Windscribe Watchdog (see build.ps1), installs it for the current user, registers a task that
# starts it again if it ever stops, and starts it. The app adds its own "start with Windows" entry
# when it runs. Safe to re-run (it replaces the old copy). No admin rights needed.
#   powershell -ExecutionPolicy Bypass -File install.ps1
$ErrorActionPreference = 'Stop'
$dir = Join-Path $env:LOCALAPPDATA 'Programs\WindscribeWatchdog'
$exe = Join-Path $dir 'WindscribeWatchdog.exe'

New-Item -ItemType Directory -Force $dir | Out-Null
foreach ($p in Get-Process WindscribeWatchdog -ErrorAction SilentlyContinue) { $p.Kill(); $p.WaitForExit(5000) | Out-Null }

& (Join-Path $PSScriptRoot 'build.ps1') -Out $exe | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'uninstall.ps1') $dir -Force
# Older versions kept a copy of the source here; it isn't needed any more.
'WindscribeWatchdog.cs', 'install.ps1' | ForEach-Object { Remove-Item (Join-Path $dir $_) -ErrorAction SilentlyContinue }

# Every 5 minutes, start the watchdog if it isn't running (unless you chose Quit).
# No time limit, runs on battery too, and at normal priority (tasks default to a low one, which
# can make it take a minute just to start).
$xml = @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo><Description>Starts Windscribe Watchdog again if it stops.</Description></RegistrationInfo>
  <Triggers>
    <TimeTrigger>
      <StartBoundary>2026-01-01T00:00:00</StartBoundary>
      <Repetition><Interval>PT5M</Interval><StopAtDurationEnd>false</StopAtDurationEnd></Repetition>
    </TimeTrigger>
  </Triggers>
  <Principals><Principal id="Author"><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <StartWhenAvailable>true</StartWhenAvailable>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>4</Priority>
  </Settings>
  <Actions Context="Author"><Exec><Command>$exe</Command><Arguments>--keepalive</Arguments></Exec></Actions>
</Task>
"@
$xmlPath = Join-Path $env:TEMP 'WindscribeWatchdog-task.xml'
[IO.File]::WriteAllText($xmlPath, $xml, [Text.Encoding]::Unicode)
schtasks.exe /create /tn WindscribeWatchdog /xml $xmlPath /f | Out-Null
$taskOk = $LASTEXITCODE -eq 0
Remove-Item $xmlPath
if (-not $taskOk) { Write-Warning 'Could not register the keepalive task; the watchdog still starts with Windows.' }

# Launch through Explorer so the watchdog isn't tied to whatever ran this script.
Start-Process explorer.exe "`"$exe`""
"Installed to $dir"
