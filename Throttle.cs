using System.Runtime.InteropServices;

namespace CoolCharge;

/// Turns Turbo Boost off/on for plugged-in use ("Processor performance boost mode", AC value only), through the
/// Windows power API. It used to launch powercfg.exe; a slow launch then failed the click (seen 2026-11-26).
/// Measured on the Latitude 7420 (2026-11-25): boost mode 0 cut real work by ~17 %, while "Maximum processor
/// state" had no effect at any value (even 5 %), so that setting is deliberately not used.
// shortcut: assumes the active power plan doesn't change while throttled (this laptop only has Balanced);
// if plans get switched, save the scheme GUID with the value and restore to that GUID.
public static class Throttle
{
    private const int BoostOff = 0, WindowsDefault = 2; // 2 = Aggressive

    // Same GUIDs powercfg prints for SUB_PROCESSOR and PERFBOOSTMODE.
    private static readonly Guid SubProcessor = new("54533251-82be-4824-96c1-47b60b740d00");
    private static readonly Guid BoostMode = new("be337238-0d82-4146-a960-4f3749d470c7");

    /// Where the saved setting and the log live. An elevated process must not write into a folder that
    /// non-admin programs control (a planted link there could redirect the write or delete), so when elevated
    /// it is the app's own folder (admin-only once installed to Program Files); otherwise the user's own folder.
    internal static readonly string StateDir = Environment.IsPrivilegedProcess
        ? AppContext.BaseDirectory
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CoolCharge");

    // Plain text on disk so the original value survives a crash or kill while throttled.
    private static readonly string SavedPath = Path.Combine(StateDir, "saved-boost-mode.txt");

    public static bool IsCapped => File.Exists(SavedPath);

    /// Why the last Cap/Restore failed, for the log. Empty after a success.
    public static string LastError { get; private set; } = "";

    /// Current AC boost mode (0 = off), null on any failure.
    public static int? GetBoostMode()
    {
        try
        {
            if (ActiveScheme() is not Guid scheme) return null;
            Guid sub = SubProcessor, setting = BoostMode;
            uint rc = PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out uint value);
            if (rc != 0) { LastError = $"read boost mode: error {rc}"; return null; }
            if (value > 6) { LastError = $"read boost mode: unexpected value {value}"; return null; }
            return (int)value;
        }
        catch (Exception ex) { LastError = "read boost mode: " + ex.Message; return null; }
    }

    public static bool Cap()
    {
        try
        {
            LastError = "";
            if (IsCapped) return true;
            if (GetBoostMode() is not int original) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(SavedPath)!);
            File.WriteAllText(SavedPath, original.ToString());
            if (Apply(BoostOff)) return true;
            // Apply is two steps; the value may have been written before the failure. Put the original back,
            // and only then drop the marker. If even that fails, keep it so a restore is still offered.
            string why = LastError;
            if (Apply(original)) File.Delete(SavedPath);
            LastError = why;
            return false;
        }
        catch (Exception ex) { LastError = "save original setting: " + ex.Message; return false; }
    }

    public static bool Restore()
    {
        try
        {
            LastError = "";
            if (!IsCapped) return true;
            int original = int.TryParse(File.ReadAllText(SavedPath).Trim(), out var v) && v is >= 0 and <= 6
                ? v : WindowsDefault;
            if (!Apply(original)) return false;
            File.Delete(SavedPath);
            return true;
        }
        catch (Exception ex) { LastError = "restore: " + ex.Message; return false; }
    }

    /// Writes the value into the active plan, then re-activates the plan so it takes effect now
    /// (what "powercfg /setacvalueindex" + "/setactive" do).
    private static bool Apply(int mode)
    {
        if (ActiveScheme() is not Guid scheme) return false;
        Guid sub = SubProcessor, setting = BoostMode;
        uint rc = PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, (uint)mode);
        if (rc != 0) { LastError = $"write boost mode {mode}: error {rc}"; return false; }
        rc = PowerSetActiveScheme(IntPtr.Zero, ref scheme);
        if (rc != 0) { LastError = $"activate power plan: error {rc}"; return false; }
        return true;
    }

    private static Guid? ActiveScheme()
    {
        uint rc = PowerGetActiveScheme(IntPtr.Zero, out IntPtr p);
        if (rc != 0) { LastError = $"get active power plan: error {rc}"; return null; }
        try { return Marshal.PtrToStructure<Guid>(p); }
        finally { LocalFree(p); }
    }

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(IntPtr rootKey, out IntPtr schemeGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadACValueIndex(IntPtr rootKey, ref Guid scheme, ref Guid subGroup, ref Guid setting, out uint value);

    [DllImport("powrprof.dll")]
    private static extern uint PowerWriteACValueIndex(IntPtr rootKey, ref Guid scheme, ref Guid subGroup, ref Guid setting, uint value);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveScheme(IntPtr rootKey, ref Guid scheme);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr mem);
}
