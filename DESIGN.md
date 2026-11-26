# CoolCharge — design

Formerly "flight-thermal-guard" / Flight Thermal Guard.

Tray app that watches CPU temperature while the laptop is charging (usually from a
Li-ion power bank on a plane) and, when it runs hot, offers one-click CPU throttling.

## What this laptop actually exposes (probed 2026-11-25)

| Question | Finding | Admin? |
|---|---|---|
| Hardware | Dell Latitude 7420, Intel i7-1185G7 (Tiger Lake, 4C/8T, TjMax 100 °C), Win 11 | — |
| `MSAcpi_ThermalZoneTemperature` (root\wmi) | **"Not supported"**, and the `Thermal Zone Information` perf counter has **no instances**. The Dell BIOS doesn't expose ACPI thermal zones to Windows. Dead end. | — |
| Dell WMI | `root\dcim\sysman` namespace exists but has **no sensor classes**: Dell Command \| Monitor is not installed. Unknown whether installing it would expose CPU temp on a 7420. | — |
| LibreHardwareMonitor | Reads the CPU's own digital thermal sensor (MSR) → per-core + package temp. Current builds use the **PawnIO** driver; the old WinRing0 driver is flagged by Defender as `VulnerableDriver:WinNT/Winring0`, so avoid any build that ships it. **Not yet tested on this laptop.** | **Yes** |
| Charging state | `root\wmi BatteryStatus`: `PowerOnline`, `Charging`, `ChargeRate` (mW). At probe time: online, charging at 19.4 W, battery at 22 %. | No |
| Power bank vs wall? | **Not distinguishable.** Both arrive as USB-C PD "AC power". Windows doesn't reliably report the negotiated wattage. | — |
| Throttle lever (superseded, see "What throttle means") | `powercfg /setacvalueindex SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMAX <n>` + `powercfg /setactive SCHEME_CURRENT`. Tested a no-op write (100→100): **works without admin**. Only plan installed: Balanced. Current AC power-mode overlay `961cc777…` (believed to be "Best power efficiency"). | No |
| Top CPU processes | `Process.TotalProcessorTime` sampled twice ~2 s apart → current % per process (cumulative CPU time alone is misleading). One `Get-Process` pass ≈ 0.2 s. | No |

**Bottom line:** CPU temperature needs LibreHardwareMonitor, which needs admin. Everything else runs as a normal user.

## Thresholds (i7-1185G7, TjMax 100 °C)

Raw CPU temps on Intel jump 15–20 °C within a second of a turbo burst, so the app judges a **30 s rolling average** of
CPU *package* temperature (poll every 5 s, 6 samples), never single samples.

| State | Enter when | Leave when | Action |
|---|---|---|---|
| Normal | — | avg ≥ 85 °C | Tray icon shows temp only |
| Warm | avg ≥ 85 °C | avg ≤ 75 °C for 2 min | Prompt: **Turn Turbo Boost off** / **Show top processes** / **Snooze 15 min** |
| Hot | avg ≥ 92 °C | avg ≤ 85 °C | Prompt again even if snoozed |
| Cooled (after a cap) | Back to Normal with cap active | — | Prompt: **Restore full speed?** |

A 10 °C gap plus the 2-min hold is the hysteresis, so it can't flap around one number.
Monitoring and prompts run **only while `PowerOnline` is true** (the case that loads the power bank).

## What "throttle" means (revised after measuring, 2026-11-25)

The original plan was to cap "Maximum processor state" (PROCTHROTTLEMAX) at 99 % or 70 %. **Measured on this laptop,
that setting does nothing**: the same amount of work got done in 5 s at 100 %, 30 % and 5 %. Energy preference
(PERFEPP 100) and switching off autonomous mode did nothing either. The laptop's firmware and Intel's thermal
service (`esifsvc`) decide the speed.

The one setting that worked is **"Processor performance boost mode" (PERFBOOSTMODE) = 0**, i.e. Turbo Boost off:
real work dropped about 17 % (243 -> 202 units). So the app has a single action:

1. **Turn Turbo Boost off** (AC value only, original saved to disk first).
2. **Show top processes**: the top 5 by current CPU %, with a kill button *only* after a confirm.
3. **Restore full speed**: put back the saved value. Also on Exit, logoff and shutdown.

Measured 2026-11-26 (all 8 threads loaded for 2.5 min, plugged in, one run each, ON first):

| | Turbo Boost on | Turbo Boost off |
|---|---|---|
| Peak CPU temperature | 99 °C | 55 °C |
| Average CPU power | 24.2 W (48 W in the first seconds) | 10.2 W |
| Last 90 s: temperature / power | 66 °C / 18.0 W | 45 °C / 10.3 W |
| Work done | 1513 units | 808 units (53 %) |

With Turbo Boost on, the firmware itself pulled the CPU back from 48 W to about 15 W over the run, so the gap
narrows on long sustained loads; the big win is removing the 97-99 °C opening spike. Off, the CPU held about
10 W and 45 °C throughout, at roughly half the speed.

## Proposed stack (minimal)

- **C# / .NET 8 WinForms**, one `NotifyIcon` (tray text such as `CPU 78 °C · charging 19 W`, icon colour = state),
  prompts as top-most dialogs (balloon tips can't carry buttons, and real toasts would add a dependency).
- **LibreHardwareMonitorLib** (NuGet, PawnIO build) for the temperature, plus plain WMI for charging, plus `powercfg.exe` to throttle.
- **Autostart elevated** through a Task Scheduler task with "Run with highest privileges" at logon, so there's no UAC
  prompt every boot.
- Roughly 200 lines in one `Program.cs` + an assert-based self-test of the threshold state machine.
- Estimated overhead: about 30–40 MB RAM and well under 1 % CPU at a 5 s poll (estimate, not measured).

Rejected: **Python** (pystray + pythonnet + LHM DLL means more moving parts, and a fragile interpreter path under an
elevated scheduled task). **Plain LHM GUI** (it shows the temp in the tray but can't prompt or throttle; it's fine as a
zero-code stopgap).

## Prerequisites (need Tom's OK; none done yet)

- Install the **.NET 8 SDK** (only the runtimes are present now): `winget install Microsoft.DotNet.SDK.8`.
- **PawnIO driver**: one-time admin install, a kernel driver from a third party. That's the trade-off for getting a temp at all.
- Verify LHM actually reports `CPU Package` on this 7420 **before** building the rest.

## Open questions for Tom

1. Prompt-only, or **auto-cap at Hot** (92 °C) if nobody answers within ~1 min (asleep on the flight)?
2. OK with the PawnIO kernel driver + elevated autostart? If not, the fallback is to try Dell Command | Monitor
   (Dell-signed, unverified coverage).
3. Worth showing charge watts (`ChargeRate`) as a power-bank-load proxy? It's free to read. The pack's own temperature
   can't be measured from the laptop.

Out of scope, but a one-time win: Dell BIOS / Dell Power Manager "Cool" thermal mode and a battery charge limit both
cut heat and pack draw with no app at all.
