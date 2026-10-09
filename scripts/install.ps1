<#
  CouchTV installer: turns this Windows PC into a TV box.

  Easiest: double-click Install-CouchTV.cmd (it asks for administrator rights).
  Or:      powershell -ExecutionPolicy Bypass -File scripts\install.ps1 [options]

  Options
    -NoShell              Keep the Windows desktop and open CouchTV on top of it at sign-in,
                          instead of replacing the desktop (slower start, but less change).
    -SkipTweaks           Leave power, Windows Update and notification settings alone.
    -SkipBrowserPolicies  Leave Edge/Brave settings alone.
    -DryRun               Show what would change without changing anything.

  Undo everything: run Uninstall-CouchTV.cmd in the install folder.
#>
[CmdletBinding()]
param(
    [string]$InstallDir = 'C:\CouchTV',
    [switch]$NoShell,
    [switch]$SkipTweaks,
    [switch]$SkipBrowserPolicies,
    [switch]$DryRun
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

function Section([string]$text) { Write-Host ''; Write-Host "== $text" -ForegroundColor Cyan }
function Note([string]$text) { Write-Host "   $text" -ForegroundColor Gray }
function Warn([string]$text) { Write-Host "   ! $text" -ForegroundColor Yellow }

# Runs a change, or only describes it in a dry run.
function Apply([string]$description, [scriptblock]$action) {
    if ($DryRun) { Write-Host "   [dry run] $description" -ForegroundColor DarkGray; return }
    Write-Host "   $description"
    & $action
}

function Set-RegValue([string]$path, [string]$name, $value, [string]$type = 'DWord') {
    Apply "$path  $name = $value" {
        if (-not (Test-Path $path)) { New-Item -Path $path -Force | Out-Null }
        New-ItemProperty -Path $path -Name $name -Value $value -PropertyType $type -Force | Out-Null
    }
}

function Invoke-PowerCfg([string[]]$arguments) {
    Apply "powercfg $($arguments -join ' ')" {
        & powercfg.exe @arguments
        if ($LASTEXITCODE -ne 0) { Warn "powercfg $($arguments -join ' ') failed (exit $LASTEXITCODE)" }
    }
}

function Ask([string]$question) {
    if ($DryRun) { Write-Host "   [dry run] would ask: $question" -ForegroundColor DarkGray; return $false }
    $answer = Read-Host "   $question [Y/n]"
    return ($answer -eq '' -or $answer -match '^(y|yes)$')
}

# The commit this folder was checked out at, read straight from .git (git itself may not be installed).
function Get-SourceCommit {
    $git = Join-Path $root '.git'
    $headFile = Join-Path $git 'HEAD'
    if (-not (Test-Path $headFile)) { return $null }
    $head = (Get-Content $headFile -TotalCount 1).Trim()
    if ($head -match '^[0-9a-f]{40}$') { return $head }
    if ($head -notmatch '^ref: (.+)$') { return $null }
    $ref = $Matches[1]
    $refFile = Join-Path $git $ref
    if (Test-Path $refFile) { return (Get-Content $refFile -TotalCount 1).Trim() }
    $packed = Join-Path $git 'packed-refs'
    if (Test-Path $packed) {
        foreach ($line in Get-Content $packed) {
            if ($line -match ('^([0-9a-f]{40}) ' + [regex]::Escape($ref) + '$')) { return $Matches[1] }
        }
    }
    return $null
}

function Find-File([string[]]$candidates) {
    foreach ($candidate in $candidates) { if ($candidate -and (Test-Path $candidate)) { return $candidate } }
    return $null
}

Write-Host 'CouchTV installer' -ForegroundColor White
if ($DryRun) { Note 'Dry run: nothing will be changed.' }

# ------------------------------------------------------------------ checks
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin -and -not $DryRun) { throw 'Please run Install-CouchTV.cmd, which asks for administrator rights.' }
$os = Get-CimInstance Win32_OperatingSystem
Note "$($os.Caption) (build $($os.BuildNumber)), setting up for user '$env:USERNAME'"
Note "TV mode will apply to this user only. Run the installer while signed in as the account the TV should use."

# ------------------------------------------------------------------ build and copy
Section "Install CouchTV to $InstallDir"
$exe = Join-Path $InstallDir 'CouchTV.exe'
$running = Get-Process CouchTV -ErrorAction SilentlyContinue
if ($running) { Apply 'Stop the running CouchTV' { $running | Stop-Process -Force; Start-Sleep -Milliseconds 500 } }

# Compile with the C# compiler built into Windows. A dry run builds into a temp folder to prove it works.
$buildDir = if ($DryRun) { Join-Path $env:TEMP 'CouchTV-dryrun' } else { $InstallDir }
Write-Host "   Compile CouchTV.exe into $buildDir"
& (Join-Path $PSScriptRoot 'build.ps1') -OutDir $buildDir | Out-Null

$ini = Join-Path $InstallDir 'couchtv.ini'
if (Test-Path $ini) {
    Apply 'Keep your existing couchtv.ini (fresh defaults saved as couchtv.default.ini)' {
        Copy-Item (Join-Path $root 'couchtv.ini') (Join-Path $InstallDir 'couchtv.default.ini') -Force
    }
} else {
    Apply 'Write couchtv.ini (your settings file)' { Copy-Item (Join-Path $root 'couchtv.ini') $ini -Force }
}
$remoteIni = Join-Path $InstallDir 'remote.ini'
if (Test-Path $remoteIni) { Note 'Keeping your existing remote.ini (remote buttons)' }
else { Apply 'Write remote.ini (remote buttons)' { Copy-Item (Join-Path $root 'remote.ini') $remoteIni -Force } }
Apply 'Copy the uninstaller and README' {
    Copy-Item (Join-Path $PSScriptRoot 'uninstall.ps1') $InstallDir -Force
    Copy-Item (Join-Path $root 'Uninstall-CouchTV.cmd') $InstallDir -Force
    Copy-Item (Join-Path $root 'README.md') $InstallDir -Force
}

# Updates: version.txt says which commit is installed; CouchTV compares it with GitHub. An unknown version
# simply means the first check offers the latest one.
$commit = Get-SourceCommit
$versionLabel = if ($commit) { $commit.Substring(0, 7) } else { 'unknown' }
Apply "Record the installed version ($versionLabel)" {
    $lines = @($(if ($commit) { $commit } else { 'unknown' }), "Installed from $root")
    Set-Content -Path (Join-Path $InstallDir 'version.txt') -Value $lines -Encoding ASCII
}

# ------------------------------------------------------------------ browsers
Section 'Browsers'
$brave = Find-File @("$env:ProgramFiles\BraveSoftware\Brave-Browser\Application\brave.exe",
                     "${env:ProgramFiles(x86)}\BraveSoftware\Brave-Browser\Application\brave.exe",
                     "$env:LOCALAPPDATA\BraveSoftware\Brave-Browser\Application\brave.exe")
$edge = Find-File @("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe",
                    "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe")
if ($brave) { Note "Brave: $brave" }
else {
    Warn 'Brave is not installed. The YouTube and Web tiles use it to block ads.'
    if ((Get-Command winget -ErrorAction SilentlyContinue) -and (Ask 'Install Brave now with winget?')) {
        winget install --id Brave.Brave --exact --accept-source-agreements --accept-package-agreements
    } else { Note 'Get it from https://brave.com, then run this installer again.' }
}
if ($edge) { Note "Edge: $edge" } else { Warn 'Microsoft Edge is missing. Netflix, Prime Video and JioHotstar use it for Full HD.' }

# ------------------------------------------------------------------ start-up
Section 'Start straight into the TV home screen'
$winlogon = 'HKCU:\Software\Microsoft\Windows NT\CurrentVersion\Winlogon'
$settings = 'HKCU:\Software\CouchTV'
if ($NoShell) {
    Note 'Keeping the Windows desktop; CouchTV opens on top of it at sign-in.'
    $current = (Get-ItemProperty $winlogon -ErrorAction SilentlyContinue).Shell
    if ($current -and $current -match 'CouchTV') {
        Apply 'Remove CouchTV as the Windows shell' { Remove-ItemProperty $winlogon -Name Shell }
    }
    Set-RegValue $settings 'ShellMode' 0
} else {
    # Replace the desktop/taskbar for this user: no Explorer, no start-up apps, straight to CouchTV.
    $shellValue = if ($exe -match ' ') { "`"$exe`"" } else { $exe }
    Set-RegValue $winlogon 'Shell' $shellValue 'String'
    Set-RegValue $settings 'ShellMode' 1
}
# Also start CouchTV when Explorer is the shell (desktop mode, or if Windows ignores the shell setting).
Set-RegValue 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' 'CouchTV' "`"$exe`" --autostart" 'String'

# The phone remote over Wi-Fi: let CouchTV receive connections, from the home network only.
Apply 'Let the phone remote connect over Wi-Fi (firewall rule, local network only)' {
    & netsh.exe advfirewall firewall delete rule name="CouchTV phone remote" | Out-Null
    & netsh.exe advfirewall firewall add rule name="CouchTV phone remote" dir=in action=allow program="$exe" remoteip=localsubnet profile=any enable=yes | Out-Null
}

# ------------------------------------------------------------------ tweaks
if (-not $SkipTweaks) {
    Section 'TV-friendly Windows settings'
    Note "Power button (and the remote's power key) = Sleep: wakes in seconds, like a TV's standby."
    Invoke-PowerCfg @('/setacvalueindex', 'SCHEME_CURRENT', 'SUB_BUTTONS', 'PBUTTONACTION', '1')
    Invoke-PowerCfg @('/setdcvalueindex', 'SCHEME_CURRENT', 'SUB_BUTTONS', 'PBUTTONACTION', '1')
    Invoke-PowerCfg @('/setacvalueindex', 'SCHEME_CURRENT', 'SUB_BUTTONS', 'SBUTTONACTION', '1')
    Invoke-PowerCfg @('/setdcvalueindex', 'SCHEME_CURRENT', 'SUB_BUTTONS', 'SBUTTONACTION', '1')
    Note 'No password prompt when it wakes up.'
    Invoke-PowerCfg @('/setacvalueindex', 'SCHEME_CURRENT', 'SUB_NONE', 'CONSOLELOCK', '0')
    Invoke-PowerCfg @('/setdcvalueindex', 'SCHEME_CURRENT', 'SUB_NONE', 'CONSOLELOCK', '0')
    Invoke-PowerCfg @('/setactive', 'SCHEME_CURRENT')
    Note 'Idle on the home screen: screen off after 30 min, sleep after 2 h (playing video keeps it awake).'
    Invoke-PowerCfg @('/change', 'monitor-timeout-ac', '30')
    Invoke-PowerCfg @('/change', 'standby-timeout-ac', '120')

    Note 'Let the keyboard/remote wake the PC from sleep:'
    $wakeable = @(& powercfg.exe /devicequery wake_programmable | Where-Object { $_ -match 'keyboard' })
    if ($wakeable.Count -eq 0) { Note '(none found - plug in the remote''s USB receiver and run the installer again)' }
    foreach ($device in $wakeable) { Invoke-PowerCfg @('/deviceenablewake', $device) }

    Note 'Windows Update: no surprise restarts between 8 AM and 2 AM.'
    Set-RegValue 'HKLM:\SOFTWARE\Microsoft\WindowsUpdate\UX\Settings' 'SmartActiveHoursState' 0
    Set-RegValue 'HKLM:\SOFTWARE\Microsoft\WindowsUpdate\UX\Settings' 'ActiveHoursStart' 8
    Set-RegValue 'HKLM:\SOFTWARE\Microsoft\WindowsUpdate\UX\Settings' 'ActiveHoursEnd' 2
    Set-RegValue 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU' 'NoAutoRebootWithLoggedOnUsers' 1

    Note 'No notification pop-ups over videos, no "finish setting up your PC" screens after updates.'
    Set-RegValue 'HKCU:\Software\Microsoft\Windows\CurrentVersion\PushNotifications' 'ToastEnabled' 0
    Set-RegValue 'HKCU:\Software\Microsoft\Windows\CurrentVersion\UserProfileEngagement' 'ScoobeSystemSettingEnabled' 0
    Set-RegValue 'HKCU:\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager' 'SubscribedContent-310093Enabled' 0
    Set-RegValue 'HKCU:\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager' 'SubscribedContent-338389Enabled' 0
    Set-RegValue 'HKCU:\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager' 'SoftLandingEnabled' 0
}

# ------------------------------------------------------------------ browser policies
if (-not $SkipBrowserPolicies) {
    Section 'Browser clean-up'
    Note 'Skip first-run screens and sidebars, and let the browsers quit fully when their window closes.'
    $edgePolicies = 'HKLM:\SOFTWARE\Policies\Microsoft\Edge'
    Set-RegValue $edgePolicies 'HideFirstRunExperience' 1
    Set-RegValue $edgePolicies 'AutoImportAtFirstRun' 4
    Set-RegValue $edgePolicies 'BackgroundModeEnabled' 0
    Set-RegValue $edgePolicies 'StartupBoostEnabled' 0
    Set-RegValue $edgePolicies 'HubsSidebarEnabled' 0
    Set-RegValue $edgePolicies 'ShowRecommendationsEnabled' 0
    # A fresh Edge profile otherwise opens a "sign in and sync" dialog over Netflix.
    Set-RegValue $edgePolicies 'BrowserSignin' 0
    Set-RegValue $edgePolicies 'NonRemovableProfileEnabled' 0
    Set-RegValue $edgePolicies 'SyncDisabled' 1
    $bravePolicies = 'HKLM:\SOFTWARE\Policies\BraveSoftware\Brave'
    Set-RegValue $bravePolicies 'BackgroundModeEnabled' 0
    Set-RegValue $bravePolicies 'BraveRewardsDisabled' 1
    Set-RegValue $bravePolicies 'BraveWalletDisabled' 1
    Set-RegValue $bravePolicies 'BraveVPNDisabled' 1
    Set-RegValue $bravePolicies 'BraveAIChatEnabled' 0
    Set-RegValue $bravePolicies 'BraveNewsDisabled' 1
    Set-RegValue $bravePolicies 'TorDisabled' 1
    Note 'Edge and Brave will now say "managed by your organization". That is just these settings.'
}

# ------------------------------------------------------------------ shortcuts
Section 'Shortcuts'
$shortcuts = @(
    @{ Path = Join-Path ([Environment]::GetFolderPath('Programs')) 'CouchTV.lnk'; Target = $exe; Arguments = '' },
    @{ Path = Join-Path ([Environment]::GetFolderPath('Desktop')) 'CouchTV.lnk'; Target = $exe; Arguments = '' },
    @{ Path = Join-Path ([Environment]::GetFolderPath('Programs')) 'Exit TV mode (uninstall CouchTV).lnk';
       Target = Join-Path $InstallDir 'Uninstall-CouchTV.cmd'; Arguments = '' }
)
foreach ($shortcut in $shortcuts) {
    Apply "Create $($shortcut.Path)" {
        $link = (New-Object -ComObject WScript.Shell).CreateShortcut($shortcut.Path)
        $link.TargetPath = $shortcut.Target
        $link.Arguments = $shortcut.Arguments
        $link.WorkingDirectory = $InstallDir
        $link.Save()
    }
}

# ------------------------------------------------------------------ automatic sign-in
Section 'Sign in automatically'
$autoAdminLogon = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon' -ErrorAction SilentlyContinue).AutoAdminLogon
if ($autoAdminLogon -eq '1') { Note 'Already signs in automatically.' }
else {
    Note 'Without this, the TV stops at the Windows sign-in screen every time it starts.'
    if (Ask "Set up automatic sign-in now with Microsoft's free Sysinternals Autologon tool?") {
        # Lets a Microsoft account sign in with its password (needed for automatic sign-in).
        Set-RegValue 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\PasswordLess\Device' 'DevicePasswordLessBuildVersion' 0
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        $zip = Join-Path $env:TEMP 'AutoLogon.zip'
        $dir = Join-Path $env:TEMP 'AutoLogon'
        Invoke-WebRequest 'https://download.sysinternals.com/files/AutoLogon.zip' -OutFile $zip -UseBasicParsing
        Expand-Archive $zip $dir -Force
        $tool = Find-File @((Join-Path $dir 'Autologon64.exe'), (Join-Path $dir 'Autologon.exe'))
        Note 'In the window that opens, type your Windows password and click Enable.'
        Note '(Microsoft account: the account password, not the PIN.)'
        Start-Process $tool -ArgumentList '/accepteula' -Wait
    } else { Note 'Later: run Sysinternals Autologon, or netplwiz.' }
}

# ------------------------------------------------------------------ check
Section 'Check'
$reportExe = if ($DryRun) { Join-Path $buildDir 'CouchTV.exe' } else { $exe }
$report = Join-Path $env:TEMP 'couchtv-selftest.txt'
Start-Process -FilePath $reportExe -ArgumentList @('--selftest', "`"$report`"") -Wait
Get-Content $report | ForEach-Object { Note $_ }

Section 'Done'
if ($DryRun) { Note 'Dry run finished. Nothing was changed.'; return }
Note 'Restart the PC to start in TV mode.'
Note 'Stuck on a black screen? Press Ctrl+Alt+Del, open Task Manager, choose "Run new task" and run:'
Note "    $InstallDir\Uninstall-CouchTV.cmd"
if (Ask 'Restart now?') { Restart-Computer -Force }
