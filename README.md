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

## Thresholds

The app judges a 30 second average (6 readings, one every 5 s), never a single reading.

- Normal to Warm: average reaches 85 C. Back to Normal only after the average stays at or below 75 C for 2 minutes.
- Warm to Hot: average reaches 92 C. Hot drops to Warm when the average falls to 85 C or lower.
- Monitoring and prompts only happen while the laptop is on AC power.

Details and reasoning are in [DESIGN.md](DESIGN.md).

## Command-line flags

- `--selftest` runs the built-in checks of the threshold logic and exits (0 = pass). It has no console window, so
  pipe the output to see it: `CoolCharge.exe --selftest 2>&1 | more`
- `--simulate <tempC>` uses a fake CPU temperature instead of the sensor, so you can try the prompts without heating
  the laptop. Example: `CoolCharge.exe --simulate 90`. Expect the first prompt after about 30 seconds,
  because the average needs 6 readings. In this mode the tray menu also has "Simulate 70 °C", "Simulate 87 °C" and
  "Simulate 94 °C" to change the fake temperature while it runs.

## What the throttle does

"Turn Turbo Boost off" sets the plugged-in "Processor performance boost mode" to off. On this laptop that cut real
CPU work by about 17 % in a test; how much it lowers temperature has not been measured yet. Only the plugged-in
value is changed, so battery behaviour stays as it was.

The more common "Maximum processor state" cap is not used: measured on this Latitude 7420 it had no effect at any
value, even 5 %.

"Restore full speed" puts back the value that was saved before. The app also restores it when you choose Exit and at
logoff or shutdown. After a crash or a kill Turbo Boost stays off; the next start shows a warning, and "Restore full
speed" in the tray menu undoes it. The original value and `log.txt` (one line per state change or action) are kept
next to the installed program in `Program Files\CoolCharge` when it runs as administrator, and in
`%LOCALAPPDATA%\CoolCharge` when it does not.

A prompt's default button is always "Snooze 15 min", so a stray Enter never changes a setting. While the CPU stays
warm or hot with Turbo Boost still on, the prompt comes back every 15 minutes.

## Limits

- It cannot tell a power bank from wall power; both look like USB-C AC power to Windows.
- It cannot measure the power bank's own temperature, only the laptop's CPU.
