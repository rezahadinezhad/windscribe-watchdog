# Removes Windscribe Watchdog: its keepalive task, startup entry, settings and folder.
# Windscribe itself is not touched.
#   powershell -ExecutionPolicy Bypass -File uninstall.ps1
$dir = Join-Path $env:LOCALAPPDATA 'Programs\WindscribeWatchdog'
schtasks.exe /delete /tn WindscribeWatchdog /f 2>$null | Out-Null   # first, so it can't start it again
Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name WindscribeWatchdog -ErrorAction SilentlyContinue
Remove-Item 'HKCU:\Software\WindscribeWatchdog' -Recurse -ErrorAction SilentlyContinue
foreach ($p in Get-Process WindscribeWatchdog -ErrorAction SilentlyContinue) { $p.Kill(); $p.WaitForExit(5000) | Out-Null }
Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
"Windscribe Watchdog removed."
