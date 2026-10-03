<#
  Removes CouchTV and puts the normal Windows desktop back.
  Run Uninstall-CouchTV.cmd (it asks for administrator rights), or:
    powershell -ExecutionPolicy Bypass -File uninstall.ps1 [-KeepFiles] [-DryRun]
#>
[CmdletBinding()]
param(
    [string]$InstallDir = 'C:\CouchTV',
    [switch]$KeepFiles,
    [switch]$DryRun
)
$ErrorActionPreference = 'Continue'

function Section([string]$text) { Write-Host ''; Write-Host "== $text" -ForegroundColor Cyan }
function Note([string]$text) { Write-Host "   $text" -ForegroundColor Gray }
function Apply([string]$description, [scriptblock]$action) {
    if ($DryRun) { Write-Host "   [dry run] $description" -ForegroundColor DarkGray; return }
    Write-Host "   $description"
    & $action
}
function Remove-RegValue([string]$path, [string]$name) {
    if ($null -eq (Get-ItemProperty $path -Name $name -ErrorAction SilentlyContinue)) { return }
    Apply "Remove $path  $name" { Remove-ItemProperty $path -Name $name -ErrorAction SilentlyContinue }
}
function Set-RegValue([string]$path, [string]$name, $value) {
    Apply "$path  $name = $value" {
        if (-not (Test-Path $path)) { New-Item -Path $path -Force | Out-Null }
        New-ItemProperty -Path $path -Name $name -Value $value -PropertyType DWord -Force | Out-Null
    }
}

Write-Host 'CouchTV uninstaller' -ForegroundColor White

Section 'Stop CouchTV and give the desktop back'
$running = Get-Process CouchTV -ErrorAction SilentlyContinue
if ($running) { Apply 'Stop CouchTV' { $running | Stop-Process -Force } }
$winlogon = 'HKCU:\Software\Microsoft\Windows NT\CurrentVersion\Winlogon'
$shell = (Get-ItemProperty $winlogon -ErrorAction SilentlyContinue).Shell
if ($shell -and $shell -match 'CouchTV') { Remove-RegValue $winlogon 'Shell' }
Remove-RegValue 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' 'CouchTV'
if (Test-Path 'HKCU:\Software\CouchTV') { Apply 'Remove HKCU:\Software\CouchTV' { Remove-Item 'HKCU:\Software\CouchTV' -Recurse } }

Section 'Undo the Windows settings'
foreach ($arguments in @(
        @('/setacvalueindex', 'SCHEME_CURRENT', 'SUB_BUTTONS', 'PBUTTONACTION', '3'),
        @('/setdcvalueindex', 'SCHEME_CURRENT', 'SUB_BUTTONS', 'PBUTTONACTION', '3'),
        @('/setacvalueindex', 'SCHEME_CURRENT', 'SUB_NONE', 'CONSOLELOCK', '1'),
        @('/setdcvalueindex', 'SCHEME_CURRENT', 'SUB_NONE', 'CONSOLELOCK', '1'),
        @('/setactive', 'SCHEME_CURRENT'))) {
    Apply "powercfg $($arguments -join ' ')" { & powercfg.exe @arguments }
}
Remove-RegValue 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU' 'NoAutoRebootWithLoggedOnUsers'
Set-RegValue 'HKCU:\Software\Microsoft\Windows\CurrentVersion\PushNotifications' 'ToastEnabled' 1
Remove-RegValue 'HKCU:\Software\Microsoft\Windows\CurrentVersion\UserProfileEngagement' 'ScoobeSystemSettingEnabled'

$edgePolicies = 'HKLM:\SOFTWARE\Policies\Microsoft\Edge'
foreach ($name in 'HideFirstRunExperience', 'AutoImportAtFirstRun', 'BackgroundModeEnabled', 'StartupBoostEnabled', 'HubsSidebarEnabled',
                  'ShowRecommendationsEnabled', 'BrowserSignin', 'NonRemovableProfileEnabled', 'SyncDisabled') {
    Remove-RegValue $edgePolicies $name
}
$bravePolicies = 'HKLM:\SOFTWARE\Policies\BraveSoftware\Brave'
foreach ($name in 'BackgroundModeEnabled', 'BraveRewardsDisabled', 'BraveWalletDisabled', 'BraveVPNDisabled', 'BraveAIChatEnabled',
                  'BraveNewsDisabled', 'TorDisabled') {
    Remove-RegValue $bravePolicies $name
}
Note 'Left as they are: automatic sign-in, wake-from-keyboard, Windows Update active hours, idle timeouts.'

Section 'Shortcuts and files'
foreach ($path in @(
        (Join-Path ([Environment]::GetFolderPath('Programs')) 'CouchTV.lnk'),
        (Join-Path ([Environment]::GetFolderPath('Desktop')) 'CouchTV.lnk'),
        (Join-Path ([Environment]::GetFolderPath('Programs')) 'Exit TV mode (uninstall CouchTV).lnk'))) {
    if (Test-Path $path) { Apply "Delete $path" { Remove-Item $path -Force } }
}
if ($KeepFiles) { Note "Kept $InstallDir and your CouchTV browser logins." }
else {
    $data = Join-Path $env:LOCALAPPDATA 'CouchTV'
    $answer = if ($DryRun) { 'n' } else { Read-Host "   Also delete $InstallDir and CouchTV's saved browser logins? [y/N]" }
    if ($answer -match '^(y|yes)$') {
        Apply "Delete $InstallDir" { Remove-Item $InstallDir -Recurse -Force -ErrorAction SilentlyContinue }
        Apply "Delete $data" { Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue }
    }
}

if (-not $DryRun -and -not (Get-Process explorer -ErrorAction SilentlyContinue)) {
    Note 'Starting the Windows desktop...'
    Start-Process explorer.exe
}
Section 'Done'
Note 'Restart the PC to finish. Windows will start to the normal desktop.'
