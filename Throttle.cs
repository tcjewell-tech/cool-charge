using System.Diagnostics;
using System.Text.RegularExpressions;

namespace FlightThermalGuard;

/// Turns Turbo Boost off/on for plugged-in use via powercfg ("Processor performance boost mode", AC value only).
/// Measured on the Latitude 7420 (2026-11-25): boost mode 0 cut real work by ~17 %, while "Maximum processor
/// state" had no effect at any value (even 5 %), so that setting is deliberately not used.
// shortcut: assumes the active power plan doesn't change while throttled (this laptop only has Balanced);
// if plans get switched, save the scheme GUID with the value and restore to that GUID.
public static class Throttle
{
    private const string Setting = "SCHEME_CURRENT SUB_PROCESSOR PERFBOOSTMODE";
    private const int BoostOff = 0, WindowsDefault = 2; // 2 = Aggressive

    /// Where the saved setting and the log live. An elevated process must not write into a folder that
    /// non-admin programs control (a planted link there could redirect the write or delete), so when elevated
    /// it is the app's own folder (admin-only once installed to Program Files); otherwise the user's own folder.
    internal static readonly string StateDir = Environment.IsPrivilegedProcess
        ? AppContext.BaseDirectory
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FlightThermalGuard");

    // Plain text on disk so the original value survives a crash or kill while throttled.
    private static readonly string SavedPath = Path.Combine(StateDir, "saved-boost-mode.txt");

    public static bool IsCapped => File.Exists(SavedPath);

    /// Current AC boost mode (0 = off), null on any failure.
    public static int? GetBoostMode()
    {
        try
        {
            // /qh because the setting is hidden by default. Locale-independent: the last two hex values
            // are the "Current AC" then "Current DC" index.
            if (Run("/qh " + Setting) is not string text) return null;
            var hex = Regex.Matches(text, @"0x([0-9a-fA-F]{8})");
            if (hex.Count < 2) return null;
            int v = Convert.ToInt32(hex[^2].Groups[1].Value, 16);
            return v is >= 0 and <= 6 ? v : null;
        }
        catch { return null; }
    }

    public static bool Cap()
    {
        try
        {
            if (IsCapped) return true;
            if (GetBoostMode() is not int original) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(SavedPath)!);
            File.WriteAllText(SavedPath, original.ToString());
            if (Apply(BoostOff)) return true;
            // Apply is two steps; the value may have been written before the failure. Put the original back,
            // and only then drop the marker. If even that fails, keep it so a restore is still offered.
            if (Apply(original)) File.Delete(SavedPath);
            return false;
        }
        catch { return false; }
    }

    public static bool Restore()
    {
        try
        {
            if (!IsCapped) return true;
            int original = int.TryParse(File.ReadAllText(SavedPath).Trim(), out var v) && v is >= 0 and <= 6
                ? v : WindowsDefault;
            if (!Apply(original)) return false;
            File.Delete(SavedPath);
            return true;
        }
        catch { return false; }
    }

    private static bool Apply(int mode) =>
        Run($"/setacvalueindex {Setting} {mode}") != null
        && Run("/setactive SCHEME_CURRENT") != null;

    /// Returns stdout, or null on non-zero exit, timeout or launch failure.
    private static string? Run(string args)
    {
        using var p = Process.Start(new ProcessStartInfo("powercfg.exe", args)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
        });
        if (p == null) return null;
        var output = p.StandardOutput.ReadToEndAsync(); // async so a hung child can't block past the timeout
        if (!p.WaitForExit(5000))
        {
            try { p.Kill(); } catch { }
            return null;
        }
        return p.ExitCode == 0 ? output.Result : null;
    }
}
