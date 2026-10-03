<#
  Installs a CouchTV update that CouchTV has already downloaded, built and checked (see src\AppUpdate.cs).
  CouchTV starts this script and exits. The script swaps in the new files and starts CouchTV again.
  If anything goes wrong it puts the previous CouchTV.exe back, so the TV is never left without a working
  home screen. Your couchtv.ini and remote.ini are never touched.

  Log: %LOCALAPPDATA%\CouchTV\update.log
#>
param(
    [Parameter(Mandatory = $true)] [string]$Source,       # unpacked source of the new version
    [Parameter(Mandatory = $true)] [string]$Build,        # folder with the new CouchTV.exe and version.txt
    [Parameter(Mandatory = $true)] [string]$InstallDir,   # e.g. C:\CouchTV
    [int]$WaitPid = 0,                                    # the CouchTV process that is exiting
    [switch]$NoRestart                                    # for testing
)
$ErrorActionPreference = 'Stop'

$logDir = Join-Path $env:LOCALAPPDATA 'CouchTV'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$log = Join-Path $logDir 'update.log'
function Write-Log([string]$text) {
    Add-Content -Path $log -Value ('{0} {1}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $text)
}

# Antivirus scanners can hold a freshly built exe for a moment, so retry copies.
function Copy-WithRetry([string]$from, [string]$to) {
    for ($attempt = 1; $attempt -le 10; $attempt++) {
        try { Copy-Item -LiteralPath $from -Destination $to -Force; return }
        catch { if ($attempt -eq 10) { throw }; Start-Sleep -Milliseconds 500 }
    }
}

$exe = Join-Path $InstallDir 'CouchTV.exe'
$backup = Join-Path $InstallDir 'CouchTV.previous.exe'
Write-Log "Installing the update in $Build into $InstallDir"
try {
    if ($WaitPid -gt 0) {
        $old = Get-Process -Id $WaitPid -ErrorAction SilentlyContinue
        if ($old -and -not $old.WaitForExit(20000)) {
            Write-Log 'CouchTV did not exit in time; stopping it'
            $old.Kill()
            $old.WaitForExit(5000) | Out-Null
        }
    }
    if (Test-Path $exe) { Copy-WithRetry $exe $backup }
    Copy-WithRetry (Join-Path $Build 'CouchTV.exe') $exe

    # Keep the user's settings; save the new defaults next to them for reference.
    Copy-WithRetry (Join-Path $Source 'couchtv.ini') (Join-Path $InstallDir 'couchtv.default.ini')
    Copy-WithRetry (Join-Path $Source 'remote.ini') (Join-Path $InstallDir 'remote.default.ini')
    foreach ($file in 'README.md', 'Uninstall-CouchTV.cmd') {
        Copy-WithRetry (Join-Path $Source $file) (Join-Path $InstallDir $file)
    }
    Copy-WithRetry (Join-Path $Source 'scripts\uninstall.ps1') (Join-Path $InstallDir 'uninstall.ps1')

    # Last, so a failed update leaves the old version number in place.
    Copy-WithRetry (Join-Path $Build 'version.txt') (Join-Path $InstallDir 'version.txt')
    Write-Log ('Updated to ' + (Get-Content (Join-Path $InstallDir 'version.txt') -TotalCount 1))
    $startArgs = '--updated'
}
catch {
    Write-Log "Update failed: $($_.Exception.Message)"
    if (Test-Path $backup) {
        try { Copy-WithRetry $backup $exe; Write-Log 'Previous version restored' }
        catch { Write-Log "Could not restore the previous version: $($_.Exception.Message)" }
    }
    $startArgs = '--update-failed'
}

if (-not $NoRestart) { Start-Process -FilePath $exe -ArgumentList $startArgs -WorkingDirectory $InstallDir }
