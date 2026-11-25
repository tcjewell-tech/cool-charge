using System.Management;
using LibreHardwareMonitor.Hardware;

namespace FlightThermalGuard;

/// CPU temperature via LibreHardwareMonitor (needs admin + the PawnIO driver) and charge rate via WMI.
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
    public static double? ChargeWatts()
    {
        try
        {
            using var search = new ManagementObjectSearcher(@"root\wmi", "SELECT ChargeRate FROM BatteryStatus");
            foreach (ManagementBaseObject o in search.Get())
                using (o)
                {
                    double mw = Convert.ToDouble(o["ChargeRate"]);
                    if (mw > 0) return mw / 1000;
                }
        }
        catch { }
        return null;
    }

    public void Dispose()
    {
        try { _pc?.Close(); } catch { }
    }
}
