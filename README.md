# CoolCharge

A small Windows tray app that watches the CPU temperature while the laptop is plugged in (for example charging from a
power bank on a plane). When the CPU runs hot it asks whether to cap CPU speed. It never throttles by itself; it only
acts when you click.

## Requirements

- Windows 11 and the .NET 8 SDK to build. The built app carries its own .NET runtime.
- The PawnIO driver, which lets the temperature library read the CPU sensor: `winget install namazso.PawnIO`
- It must run as administrator to read the CPU temperature. Without admin the tray shows no reading.

## Build and run

    dotnet build -c Release
    bin\Release\net8.0-windows\win-x64\CoolCharge.exe

Start it from an elevated prompt (or use the autostart task below). The tray icon is a coloured square showing the
temperature. Windows 11 hides new tray icons: look behind the `^` arrow next to the clock, and drag the icon onto the
taskbar to keep it in view. Windows remembers that per program location, so the installed copy needs it once too.
Prompts appear on top of other windows whether or not the icon is visible.

## Autostart

From an elevated PowerShell in this folder:

    .\install-task.ps1             # copies the build to Program Files\CoolCharge and starts that copy at your logon, elevated, no UAC prompt (re-run after each rebuild)
    .\install-task.ps1 -Uninstall  # removes it

The task is created for the account running the script. Pass `-ExePath` if the exe is somewhere else.
`log.txt` is kept across reinstalls and removed by `-Uninstall`. The task starts the app through `cmd.exe` with .NET
diagnostics and profiling disabled (`DOTNET_EnableDiagnostics=0`), so a profiler injected through the user's
environment cannot load into the elevated process; a console window may flash at logon.

## Thresholds

The app judges a 30 second average (6 readings, one every 5 s), never a single reading.

- Normal to Warm: average reaches 85 C. Back to Normal only after the average stays at or below 75 C for 2 minutes.
- Warm to Hot: average reaches 92 C. Hot drops to Warm when the average falls to 85 C or lower.
- Monitoring and prompts only happen while the laptop is on AC power.

Details and reasoning are in [DESIGN.md](DESIGN.md).

## Command-line flags

- `--selftest` runs the built-in checks (threshold logic, Turbo Boost save/restore, prompt timing) and exits (0 = pass). It has no console window, so
  pipe the output to see it: `CoolCharge.exe --selftest 2>&1 | more`
- `--simulate <tempC>` uses a fake CPU temperature instead of the sensor, so you can try the prompts without heating
  the laptop. Example: `CoolCharge.exe --simulate 90`. Expect the first prompt after about 30 seconds,
  because the average needs 6 readings. In this mode the tray menu also has "Simulate 70 °C", "Simulate 87 °C" and
  "Simulate 94 °C" to change the fake temperature while it runs.
- `--restore` restores the saved Turbo Boost setting and exits (0 = success, 1 = failure). The installer uses it.

## What the throttle does

"Turn Turbo Boost off" sets the plugged-in "Processor performance boost mode" to off. On this laptop that cut real
CPU work by about 17 % in a single-thread test. Under a 2.5-minute all-core load it took the CPU from a 99 °C peak
and 24 W average to a 55 °C peak and 10 W, at about half the speed (details in DESIGN.md). Only the plugged-in
value is changed, so battery behaviour stays as it was.

The more common "Maximum processor state" cap is not used: measured on this laptop it had no effect at any
value, even 5 %.

"Restore full speed" puts back the setting saved before Turbo Boost was turned off, into the power plan that was
active at that moment. If Turbo Boost was already off when CoolCharge took over, it restores the Windows default
(Aggressive) instead, never "off". The app also restores it when you choose Exit and at
logoff or shutdown. After a crash or a kill Turbo Boost stays off; the next start shows a warning, and "Restore full
speed" in the tray menu undoes it. The original value and `log.txt` (state changes, actions, plug and sleep events, sensor errors) are kept
next to the installed program in `Program Files\CoolCharge` when it runs as administrator, and in
`%LOCALAPPDATA%\CoolCharge` when it does not.

A prompt's default button is always "Snooze 15 min" (for the restore prompt, "Keep it off"), so a stray Enter never
changes a setting. While the CPU stays warm or hot with Turbo Boost still on, the prompt comes back every 15 minutes.
Snooze timing uses elapsed time, so clock or time-zone changes don't stretch it. Turning Turbo Boost off (from the
prompt or the tray menu) stops the 15-minute Warm reminders; entering Hot still asks. After sleep, unplugging or a
sensor dropout while Turbo Boost is off, the app offers "Restore full speed" once readings are back to Normal.
"End process…" refuses Windows' own critical processes.

## Limits

- It cannot tell a power bank from wall power; both look like USB-C AC power to Windows.
- It cannot measure the power bank's own temperature, only the laptop's CPU.
