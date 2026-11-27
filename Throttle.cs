using System.Runtime.InteropServices;

namespace CoolCharge;

/// Turns Turbo Boost off/on for plugged-in use ("Processor performance boost mode", AC value only), through the
/// Windows power API. It used to launch powercfg.exe; a slow launch then failed the click (seen 2026-11-26).
/// Measured on the development laptop (2026-11-25): boost mode 0 cut real work by ~17 %, while "Maximum processor
/// state" had no effect at any value (even 5 %), so that setting is deliberately not used.
/// The marker remembers the plan as well as the value, so a restore writes back into that plan even if the user
/// switched plans meanwhile, and never switches the active plan itself.
public static class Throttle
{
    private const int BoostOff = 0, WindowsDefault = 2; // 2 = Aggressive

    // Same GUIDs powercfg prints for SUB_PROCESSOR and PERFBOOSTMODE.
    private static readonly Guid SubProcessor = new("54533251-82be-4824-96c1-47b60b740d00");
    private static readonly Guid BoostMode = new("be337238-0d82-4146-a960-4f3749d470c7");

    // SessionEnding can call Restore re-entrantly (inside a dialog's message loop) or from SystemEvents' own thread
    // if there is no UI sync context; the lock covers both. It is reentrant, so keep the guarded code reentrant too.
    private static readonly object Gate = new();

    /// Where the saved setting and the log live. An elevated process must not write into a folder that
    /// non-admin programs control (a planted link there could redirect the write or delete), so when elevated
    /// it is the app's own folder (admin-only once installed to Program Files); otherwise the user's own folder.
    internal static readonly string StateDir = Environment.IsPrivilegedProcess
        ? AppContext.BaseDirectory
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CoolCharge");

    // Test hooks; the live defaults below are what the app uses. Self-test swaps them, then calls ResetTestHooks.
    /// Active plan and its AC boost mode, null on failure.
    internal static Func<(Guid Scheme, int Mode)?> ReadMode = LiveRead;
    /// Writes a mode into a plan's AC index and re-activates the plan only if it is the active one.
    internal static Func<Guid, int, bool> WriteMode = LiveWrite;
    /// False only when Windows says the plan is gone; any other answer counts as "exists" so the marker is kept.
    internal static Func<Guid, bool> PlanExists = LivePlanExists;
    /// Plain text "<scheme>:<mode>" on disk so the original value survives a crash or kill while throttled.
    internal static string SavedFilePath = Path.Combine(StateDir, "saved-boost-mode.txt");

    internal static void ResetTestHooks()
    {
        ReadMode = LiveRead;
        WriteMode = LiveWrite;
        PlanExists = LivePlanExists;
        SavedFilePath = Path.Combine(StateDir, "saved-boost-mode.txt");
    }

    public static bool IsCapped { get { lock (Gate) return File.Exists(SavedFilePath); } }

    /// Why the last Cap/Restore failed, for the log. Empty after a success.
    public static string LastError { get; private set; } = "";

    /// Current AC boost mode of the active plan (0 = off), null on any failure.
    public static int? GetBoostMode() { lock (Gate) return ReadMode()?.Mode; }

    public static bool Cap()
    {
        lock (Gate)
        {
            try
            {
                LastError = "";
                if (IsCapped) return true;
                if (ReadMode() is not (Guid scheme, int current)) return false;
                // Already off (another tool, or a lost marker): saving 0 would make "Restore full speed" a no-op.
                int original = current == BoostOff ? WindowsDefault : current;
                Directory.CreateDirectory(Path.GetDirectoryName(SavedFilePath)!);
                File.WriteAllText(SavedFilePath, $"{scheme}:{original}");
                if (current == BoostOff || WriteMode(scheme, BoostOff)) return true;
                // Apply is two steps; the value may have been written before the failure. Put the original back,
                // and only then drop the marker. If even that fails, keep it so a restore is still offered.
                string why = LastError;
                if (WriteMode(scheme, original)) File.Delete(SavedFilePath);
                LastError = why;
                return false;
            }
            catch (Exception ex) { LastError = "save original setting: " + ex.Message; return false; }
        }
    }

    public static bool Restore()
    {
        lock (Gate)
        {
            try
            {
                LastError = "";
                if (!IsCapped) return true;
                // "<scheme>:<mode>", or a legacy "<mode>" meaning the active plan. Anything unreadable, and 0
                // (a restore must never leave boost off), becomes the Windows default.
                string[] p = File.ReadAllText(SavedFilePath).Trim().Split(':');
                Guid? saved = p.Length == 2 && Guid.TryParse(p[0], out var g) ? g : null;
                int mode = (p.Length == 1 || saved != null) && int.TryParse(p[^1], out var v) && v is >= 1 and <= 6
                    ? v : WindowsDefault;
                if ((saved ?? ReadMode()?.Scheme) is not Guid scheme) return false;
                if (!WriteMode(scheme, mode))
                {
                    // The plan that was throttled was deleted since: nothing is left to restore, so don't keep failing.
                    if (saved is null || PlanExists(scheme)) return false;
                    LastError = $"power plan {scheme} no longer exists; nothing to restore";
                }
                File.Delete(SavedFilePath);
                return true;
            }
            catch (Exception ex) { LastError = "restore: " + ex.Message; return false; }
        }
    }

    private static (Guid, int)? LiveRead()
    {
        try
        {
            if (ActiveScheme() is not Guid scheme) return null;
            Guid sub = SubProcessor, setting = BoostMode;
            uint rc = PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out uint value);
            if (rc != 0) { LastError = $"read boost mode: error {rc}"; return null; }
            if (value > 6) { LastError = $"read boost mode: unexpected value {value}"; return null; }
            return (scheme, (int)value);
        }
        catch (Exception ex) { LastError = "read boost mode: " + ex.Message; return null; }
    }

    /// Writes the value into the plan, then re-activates it if it is the active one so it takes effect now
    /// (what "powercfg /setacvalueindex" + "/setactive" do).
    private static bool LivePlanExists(Guid scheme)
    {
        Guid sub = SubProcessor, setting = BoostMode;
        return PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out _) != ErrorFileNotFound;
    }

    private const uint ErrorFileNotFound = 2; // what the power API returns for a plan GUID that doesn't exist

    private static bool LiveWrite(Guid scheme, int mode)
    {
        Guid sub = SubProcessor, setting = BoostMode;
        uint rc = PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, (uint)mode);
        if (rc != 0) { LastError = $"write boost mode {mode}: error {rc}"; return false; }
        if (ActiveScheme() is not Guid active) return false;
        if (active != scheme) return true;
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
