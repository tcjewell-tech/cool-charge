using System.Diagnostics;

namespace FlightThermalGuard;

public static class TopProcesses
{
    /// Blocking (sleeps intervalMs). CPU % is of total capacity across all logical processors.
    public static List<(int Pid, string Name, double CpuPercent)> Sample(int count = 5, int intervalMs = 2000)
    {
        var procs = Process.GetProcesses();
        try
        {
            int self = Environment.ProcessId;
            var first = new Dictionary<Process, TimeSpan>();
            foreach (var p in procs)
            {
                if (p.Id == 0 || p.Id == self) continue;
                try { first[p] = p.TotalProcessorTime; } catch { } // access denied / exited: expected
            }

            Thread.Sleep(intervalMs);

            var result = new List<(int, string, double)>();
            foreach (var (p, t0) in first)
            {
                try
                {
                    var delta = p.TotalProcessorTime - t0; // Process caches ids, so a recycled pid throws or reads as exited
                    result.Add((p.Id, p.ProcessName, delta.TotalMilliseconds / intervalMs / Environment.ProcessorCount * 100));
                }
                catch { }
            }
            return result.OrderByDescending(r => r.Item3).Take(count).ToList();
        }
        finally
        {
            foreach (var p in procs) p.Dispose();
        }
    }
}
