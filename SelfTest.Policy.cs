using static CoolCharge.PromptPolicy.Prompt;
using static CoolCharge.ThermalState;

namespace CoolCharge;

/// PromptPolicy checks (which prompt is due, snooze, restore offers). Times are monotonic milliseconds.
public static partial class SelfTest
{
    private static (string, Action)[] PolicyCases =>
    [
        ("policy: snooze is exactly 15 min of monotonic time", PolicySnooze),
        ("policy: entering Hot asks even while Warm is snoozed", PolicyHotOverridesSnooze),
        ("policy: Turbo Boost off (tray menu or prompt) stops Warm reminders, Hot still asks", PolicyTurboOffQuiets),
        ("policy: real cool-down offers restore once", PolicyCoolDownRestore),
        ("policy: after sleep/unplug/no reading, restore waits for a full Normal window, once", PolicyResetRestore),
        ("policy: no restore offer when not capped", PolicyNoRestoreUncapped),
        ("policy: stale heat prompt: cap ignored, schedule not overwritten", PolicyStalePrompt),
        ("policy: Warm -> Hot while the Warm prompt is open keeps the Hot prompt owed", PolicyWarmToHotOpen),
    ];

    private const long Min = 60_000, Snooze = PromptPolicy.SnoozeMs;
    private const double CoolAvg = 70; // a full window, well below the 75 °C clear line

    /// Show the heat prompt and answer it with Snooze (or close) at `now`.
    private static void SnoozeAt(PromptPolicy p, ThermalState s, long now, bool capped) =>
        p.HeatClosed(p.HeatShown(), s == Hot, s, now, capped);

    private static void PolicySnooze()
    {
        Eq(Snooze, 15 * Min, "SnoozeMs");
        // Only differences of "now" matter: the same sequence at a tiny and a huge base behaves the same.
        foreach (long t0 in new[] { 0L, 1_000_000_000_000L })
        {
            var p = new PromptPolicy();
            Eq(p.Next(Normal, CoolAvg, t0, false), None, $"[{t0}] Normal: nothing due");
            p.StateChanged(Normal, Warm, t0, false);
            Eq(p.Next(Warm, CoolAvg, t0, false), Heat, $"[{t0}] entering Warm: due now");
            SnoozeAt(p, Warm, t0, false);
            Eq(p.Next(Warm, CoolAvg, t0, false), None, $"[{t0}] right after snooze");
            Eq(p.Next(Warm, CoolAvg, t0 + Snooze - 1, false), None, $"[{t0}] 1 ms before 15 min");
            Eq(p.Next(Warm, CoolAvg, t0 + Snooze - 1, false), None, $"[{t0}] asking again changes nothing");
            Eq(p.Next(Warm, CoolAvg, t0 + Snooze, false), Heat, $"[{t0}] exactly 15 min");
            Eq(p.Next(Hot, CoolAvg, t0 + Snooze, false), Heat, $"[{t0}] still due if Hot now");
            SnoozeAt(p, Warm, t0 + Snooze + 7, false);
            Eq(p.Next(Warm, CoolAvg, t0 + 2 * Snooze + 6, false), None, $"[{t0}] second snooze, 1 ms early");
            Eq(p.Next(Warm, CoolAvg, t0 + 2 * Snooze + 7, false), Heat, $"[{t0}] second snooze counts from its answer");
        }
    }

    private static void PolicyHotOverridesSnooze()
    {
        var p = new PromptPolicy();
        p.StateChanged(Normal, Warm, 0, false);
        SnoozeAt(p, Warm, 0, false);
        p.StateChanged(Warm, Hot, Min, false);
        Eq(p.Next(Hot, CoolAvg, Min, false), Heat, "Warm -> Hot inside the snooze: due now");
        SnoozeAt(p, Hot, Min, false);
        p.StateChanged(Hot, Warm, 2 * Min, false);
        Eq(p.Next(Warm, CoolAvg, 2 * Min, false), None, "Hot -> Warm keeps the snooze");
        Eq(p.Next(Warm, CoolAvg, Min + Snooze, false), Heat, "...until it runs out");
        p = new PromptPolicy();
        p.StateChanged(Normal, Hot, 5, false);
        Eq(p.Next(Hot, CoolAvg, 5, false), Heat, "Normal -> Hot: due now");
    }

    private static void PolicyTurboOffQuiets()
    {
        // Tray menu: snoozed Warm prompt, then Turbo Boost off from the menu.
        var p = new PromptPolicy();
        p.StateChanged(Normal, Warm, 0, false);
        SnoozeAt(p, Warm, 0, false);
        p.TurboOff(Min);
        Eq(p.Next(Warm, CoolAvg, Snooze, true), None, "no reminder at 15 min");
        Eq(p.Next(Warm, CoolAvg, 600 * Min, true), None, "no reminder after 10 h");
        p.StateChanged(Warm, Hot, 601 * Min, true);
        Eq(p.Next(Hot, CoolAvg, 601 * Min, true), Heat, "new entry into Hot still asks");
        SnoozeAt(p, Hot, 601 * Min, true);
        Eq(p.Next(Hot, CoolAvg, 700 * Min, true), None, "snoozed while off: no reminders");

        // A prompt already due (just entered Warm, not shown yet) still shows after a menu cap.
        p = new PromptPolicy();
        p.StateChanged(Normal, Warm, 0, false);
        p.TurboOff(0);
        Eq(p.Next(Warm, CoolAvg, 0, true), Heat, "already-due prompt still shows");

        // From the prompt itself.
        p = new PromptPolicy();
        p.StateChanged(Normal, Warm, 0, false);
        int ep = p.HeatShown();
        p.TurboOff(Min);
        p.HeatClosed(ep, false, Warm, Min, true);
        Eq(p.Next(Warm, CoolAvg, 600 * Min, true), None, "capped from the prompt: no Warm reminders");
        p.StateChanged(Warm, Hot, 601 * Min, true);
        Eq(p.Next(Hot, CoolAvg, 601 * Min, true), Heat, "capped from the prompt: Hot still asks");
    }

    private static void PolicyCoolDownRestore()
    {
        var p = new PromptPolicy();
        p.StateChanged(Normal, Warm, 0, false);
        p.TurboOff(0);
        p.StateChanged(Warm, Normal, 10 * Min, true);
        Eq(p.Next(Normal, CoolAvg, 10 * Min, true), Restore, "cool-down while capped offers restore");
        Eq(p.Next(Normal, CoolAvg, 10 * Min + 5000, true), None, "only once");
        Eq(p.Next(Normal, CoolAvg, 100 * Min, true), None, "not later either");

        p = new PromptPolicy();
        p.StateChanged(Normal, Warm, 0, false);
        p.StateChanged(Warm, Normal, 10 * Min, false);
        Eq(p.Next(Normal, CoolAvg, 10 * Min, false), None, "cool-down while not capped: no offer");

        p = new PromptPolicy();
        p.StateChanged(Normal, Warm, 0, false);
        p.StateChanged(Warm, Normal, 10 * Min, true);
        Eq(p.Next(Normal, CoolAvg, 10 * Min, false), None, "restored from the menu before it showed: no offer");
        Eq(p.Next(Normal, CoolAvg, 10 * Min, true), None, "...and the offer is gone");
    }

    private static void PolicyResetRestore()
    {
        foreach (string cause in new[] { "sleep/resume", "unplugged", "no reading" })
        {
            var p = new PromptPolicy();
            p.StateChanged(Normal, Warm, 0, false);
            p.TurboOff(0);
            for (int i = 0; i < 3; i++) p.ReadingsStopped(true); // called on every tick while it lasts
            for (int i = 1; i <= 5; i++)
                Eq(p.Next(Normal, null, Min + i * 5000, true), None, $"{cause}: window refilling, sample {i}");
            Eq(p.Next(Normal, CoolAvg, Min + 30_000, true), Restore, $"{cause}: full window in Normal offers restore");
            Eq(p.Next(Normal, CoolAvg, Min + 35_000, true), None, $"{cause}: only once");
        }

        // It heats up instead: normal heat flow, and a later real cool-down offers the restore.
        var q = new PromptPolicy();
        q.ReadingsStopped(true);
        q.StateChanged(Normal, Warm, Min, true);
        Eq(q.Next(Warm, CoolAvg, Min, true), Heat, "heated after reset: heat prompt");
        SnoozeAt(q, Warm, Min, true);
        q.StateChanged(Warm, Normal, 20 * Min, true);
        Eq(q.Next(Normal, CoolAvg, 20 * Min, true), Restore, "later cool-down offers restore");
        Eq(q.Next(Normal, CoolAvg, 21 * Min, true), None, "once");

        // Straight to Hot after the reset: the heat prompt, not the restore offer.
        q = new PromptPolicy();
        q.ReadingsStopped(true);
        q.StateChanged(Normal, Hot, Min, true);
        Eq(q.Next(Hot, CoolAvg, Min, true), Heat, "Hot after reset asks heat, not restore");

        // After a reset the monitor says Normal for anything under 85: only a cool average (<= 75) offers restore.
        q = new PromptPolicy();
        q.ReadingsStopped(true);
        Eq(q.Next(Normal, 84, Min, true), None, "full window at 84 C: no offer");
        Eq(q.Next(Normal, 75.01, Min + 5000, true), None, "75.01 C: no offer");
        Eq(q.Next(Normal, 75, Min + 10_000, true), Restore, "75 C: offer");

        // Turned off again from the tray menu while an offer was pending: don't ask to undo it.
        q = new PromptPolicy();
        q.ReadingsStopped(true);
        q.TurboOff(Min);
        Eq(q.Next(Normal, CoolAvg, Min + 30_000, true), None, "Turbo Boost just turned off: no restore offer");
    }

    private static void PolicyNoRestoreUncapped()
    {
        var p = new PromptPolicy();
        p.ReadingsStopped(false);
        Eq(p.Next(Normal, CoolAvg, Min, false), None, "reset while not capped: no offer");
        p = new PromptPolicy();
        p.ReadingsStopped(true);
        Eq(p.Next(Normal, CoolAvg, Min, false), None, "restored before the window filled: no offer");
        Eq(p.Next(Normal, CoolAvg, 2 * Min, true), None, "...and it does not come back");
    }

    private static void PolicyStalePrompt()
    {
        // Cooled down while the Warm prompt was open.
        var p = new PromptPolicy();
        p.StateChanged(Normal, Warm, 0, false);
        int ep = p.HeatShown();
        Check(!p.IsStale(ep), "fresh prompt is not stale");
        p.StateChanged(Warm, Hot, Min, false);
        Check(!p.IsStale(ep), "Warm -> Hot does not make it stale");
        p.StateChanged(Hot, Warm, 2 * Min, false);
        p.StateChanged(Warm, Normal, 5 * Min, false);
        Check(p.IsStale(ep), "cooled down: stale, so a cap click is ignored");
        p.HeatClosed(ep, false, Normal, 6 * Min, false);
        p.StateChanged(Normal, Warm, 7 * Min, false);
        Eq(p.Next(Warm, CoolAvg, 7 * Min, false), Heat, "no snooze was written: a new Warm entry asks at once");

        // Readings stopped while it was open, then came back warm.
        p = new PromptPolicy();
        p.StateChanged(Normal, Warm, 0, false);
        ep = p.HeatShown();
        p.ReadingsStopped(false);
        Check(p.IsStale(ep), "readings stopped: stale");
        p.HeatClosed(ep, false, Normal, Min, false);
        Eq(p.Next(Normal, null, Min, false), None, "nothing due while refilling");
        p.StateChanged(Normal, Warm, 2 * Min, false);
        Eq(p.Next(Warm, CoolAvg, 2 * Min, false), Heat, "back warm: asks at once, not 15 min after the stale answer");

        // A stale close also must not cancel a restore offer that the cool-down created.
        p = new PromptPolicy();
        p.StateChanged(Normal, Warm, 0, true);
        ep = p.HeatShown();
        p.StateChanged(Warm, Normal, 5 * Min, true);
        p.HeatClosed(ep, false, Normal, 6 * Min, true);
        Eq(p.Next(Normal, CoolAvg, 6 * Min, true), Restore, "restore offer survives the stale close");
    }

    private static void PolicyWarmToHotOpen()
    {
        var p = new PromptPolicy();
        p.StateChanged(Normal, Warm, 0, false);
        int ep = p.HeatShown();
        p.StateChanged(Warm, Hot, Min, false);
        p.HeatClosed(ep, false, Hot, 2 * Min, false);
        Eq(p.Next(Hot, CoolAvg, 2 * Min, false), Heat, "snoozed Warm prompt: Hot prompt still owed");

        // Already capped before the Warm prompt (it had no cap button): Hot is still owed.
        p = new PromptPolicy();
        p.StateChanged(Normal, Warm, 0, true);
        ep = p.HeatShown();
        p.StateChanged(Warm, Hot, Min, true);
        p.HeatClosed(ep, false, Hot, 2 * Min, true);
        Eq(p.Next(Hot, CoolAvg, 2 * Min, true), Heat, "already capped: Hot prompt still owed");

        // Turned off from that Warm prompt: the Hot prompt is dropped.
        p = new PromptPolicy();
        p.StateChanged(Normal, Warm, 0, false);
        ep = p.HeatShown();
        p.StateChanged(Warm, Hot, Min, false);
        p.TurboOff(2 * Min);
        p.HeatClosed(ep, false, Hot, 2 * Min, true);
        Eq(p.Next(Hot, CoolAvg, 100 * Min, true), None, "capped from the Warm prompt: no Hot prompt");
    }
}
