<#
.SYNOPSIS
    Registers (or removes) the scheduled task that starts Flight Thermal Guard at logon.
.DESCRIPTION
    Run from an elevated PowerShell. The task starts the exe when the CURRENT user logs on, elevated
    (RunLevel Highest, so the CPU temperature can be read) but inside that user's session (so the tray icon shows).
.PARAMETER Uninstall
    Remove the task instead of installing it.
.PARAMETER ExePath
    Path to the built FlightThermalGuard.exe. Default: bin\Release\net8.0-windows\win-x64\FlightThermalGuard.exe next to
    this script. Its whole folder is copied to "Program Files\FlightThermalGuard" and the task runs that copy,
    so re-run this script after every rebuild.
.EXAMPLE
    .\install-task.ps1
    .\install-task.ps1 -Uninstall
#>
[CmdletBinding()]
param(
    [switch]$Uninstall,
    [string]$ExePath
)

$ErrorActionPreference = 'Stop'
# Worked out here, not as a param() default: under Windows PowerShell 5.1 $PSScriptRoot was empty there.
if (-not $ExePath) {
    $root = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
    $ExePath = Join-Path $root 'bin\Release\net8.0-windows\win-x64\FlightThermalGuard.exe'
}
$TaskName = 'FlightThermalGuard'
$InstallDir = Join-Path $env:ProgramFiles 'FlightThermalGuard'

function Stop-WithError([string]$Message) {
    [Console]::Error.WriteLine("ERROR: $Message")
    exit 1
}

# This task runs elevated at every logon, so only an elevated shell may create or remove it.
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$isAdmin = (New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Stop-WithError 'Not running as administrator. Open PowerShell with "Run as administrator" and run this script again.'
}

# Removing the installed folder would delete the record of the original Turbo Boost setting.
if (Test-Path -LiteralPath (Join-Path $InstallDir 'saved-boost-mode.txt')) {
    Stop-WithError 'Turbo Boost is still off from the app. Start Flight Thermal Guard, choose "Restore full speed", exit it, then run this script again.'
}

# A running copy locks its files, so replacing or removing them would fail halfway. Stop it first.
# Safe to kill: the check above proved Turbo Boost is not off, so it has nothing to restore on exit.
Get-Process -Name FlightThermalGuard -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill(); [void]$_.WaitForExit(5000) }

if ($Uninstall) {
    if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
        Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
        Write-Host "Removed scheduled task '$TaskName'."
    } else {
        Write-Host "Scheduled task '$TaskName' is not installed. Nothing to do."
    }
    if (Test-Path -LiteralPath $InstallDir) {
        try { Remove-Item -LiteralPath $InstallDir -Recurse -Force; Write-Host "Removed $InstallDir." }
        catch { Stop-WithError "Could not remove $InstallDir (exit Flight Thermal Guard from its tray icon first): $($_.Exception.Message)" }
    }
    exit 0
}

# The path becomes the program an elevated task runs, so insist on a real, existing .exe at a full path.
if (-not (Test-Path -LiteralPath $ExePath -PathType Leaf)) {
    Stop-WithError "Executable not found: $ExePath`nBuild it first (dotnet build -c Release) or pass -ExePath."
}
$exe = (Resolve-Path -LiteralPath $ExePath).ProviderPath
if ([IO.Path]::GetExtension($exe) -ne '.exe') { Stop-WithError "Not an .exe file: $exe" }
# The whole folder gets copied into Program Files, so make sure it really is this app's build output.
if ((Split-Path -Leaf $exe) -ne 'FlightThermalGuard.exe' -or
    -not (Test-Path -LiteralPath (Join-Path ([IO.Path]::GetDirectoryName($exe)) 'FlightThermalGuard.dll') -PathType Leaf)) {
    Stop-WithError "Not a Flight Thermal Guard build folder (needs FlightThermalGuard.exe and .dll): $exe"
}
if ($exe.Contains('%')) { Stop-WithError "Path contains '%' (Task Scheduler would expand it as an environment variable): $exe" }

# The task runs elevated, so it must not run from a folder that non-admin programs can write to
# (anything able to replace the exe or a DLL there would get admin at the next logon). Copy the build
# into Program Files, which only administrators can modify, and run that copy.
try {
    if (Test-Path -LiteralPath $InstallDir) { Remove-Item -LiteralPath $InstallDir -Recurse -Force }
    Copy-Item -LiteralPath ([IO.Path]::GetDirectoryName($exe)) -Destination $InstallDir -Recurse -Force
} catch {
    Stop-WithError "Could not copy the app to $InstallDir (exit Flight Thermal Guard from its tray icon first): $($_.Exception.Message)"
}
$exe = Join-Path $InstallDir (Split-Path -Leaf $exe)

$user = $identity.Name   # DOMAIN\user of this elevated session
try {
    # Quoted path, no arguments: the task cannot run anything but this one file.
    $action    = New-ScheduledTaskAction -Execute ('"{0}"' -f $exe) -WorkingDirectory ([IO.Path]::GetDirectoryName($exe))
    $trigger   = New-ScheduledTaskTrigger -AtLogOn -User $user
    $principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Highest
    $settings  = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
                    -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew
    Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Principal $principal `
        -Settings $settings -Description 'Flight Thermal Guard tray app (CPU temperature watcher), started at logon.' -Force | Out-Null
} catch {
    Stop-WithError "Could not register the task: $($_.Exception.Message)"
}

Write-Host "Installed scheduled task '$TaskName' for $user."
Write-Host "  Runs: $exe"
Write-Host '  It starts at your next logon (elevated, no UAC prompt). Remove it with: .\install-task.ps1 -Uninstall'
