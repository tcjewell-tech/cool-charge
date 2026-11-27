<#
.SYNOPSIS
    Registers (or removes) the scheduled task that starts CoolCharge at logon.
.DESCRIPTION
    Run from an elevated PowerShell. The task starts the exe when the CURRENT user logs on, elevated
    (RunLevel Highest, so the CPU temperature can be read) but inside that user's session (so the tray icon shows).
.PARAMETER Uninstall
    Remove the task instead of installing it.
.PARAMETER ExePath
    Path to the built CoolCharge.exe. Default: bin\Release\net8.0-windows\win-x64\CoolCharge.exe next to
    this script. Its whole folder is copied to "Program Files\CoolCharge" and the task runs that copy,
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
    $ExePath = Join-Path $root 'bin\Release\net8.0-windows\win-x64\CoolCharge.exe'
}
$TaskName = 'CoolCharge'
$InstallDir = Join-Path $env:ProgramFiles 'CoolCharge'

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

# A running copy locks its files, so replacing or removing them would fail halfway. Stop it first.
Get-Process -Name CoolCharge -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill(); [void]$_.WaitForExit(5000) }

# The killed app could not restore Turbo Boost, and removing the folder would delete the record of the
# original setting. So have the installed app do its own "Restore full speed" (--restore): it alone knows
# the marker format. It is a WinExe, so Start-Process -Wait is needed to get its exit code.
$saved = Join-Path $InstallDir 'saved-boost-mode.txt'
if (Test-Path -LiteralPath $saved) {
    $installed = Join-Path $InstallDir 'CoolCharge.exe'
    if (-not (Test-Path -LiteralPath $installed -PathType Leaf)) {
        Stop-WithError "Turbo Boost may still be off (found $saved) but $installed is missing, so it cannot be restored. Nothing was removed."
    }
    $marker = "$(Get-Content -LiteralPath $saved -TotalCount 1)".Trim()
    if ($marker -match '^[0-6]$') {
        # shortcut: a single digit was written by a build without --restore (it would ignore the flag and start the
        # tray app, hanging -Wait), so restore it here the old way. Drop this branch once no such install remains.
        $mode = if ($marker -eq '0') { '2' } else { $marker }   # never restore "off"
        & powercfg.exe /setacvalueindex SCHEME_CURRENT SUB_PROCESSOR PERFBOOSTMODE $mode
        if ($LASTEXITCODE -eq 0) { & powercfg.exe /setactive SCHEME_CURRENT }
        if ($LASTEXITCODE -ne 0) { Stop-WithError 'Could not restore Turbo Boost (powercfg failed). Nothing was removed.' }
        Remove-Item -LiteralPath $saved -Force
    } else {
        # The child inherits this shell's environment; lock out .NET diagnostics/profilers as the task does.
        $env:DOTNET_EnableDiagnostics = '0'
        $env:DOTNET_DbgEnableMiniDump = '0'
        $p = Start-Process -FilePath $installed -ArgumentList '--restore' -Wait -PassThru
        if ($p.ExitCode -ne 0) { Stop-WithError "Could not restore Turbo Boost (CoolCharge.exe --restore exited with $($p.ExitCode)). Nothing was removed." }
    }
    Write-Host 'Turbo Boost was off from the app; restored it to its original setting.'
}

if ($Uninstall) {
    if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
        Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
        Write-Host "Removed scheduled task '$TaskName'."
    } else {
        Write-Host "Scheduled task '$TaskName' is not installed. Nothing to do."
    }
    if (Test-Path -LiteralPath $InstallDir) {
        try { Remove-Item -LiteralPath $InstallDir -Recurse -Force; Write-Host "Removed $InstallDir." }
        catch { Stop-WithError "Could not remove $InstallDir (exit CoolCharge from its tray icon first): $($_.Exception.Message)" }
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
if ((Split-Path -Leaf $exe) -ne 'CoolCharge.exe' -or
    -not (Test-Path -LiteralPath (Join-Path ([IO.Path]::GetDirectoryName($exe)) 'CoolCharge.dll') -PathType Leaf)) {
    Stop-WithError "Not a CoolCharge build folder (needs CoolCharge.exe and .dll): $exe"
}
if ($exe.Contains('%')) { Stop-WithError "Path contains '%' (Task Scheduler would expand it as an environment variable): $exe" }

# The task runs elevated, so it must not run from a folder that non-admin programs can write to
# (anything able to replace the exe or a DLL there would get admin at the next logon). Copy the build
# into Program Files, which only administrators can modify, and run that copy.
try {
    # Keep log.txt across reinstalls; everything else is replaced.
    if (Test-Path -LiteralPath $InstallDir) {
        Get-ChildItem -LiteralPath $InstallDir -Force | Where-Object { $_.Name -ne 'log.txt' } |
            Remove-Item -Recurse -Force
    } else {
        [void](New-Item -ItemType Directory -Path $InstallDir)
    }
    # Skip *.txt and *.log: local logs/markers in the build folder must never be installed.
    $src = [IO.Path]::GetDirectoryName($exe)
    Get-ChildItem -LiteralPath $src -Recurse -File -Force |
        Where-Object { $_.Extension -notin '.txt', '.log' } | ForEach-Object {
            $dest = Join-Path $InstallDir $_.FullName.Substring($src.Length).TrimStart([char]92)
            [void](New-Item -ItemType Directory -Path (Split-Path -Parent $dest) -Force)
            Copy-Item -LiteralPath $_.FullName -Destination $dest -Force
        }
} catch {
    Stop-WithError "Could not copy the app to $InstallDir (exit CoolCharge from its tray icon first): $($_.Exception.Message)"
}
$exe = Join-Path $InstallDir (Split-Path -Leaf $exe)

$user = $identity.Name   # DOMAIN\user of this elevated session
try {
    # Launched through cmd.exe so DOTNET_EnableDiagnostics=0 (no debugger/profiler/diagnostic IPC) is set
    # whatever the user's environment says. cmd is addressed by an absolute path from GetFolderPath, not
    # %SystemRoot%; /d skips cmd's AutoRun (settable under HKCU); no space before && so the value is exactly "0".
    # The only program started is the installed exe. A console window may flash briefly at logon.
    $cmd       = Join-Path ([Environment]::GetFolderPath('System')) 'cmd.exe'
    $action    = New-ScheduledTaskAction -Execute $cmd `
                    -Argument ('/d /c set DOTNET_EnableDiagnostics=0&& set DOTNET_DbgEnableMiniDump=0&& start "" "{0}"' -f $exe) `
                    -WorkingDirectory ([IO.Path]::GetDirectoryName($exe))
    $trigger   = New-ScheduledTaskTrigger -AtLogOn -User $user
    $principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Highest
    $settings  = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
                    -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew
    Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Principal $principal `
        -Settings $settings -Description 'CoolCharge tray app (CPU temperature watcher), started at logon.' -Force | Out-Null
} catch {
    Stop-WithError "Could not register the task: $($_.Exception.Message)"
}

Write-Host "Installed scheduled task '$TaskName' for $user."
Write-Host "  Runs: $exe"
Write-Host '  It starts at your next logon (elevated, no UAC prompt). Remove it with: .\install-task.ps1 -Uninstall'
