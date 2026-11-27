namespace CoolCharge;

/// Throttle checks. They run against test hooks, never the live power plan or the real state folder.
public static partial class SelfTest
{
    private static (string, Action)[] ThrottleCases =>
    [
        ("throttle: cap saves plan:original and writes 0, restore puts it back", CapThenRestore),
        ("throttle: boost already off saves 2, never 0", CapWhenAlreadyOff),
        ("throttle: restore targets the saved plan after a plan switch", RestoreSavedPlan),
        ("throttle: legacy single-digit marker means the active plan", LegacyMarker),
        ("throttle: corrupt, empty or out-of-range marker restores 2", CorruptMarker),
        ("throttle: failed apply rolls back and drops the marker", ApplyFailureRollsBack),
        ("throttle: failed rollback keeps the marker", RollbackFailureKeepsMarker),
        ("throttle: restore when not capped is a no-op", RestoreNotCapped),
        ("throttle: read failure caps nothing", ReadFailure),
        ("throttle: saved plan deleted since: marker dropped, other plans untouched", SavedPlanDeleted),
    ];

    private static void SavedPlanDeleted() => WithFakePower(f =>
    {
        Check(Throttle.Cap(), "cap on plan A");
        f.Active = PlanB;
        f.Modes.Remove(PlanA); // the user deleted the throttled plan
        Check(Throttle.Restore(), "restore succeeds: nothing left to restore");
        Check(!File.Exists(f.Marker), "marker dropped");
        Eq(f.Modes[PlanB], 3, "plan B untouched");
        // A write failure on a plan that still exists keeps the marker.
        f.Modes[PlanA] = 2;
        Check(Throttle.Cap(), "cap on plan B");
        f.WriteFails = (_, _) => true;
        Check(!Throttle.Restore(), "restore fails while the plan exists");
        Check(File.Exists(f.Marker), "marker kept");
    });

    private static readonly Guid PlanA = new("381b4222-f694-41f0-9685-ff5bb260df2e"),
        PlanB = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");

    /// In-memory power plans: scheme -> AC boost mode, plus the active plan and every write made.
    private sealed class FakePower
    {
        public readonly Dictionary<Guid, int> Modes = new() { [PlanA] = 2, [PlanB] = 3 };
        public Guid Active = PlanA;
        public bool ReadFails;
        public Func<Guid, int, bool> WriteFails = (_, _) => false;
        public readonly List<(Guid, int)> Writes = new();
        public string Marker = "";
        public string Log => string.Join(" ", Writes.Select(w => $"{(w.Item1 == PlanA ? "A" : "B")}={w.Item2}"));
    }

    private static void WithFakePower(Action<FakePower> body)
    {
        string dir = Path.Combine(Path.GetTempPath(), "CoolCharge-selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var f = new FakePower { Marker = Path.Combine(dir, "saved-boost-mode.txt") };
        try
        {
            Throttle.SavedFilePath = f.Marker;
            Throttle.ReadMode = () => f.ReadFails ? null : (f.Active, f.Modes[f.Active]);
            // Like the live write, the value lands before a failure can happen (the activate step).
            Throttle.WriteMode = (s, m) =>
            {
                if (!f.Modes.ContainsKey(s)) return false; // like Windows: no such plan
                f.Writes.Add((s, m)); f.Modes[s] = m; return !f.WriteFails(s, m);
            };
            Throttle.PlanExists = s => f.Modes.ContainsKey(s);
            body(f);
        }
        finally
        {
            Throttle.ResetTestHooks();
            Directory.Delete(dir, true);
        }
    }

    private static void CapThenRestore() => WithFakePower(f =>
    {
        Check(Throttle.Cap(), "cap");
        Eq(File.ReadAllText(f.Marker), $"{PlanA}:2", "marker after cap");
        Eq(f.Modes[PlanA], 0, "plan A mode after cap");
        Check(Throttle.IsCapped, "IsCapped after cap");
        Check(Throttle.Cap(), "second cap");
        Eq(f.Log, "A=0", "writes after two caps");
        Check(Throttle.Restore(), "restore");
        Eq(f.Modes[PlanA], 2, "plan A mode after restore");
        Check(!File.Exists(f.Marker) && !Throttle.IsCapped, "marker gone after restore");
        Eq(f.Log, "A=0 A=2", "writes");
    });

    private static void CapWhenAlreadyOff() => WithFakePower(f =>
    {
        f.Modes[PlanA] = 0;
        Check(Throttle.Cap(), "cap");
        Eq(File.ReadAllText(f.Marker), $"{PlanA}:2", "marker saves the Windows default");
        Eq(f.Log, "", "no write needed when already off");
        Check(Throttle.Restore(), "restore");
        Eq(f.Modes[PlanA], 2, "plan A mode after restore");
    });

    private static void RestoreSavedPlan() => WithFakePower(f =>
    {
        Check(Throttle.Cap(), "cap on plan A");
        f.Active = PlanB; // the user switched plans while throttled
        Check(Throttle.Restore(), "restore");
        Eq(f.Modes[PlanA], 2, "plan A mode restored");
        Eq(f.Modes[PlanB], 3, "plan B untouched");
        Eq(f.Active, PlanB, "active plan not switched");
        Eq(f.Log, "A=0 A=2", "writes");
    });

    private static void LegacyMarker() => WithFakePower(f =>
    {
        f.Active = PlanB;
        File.WriteAllText(f.Marker, "4\r\n");
        Check(Throttle.Restore(), "restore legacy 4");
        // a legacy 0 (saved before the fix) must not leave boost off
        File.WriteAllText(f.Marker, "0");
        Check(Throttle.Restore(), "restore legacy 0");
        Eq(f.Log, "B=4 B=2", "writes go to the active plan");
        Check(!File.Exists(f.Marker), "marker gone");
    });

    private static void CorruptMarker() => WithFakePower(f =>
    {
        f.Active = PlanB;
        foreach (var (text, want) in new[]
        {
            ("", "B=2"), ("   ", "B=2"), ("garbage", "B=2"), ("7", "B=2"), ("-1", "B=2"), ("not-a-guid:3", "B=2"),
            ("1:2:3", "B=2"), ($"{PlanA}:9", "A=2"), ($"{PlanA}:0", "A=2"), ($"{PlanA}:", "A=2"),
        })
        {
            f.Writes.Clear();
            File.WriteAllText(f.Marker, text);
            Check(Throttle.Restore(), $"restore '{text}'");
            Eq(f.Log, want, $"write for '{text}'");
            Check(!File.Exists(f.Marker), $"marker gone after '{text}'");
        }
    });

    private static void ApplyFailureRollsBack() => WithFakePower(f =>
    {
        f.WriteFails = (_, m) => m == 0;
        Check(!Throttle.Cap(), "cap reports failure");
        Eq(f.Log, "A=0 A=2", "wrote 0, then put the original back");
        Eq(f.Modes[PlanA], 2, "plan A mode");
        Check(!File.Exists(f.Marker) && !Throttle.IsCapped, "marker removed");
    });

    private static void RollbackFailureKeepsMarker() => WithFakePower(f =>
    {
        f.WriteFails = (_, _) => true;
        Check(!Throttle.Cap(), "cap reports failure");
        Eq(File.ReadAllText(f.Marker), $"{PlanA}:2", "marker kept");
        Check(Throttle.IsCapped, "restore still offered");
        Check(!Throttle.Restore(), "restore fails while writes fail");
        Check(File.Exists(f.Marker), "marker kept after failed restore");
        f.WriteFails = (_, _) => false;
        Check(Throttle.Restore(), "restore once writes work");
        Eq(f.Modes[PlanA], 2, "plan A mode");
        Check(!File.Exists(f.Marker), "marker gone");
    });

    private static void RestoreNotCapped() => WithFakePower(f =>
    {
        Check(Throttle.Restore(), "restore");
        Eq(f.Log, "", "no writes");
        Check(!File.Exists(f.Marker), "no marker");
    });

    private static void ReadFailure() => WithFakePower(f =>
    {
        f.ReadFails = true;
        Eq(Throttle.GetBoostMode(), null, "GetBoostMode");
        Check(!Throttle.Cap(), "cap fails");
        Check(!File.Exists(f.Marker), "no marker");
        Eq(f.Log, "", "no writes");
    });
}
