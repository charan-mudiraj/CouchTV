# Compiles CouchTV.exe with the C# compiler that ships inside Windows (.NET Framework 4.x).
# Nothing to download or install.
#   powershell -ExecutionPolicy Bypass -File scripts\build.ps1 [-OutDir <folder>]
param(
    [string]$OutDir = (Join-Path (Split-Path -Parent $PSScriptRoot) 'bin')
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path (Join-Path $framework 'csc.exe'))) { $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319' }
$csc = Join-Path $framework 'csc.exe'
if (-not (Test-Path $csc)) { throw "The C# compiler from .NET Framework 4 was not found. It is part of Windows 10/11; try installing .NET Framework 4.8." }

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$exe = Join-Path $OutDir 'CouchTV.exe'

$references = @(
    (Join-Path $framework 'WPF\PresentationFramework.dll'),
    (Join-Path $framework 'WPF\PresentationCore.dll'),
    (Join-Path $framework 'WPF\WindowsBase.dll'),
    (Join-Path $framework 'System.Xaml.dll'),
    (Join-Path $framework 'System.Web.Extensions.dll'),
    (Join-Path $framework 'System.IO.Compression.dll'),
    (Join-Path $framework 'System.IO.Compression.FileSystem.dll'),
    (Join-Path $framework 'System.Security.dll'),
    (Join-Path $framework 'System.Drawing.dll'),
    (Join-Path $framework 'System.Management.dll'),
    (Join-Path $framework 'WPF\UIAutomationClient.dll'),
    (Join-Path $framework 'WPF\UIAutomationTypes.dll'),
    # Windows' own API descriptions, for switching Bluetooth on and off (BluetoothPower.cs) and what's playing
    # (NowPlaying.cs: the media controls, and their pictures). Part of Windows 10/11.
    (Join-Path $env:WINDIR 'System32\WinMetadata\Windows.Devices.winmd'),
    (Join-Path $env:WINDIR 'System32\WinMetadata\Windows.Foundation.winmd'),
    (Join-Path $env:WINDIR 'System32\WinMetadata\Windows.Media.winmd'),
    (Join-Path $env:WINDIR 'System32\WinMetadata\Windows.Storage.winmd'),
    (Join-Path $framework 'System.Runtime.dll'),
    'System.dll'
)
# WebView2 (Edge's engine, part of Windows), for the home screen's trailers (Trailer.cs): Microsoft's SDK, compiled
# against here and carried inside CouchTV.exe (unpacked when it runs), as updates only replace the exe.
$webview2 = Join-Path $root 'lib\webview2'
$references += (Join-Path $webview2 'Microsoft.Web.WebView2.Core.dll'), (Join-Path $webview2 'Microsoft.Web.WebView2.Wpf.dll')
$cscArgs = @('/nologo', '/target:winexe', '/platform:anycpu', '/optimize+', '/codepage:65001', "/out:$exe",
             "/resource:$(Join-Path $root 'couchtv.ini'),CouchTV.couchtv.ini",
             "/resource:$(Join-Path $root 'remote.ini'),CouchTV.remote.ini")
# CouchTV's touches for websites (Sites.cs): the browser extension, written out when a web tile opens.
$cscArgs += Get-ChildItem (Join-Path $root 'sites') -File | ForEach-Object { "/resource:$($_.FullName),CouchTV.sites.$($_.Name)" }
$cscArgs += Get-ChildItem $webview2 -Filter *.dll | ForEach-Object { "/resource:$($_.FullName),CouchTV.lib.$($_.Name)" }
$cscArgs += "/resource:$(Join-Path $root 'home\trailer.html'),CouchTV.home.trailer.html"
$cscArgs += $references | ForEach-Object { "/r:$_" }
$cscArgs += Get-ChildItem (Join-Path $root 'src\*.cs') | ForEach-Object { $_.FullName }

& $csc @cscArgs
if ($LASTEXITCODE -ne 0) { throw "Build failed (see the errors above)." }
Write-Output $exe
