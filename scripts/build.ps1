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
    'System.dll'
)
$cscArgs = @('/nologo', '/target:winexe', '/platform:anycpu', '/optimize+', '/codepage:65001', "/out:$exe",
             "/resource:$(Join-Path $root 'couchtv.ini'),CouchTV.couchtv.ini",
             "/resource:$(Join-Path $root 'remote.ini'),CouchTV.remote.ini")
$cscArgs += $references | ForEach-Object { "/r:$_" }
$cscArgs += Get-ChildItem (Join-Path $root 'src\*.cs') | ForEach-Object { $_.FullName }

& $csc @cscArgs
if ($LASTEXITCODE -ne 0) { throw "Build failed (see the errors above)." }
Write-Output $exe
