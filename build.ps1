# Compiles Windscribe Watchdog with the C# compiler that ships with Windows (.NET Framework 4.8).
# No SDK or downloads needed. Output: bin\WindscribeWatchdog.exe (or the path given with -Out).
#   powershell -ExecutionPolicy Bypass -File build.ps1
param([string]$Out = (Join-Path $PSScriptRoot 'bin\WindscribeWatchdog.exe'))
$ErrorActionPreference = 'Stop'
$fw  = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$src = Join-Path $PSScriptRoot 'src'
New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null

$references = 'System.Windows.Forms.dll', 'System.Drawing.dll', 'System.ServiceProcess.dll', 'System.Xaml.dll',
              'PresentationFramework.dll', 'PresentationCore.dll', 'WindowsBase.dll', 'WindowsFormsIntegration.dll',
              'UIAutomationClient.dll', 'UIAutomationTypes.dll'
$icon = Join-Path $PSScriptRoot 'assets\watchdog.ico'
$arguments = @('/nologo', '/codepage:65001', '/target:winexe', '/optimize+', "/out:$Out", "/lib:$fw\WPF",
               "/win32icon:$icon",
               "/resource:$(Join-Path $src 'MainWindow.xaml'),WindscribeWatchdog.MainWindow.xaml",
               "/resource:$icon,WindscribeWatchdog.watchdog.ico") +
             ($references | ForEach-Object { "/r:$_" }) +
             (Get-ChildItem $src -Filter *.cs | ForEach-Object { $_.FullName })

& (Join-Path $fw 'csc.exe') @arguments
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
$Out
