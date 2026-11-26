using static CoolCharge.ThermalState;

namespace CoolCharge;

/// Assert-based checks for the threshold state machine. Run with `--selftest`; returns 0 when everything passes.
/// Failures go to stderr (one line each), the summary to stdout. Monitor tests poll every 5 s like the app.
public static class SelfTest
{
    private const double Poll = 5; // seconds per sample
    private static int _checks, _failed;
    private static string _case = "";

    public static int Run()
    {
        _checks = _failed = 0;
        (string, Action)[] cases =
        {
            ("Next table (boundaries, hysteresis)", NextTable),
            ("window: no average / no prompt before 6 samples", WindowFill),
            ("window: only the last 6 samples count", WindowSlides),
            ("single spike never leaves Normal", Spikes),
            ("enter Warm / Hot", Entering),
            ("Hot stays Hot above 85, drops to Warm, then needs the full hold", HotBehaviour),
            ("Warm does not clear while average is above 75", WarmHolds),
            ("Warm clears only after 120 s at or below 75", ClearHold),
            ("hold timer restarts when average goes back above 75", ClearRestart),
            ("noisy input around a threshold prompts once, not repeatedly", NoFlapping),
            ("Reset() clears window, timer and state", ResetClears),
        };
        foreach (var (name, body) in cases)
        {
            _case = name;
            try { body(); }
            catch (Exception ex) { _checks++; Fail($"threw {ex.GetType().Name}: {ex.Message}"); }
        }
        Console.Out.WriteLine($"SelfTest: {cases.Length} cases, {_checks} checks, {_failed} failed -> {(_failed == 0 ? "PASS" : "FAIL")}");
        return _failed == 0 ? 0 : 1;
    }

    // ---- tiny harness ------------------------------------------------------------------------------------------
    private static void Fail(string msg) { _failed++; Console.Error.WriteLine($"FAIL [{_case}] {msg}"); }
    private static void Check(bool ok, string what) { _checks++; if (!ok) Fail(what); }
    private static void Eq<T>(T got, T want, string what) =>
        Check(EqualityComparer<T>.Default.Equals(got, want), $"{what}: got {got}, want {want}");

    private static ThermalState Feed(ThermalMonitor m, double temp, int n)
    {
        var s = m.State;
        for (int i = 0; i < n; i++) s = m.Update(temp, Poll);
        return s;
    }

    private static List<ThermalState> Trace(ThermalMonitor m, IEnumerable<double> temps) =>
        temps.Select(t => m.Update(t, Poll)).ToList();

    /// Number of updates that moved to a more severe state, i.e. the times the app would prompt.
    private static int Entries(ThermalMonitor m, IEnumerable<double> temps)
    {
        int n = 0;
        foreach (double t in temps) { var before = m.State; if (m.Update(t, Poll) > before) n++; }
        return n;
    }

    private static ThermalMonitor InState(double steadyTemp, ThermalState want)
    {
        var m = new ThermalMonitor();
        Feed(m, steadyTemp, Thermal.WindowSamples);
        Eq(m.State, want, $"setup: {Thermal.WindowSamples} x {steadyTemp} C");
        return m;
    }

    private static IEnumerable<double> Const(double t, int n) => Enumerable.Repeat(t, n);

    /// Alternating blocks of 6 samples (30 s): a for 6, b for 6, repeated.
    private static IEnumerable<double> Blocks(double a, double b, int cycles) =>
        Enumerable.Range(0, cycles * 12).Select(i => i % 12 < 6 ? a : b);

    // ---- cases -------------------------------------------------------------------------------------------------
    private static void NextTable()
    {
        Eq(Thermal.WindowSamples, 6, "window size the tests assume");
        (ThermalState from, double avg, double held, ThermalState want)[] rows =
        {
            // Normal: Warm at exactly 85, Hot at exactly 92; a stale hold value changes nothing
            (Normal, 0, 0, Normal), (Normal, 75, 0, Normal), (Normal, 84.99, 999, Normal),
            (Normal, 85, 0, Warm), (Normal, 85.01, 0, Warm), (Normal, 91.99, 0, Warm),
            (Normal, 92, 0, Hot), (Normal, 100, 0, Hot),
            // Warm: no clear above 75 however long, none under 120 s, clear at exactly 75 C / 120 s
            (Warm, 80, 999, Warm), (Warm, 85, 0, Warm), (Warm, 91.99, 0, Warm), (Warm, 92, 0, Hot),
            (Warm, 75.01, 999, Warm), (Warm, 75, 0, Warm), (Warm, 75, 119.99, Warm), (Warm, 60, 119.99, Warm),
            (Warm, 75, 120, Normal), (Warm, 60, 120, Normal), (Warm, 75, 500, Normal),
            // Hot: stays between 85 and 92, drops to Warm at <= 85 and never straight to Normal
            (Hot, 100, 0, Hot), (Hot, 92, 0, Hot), (Hot, 91.99, 0, Hot), (Hot, 88, 0, Hot), (Hot, 85.01, 0, Hot),
            (Hot, 85, 0, Warm), (Hot, 84.99, 0, Warm), (Hot, 75, 999, Warm), (Hot, 0, 999, Warm),
        };
        foreach (var r in rows)
            Eq(Thermal.Next(r.from, r.avg, r.held), r.want, $"Next({r.from}, avg {r.avg}, held {r.held})");
    }

    private static void WindowFill()
    {
        var m = new ThermalMonitor();
        Eq(m.State, Normal, "fresh monitor state");
        Check(m.Average is null, "fresh monitor has no average");
        for (int i = 1; i <= 5; i++)
        {
            var s = m.Update(100, Poll); // would be Hot if judged on its own
            Check(s == Normal && m.State == Normal && m.Average is null, $"sample {i} of 100 C: Normal, no average");
        }
        Eq(m.Update(100, Poll), Hot, "6th sample of 100 C");
        Eq(m.Average, (double?)100, "average once full");
    }

    private static void WindowSlides()
    {
        var m = new ThermalMonitor();
        Feed(m, 100, 1);
        Feed(m, 60, 6); // the 100 has left the window
        Eq(m.Average, (double?)60, "average after the 100 C sample left the window");
        Eq(m.State, Normal, "state after the spike left");
    }

    private static void Spikes()
    {
        // one 100 C sample among 60 C, at the end of the first window, mid-stream, and right after warm-up
        Check(Trace(new ThermalMonitor(), Const(60, 5).Append(100).Concat(Const(60, 12))).All(s => s == Normal),
            "spike as 6th sample");
        Check(Trace(new ThermalMonitor(), Const(60, 2).Append(100).Concat(Const(60, 12))).All(s => s == Normal),
            "spike as 3rd sample");
        Check(Trace(new ThermalMonitor(), Const(60, 20).Append(100).Concat(Const(60, 12))).All(s => s == Normal),
            "spike after long idle");
        // turbo bursts: 100/60 alternating averages 80
        Check(Trace(new ThermalMonitor(), Enumerable.Range(0, 100).Select(i => i % 2 == 0 ? 100.0 : 60.0))
            .All(s => s == Normal), "alternating 100/60 stays Normal");
        // a spike on top of a Warm baseline must not jump to Hot: (5 x 86 + 100) / 6 = 88.3
        Eq(Feed(InState(86, Warm), 100, 1), Warm, "single 100 C sample while Warm");
        // ...and while Hot, a single cold sample must not drop it to Warm: (5 x 93 + 60) / 6 = 87.5
        Eq(Feed(InState(93, Hot), 60, 1), Hot, "single 60 C sample while Hot");
    }

    private static void Entering()
    {
        // Warm: needs the full window; exact boundary 85 (also as a mixed window: 3 x 80 + 3 x 90 = 85)
        var t = Trace(new ThermalMonitor(), Const(86, 6));
        Check(t.Take(5).All(s => s == Normal) && t[5] == Warm, "6 x 86: Normal for 5 samples, Warm on the 6th");
        Eq(Feed(new ThermalMonitor(), 84.9, 12), Normal, "sustained 84.9 C");
        Eq(Feed(new ThermalMonitor(), 85, 6), Warm, "sustained 85 C (boundary)");
        Eq(Trace(new ThermalMonitor(), new double[] { 80, 90, 80, 90, 80, 90 })[5], Warm, "mixed window averaging exactly 85");
        // Hot: boundary 92, from Normal and from Warm
        Eq(Feed(new ThermalMonitor(), 91.9, 12), Warm, "sustained 91.9 C");
        Eq(Feed(new ThermalMonitor(), 92, 6), Hot, "sustained 92 C (boundary)");
        t = Trace(new ThermalMonitor(), Const(93, 6));
        Check(t.Take(5).All(s => s == Normal) && t[5] == Hot, "6 x 93 from Normal: Hot on the 6th");
        var m = InState(86, Warm);
        // 86 -> 93 raises the average 7/6 C per sample, so it crosses 92 on the 6th
        Check(Trace(m, Const(93, 6)) is [Warm, Warm, Warm, Warm, Warm, Hot], "6 x 93 from Warm: Hot on the 6th");
    }

    private static void HotBehaviour()
    {
        var m = InState(93, Hot);
        Eq(Feed(m, 88, 30), Hot, "Hot stays Hot while average is 85..92 (88 C)");
        Eq(Feed(m, 90, 30), Hot, "still Hot at 90 C");
        // 93 -> 85: average is 86.33 after 5 samples (still Hot), exactly 85 after 6 (Warm)
        m = InState(93, Hot);
        Check(Trace(m, Const(85, 6)) is [Hot, Hot, Hot, Hot, Hot, Warm], "Hot -> Warm exactly when average reaches 85");
        // Hot -> Warm -> Normal takes the full hold. 6 x 93 then 50 C: average 85.8 (Hot), 78.7 (Warm),
        // 71.5 on the 3rd sample = first at or below 75, so Normal on the 3rd + 23 = 26th.
        var t = Trace(InState(93, Hot), Const(50, 26));
        Eq(t[0], Hot, "1st cold sample (avg 85.8)");
        Check(t.Skip(1).Take(24).All(s => s == Warm), "samples 2..25 are Warm, never Normal");
        Eq(t[25], Normal, "26th cold sample completes the hold");
    }

    private static void WarmHolds()
    {
        var m = InState(86, Warm);
        Check(Trace(m, Const(80, 100)).All(s => s == Warm), "Warm never clears at 80 C (500 s)");
        Eq(Feed(m, 76, 200), Warm, "Warm never clears at 76 C");
        Eq(Feed(m, 84, 50), Warm, "Warm does not re-prompt or change between 75 and 85");
    }

    private static void ClearHold()
    {
        // 6 x 86 then 75 C: average 76.8 after 5 samples, exactly 75 on the 6th = start of the hold (timer 5 s).
        // Timer reaches 120 s on the 6th + 23 = 29th sample.
        var m = InState(86, Warm);
        Eq(Feed(m, 75, 28), Warm, "28 samples of 75 C (115 s)");
        Eq(Feed(m, 75, 1), Normal, "29th sample of 75 C (120 s)");
        // Realistic cool-down to 70 C: average passes 75 on the 5th sample (75.3 then 72.7), Normal on the 28th.
        m = InState(86, Warm);
        Eq(Feed(m, 70, 27), Warm, "27 samples of 70 C");
        Eq(Feed(m, 70, 1), Normal, "28th sample of 70 C");
        // The caller supplies elapsed time, so the hold works with uneven polls: 5 + 114 s is not enough, +1 is.
        m = InState(86, Warm);
        Feed(m, 75, 6);
        Eq(m.Update(75, 114), Warm, "119 s held");
        Eq(m.Update(75, 1), Normal, "120 s held");
        Eq(Feed(m, 70, 50), Normal, "stays Normal when cool");
    }

    private static void ClearRestart()
    {
        // Hold for 115 s (one sample short), then the average goes just above 75 for one sample: timer restarts.
        var m = InState(86, Warm);
        Eq(Feed(m, 75, 28), Warm, "115 s held");
        Eq(m.Update(80, Poll), Warm, "one 80 C sample: average 75.83, timer back to 0");
        Eq(m.Update(70, Poll), Warm, "70 C: average 75.0, timer 5 s");
        Eq(Feed(m, 75, 22), Warm, "another 22 samples (115 s since the restart)");
        Eq(Feed(m, 75, 1), Normal, "full 120 s since the restart");
        // Same idea but the average climbs well above 75 for a while.
        m = InState(86, Warm);
        Feed(m, 70, 20); // hold running
        Eq(Feed(m, 80, 12), Warm, "heating back up to 80 C");
        // 80 -> 70: average is exactly 75 on the 3rd sample (the hold starts), so Normal on the 3rd + 23 = 26th
        Eq(Feed(m, 70, 25), Warm, "25 cool samples after the interruption");
        Eq(Feed(m, 70, 1), Normal, "26th cool sample after the interruption");
    }

    private static void NoFlapping()
    {
        // each of these must prompt exactly once (Normal -> Warm / Hot) and never clear on the way
        var m = new ThermalMonitor();
        Eq(Entries(m, Blocks(86, 70, 30)), 1, "86/70 C in 30 s blocks: entries");
        Eq(m.State, Warm, "86/70 C blocks end Warm");
        m = new ThermalMonitor();
        Eq(Entries(m, Blocks(86, 80, 30)), 1, "86/80 C blocks: entries");
        m = new ThermalMonitor();
        Eq(Entries(m, Enumerable.Range(0, 200).Select(i => i % 2 == 0 ? 84.0 : 86.0)), 1, "84/86 C alternating (avg 85): entries");
        Eq(m.State, Warm, "84/86 C alternating ends Warm");
        m = new ThermalMonitor();
        Eq(Entries(m, Blocks(93, 90, 30)), 1, "93/90 C blocks: entries");
        Eq(m.State, Hot, "93/90 C blocks end Hot");
        m = new ThermalMonitor();
        Eq(Entries(m, Enumerable.Range(0, 200).Select(i => i % 2 == 0 ? 91.0 : 93.0)), 1, "91/93 C alternating (avg 92): entries");
        Eq(m.State, Hot, "91/93 C alternating ends Hot");
    }

    private static void ResetClears()
    {
        // state and window
        var m = InState(93, Hot);
        m.Reset();
        Eq(m.State, Normal, "state after Reset from Hot");
        Check(m.Average is null, "average after Reset");
        for (int i = 1; i <= 5; i++)
            Check(m.Update(100, Poll) == Normal, $"sample {i} after Reset: window refilling, no prompt");
        Eq(m.Update(100, Poll), Hot, "6th sample after Reset");

        // 75 s into a hold, Reset, warm up again: the hold must start from zero. (A stale timer would not show
        // here anyway, because heating up to Warm resets it; this pins the user-visible behaviour.)
        m = InState(86, Warm);
        Feed(m, 75, 20);
        m.Reset();
        Eq(m.State, Normal, "state after Reset from Warm");
        Feed(m, 86, 6);
        Eq(m.State, Warm, "Warm again after Reset + 6 hot samples (prompts again)");
        Eq(Feed(m, 75, 28), Warm, "115 s held after Reset");
        Eq(Feed(m, 75, 1), Normal, "120 s held after Reset");
    }
}
