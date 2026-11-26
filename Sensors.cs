using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;

namespace CoolCharge;

/// CPU temperature via LibreHardwareMonitor (needs admin + the PawnIO driver) and charge rate from Windows.
/// Every method returns null instead of throwing: "no reading" is a normal state (not elevated, driver missing).
public sealed class Sensors : IDisposable
{
    private readonly Computer? _pc;
    private readonly IHardware? _cpu;

    public Sensors()
    {
        try
        {
            _pc = new Computer { IsCpuEnabled = true };
            _pc.Open();
            _cpu = _pc.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
        }
        catch { _pc = null; }
    }

    public double? CpuTempC()
    {
        try
        {
            if (_cpu is null) return null;
            _cpu.Update();
            var temps = _cpu.Sensors.Where(s => s.SensorType == SensorType.Temperature).ToList();
            var sensor = temps.FirstOrDefault(s => s.Name == "CPU Package")
                      ?? temps.FirstOrDefault(s => s.Name == "Core Max");
            // Without admin the sensors exist but have no value; out-of-range values are treated the same way.
            return sensor?.Value is float v && v > 0 && v < 150 ? v : null;
        }
        catch { return null; }
    }

    /// Watts flowing into the battery, or null if not charging / not available.
    // Direct power call instead of WMI: a WMI query every 5 s leaked ~4 handles per poll (measured).
    public static double? ChargeWatts() =>
        CallNtPowerInformation(SystemBatteryState, IntPtr.Zero, 0, out var s, Marshal.SizeOf<BatteryState>()) == 0
        && s.Charging != 0 && s.Rate > 0 ? s.Rate / 1000.0 : null;

    const int SystemBatteryState = 5;

    [StructLayout(LayoutKind.Sequential)]
    struct BatteryState // SYSTEM_BATTERY_STATE; Rate is in mW, positive while charging
    {
        public byte AcOnLine, BatteryPresent, Charging, Discharging, Spare1, Spare2, Spare3, Tag;
        public uint MaxCapacity, RemainingCapacity;
        public int Rate;
        public uint EstimatedTime, DefaultAlert1, DefaultAlert2;
    }

    [DllImport("powrprof.dll")]
    static extern int CallNtPowerInformation(int level, IntPtr inBuf, int inLen, out BatteryState state, int outLen);

    public void Dispose()
    {
        try { _pc?.Close(); } catch { }
    }
}
