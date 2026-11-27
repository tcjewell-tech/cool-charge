namespace CoolCharge;

/// Which prompt is due: heat (warm/hot) or "Restore full speed". Pure: no clock, UI or disk inside. The caller
/// passes a monotonic "now" in milliseconds (Environment.TickCount64), so clock or time-zone changes can't
/// stretch or shorten a snooze, and the cached "Turbo Boost is off" state.
public sealed class PromptPolicy
{
    public enum Prompt { None, Heat, Restore }

    public const long SnoozeMs = 15 * 60 * 1000;
    private const long Never = long.MaxValue;

    private long _heatDue = Never;
    private bool _offerRestore;  // shown once, only while Normal with a full window
    private bool _offSinceShown; // Turbo Boost was turned off while the current heat prompt was open

    /// Changes whenever the monitor goes Normal or readings stop. A heat prompt runs its own message loop, so
    /// ticks continue while it is open; if this changed meanwhile, its answer is stale.
    public int Episode { get; private set; }

    public bool IsStale(int episode) => episode != Episode;

    /// The monitor moved from prev to state (prev != state).
    public void StateChanged(ThermalState prev, ThermalState state, long now, bool capped)
    {
        // Entering Hot always asks, even if Warm was snoozed or Turbo Boost is already off.
        if (state == ThermalState.Hot || prev == ThermalState.Normal) { _heatDue = now; _offerRestore = false; }
        if (state == ThermalState.Normal)
        {
            // Only a real cool-down (Warm held at or below 75 °C for 2 min) gets here; a reset goes through ReadingsStopped.
            _heatDue = Never;
            _offerRestore = capped;
            Episode++;
        }
    }

    /// Readings stopped (sleep/resume gap, unplugged, no sensor reading) and the samples were dropped.
    public void ReadingsStopped(bool capped)
    {
        _heatDue = Never;
        if (capped) _offerRestore = true; // no cool-down could be seen, so offer it once readings are back to Normal
        Episode++;
    }

    /// Turbo Boost was turned off (from a prompt or the tray menu): no more reminders. A prompt already due still shows.
    public void TurboOff(long now)
    {
        _offSinceShown = true;
        _offerRestore = false; // just turned off: don't ask to undo it
        if (_heatDue > now) _heatDue = Never;
    }

    /// The prompt to show now, or None. `average` is the 30 s average, null while the window refills.
    /// A restore offer is used up when returned, or dropped if no longer capped.
    public Prompt Next(ThermalState state, double? average, long now, bool capped)
    {
        if (state != ThermalState.Normal) return now >= _heatDue ? Prompt.Heat : Prompt.None;
        // After a reset the monitor reads Normal for anything under 85 °C, so wait for a genuinely cool average.
        if (!_offerRestore || average is not double a || a > Thermal.ClearAt) return Prompt.None;
        _offerRestore = false;
        return capped ? Prompt.Restore : Prompt.None;
    }

    /// Call as a heat prompt opens; pass the result to IsStale and HeatClosed.
    public int HeatShown()
    {
        _offSinceShown = false;
        return Episode;
    }

    /// The heat prompt opened at `episode` has closed; `capped` is the state after any cap from it.
    public void HeatClosed(int episode, bool wasHot, ThermalState state, long now, bool capped)
    {
        if (IsStale(episode)) return; // cooled down or readings stopped meanwhile: the schedule already moved on
        // It went from Warm to Hot while this prompt sat open and Turbo Boost wasn't turned off; that Hot prompt is still owed.
        if (!wasHot && !_offSinceShown && state == ThermalState.Hot) return;
        // Remind every 15 min while Turbo Boost is still on; once it is off, only a new entry into Hot asks.
        _heatDue = capped ? Never : now + SnoozeMs;
    }
}
