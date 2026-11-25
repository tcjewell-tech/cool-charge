namespace FlightThermalGuard;

public enum ThermalState { Normal, Warm, Hot }

public static class Thermal
{
    public const double WarmAt = 85, HotAt = 92, ClearAt = 75; // °C, i7-1185G7 (TjMax 100)
    public const double ClearHoldSeconds = 120;
    public const int WindowSamples = 6; // 6 x 5 s poll = 30 s average

    /// Pure transition rule. secondsBelowClear = how long the average has stayed at or below ClearAt.
    public static ThermalState Next(ThermalState state, double avg, double secondsBelowClear)
    {
        if (avg >= HotAt) return ThermalState.Hot;
        return state switch
        {
            ThermalState.Hot => avg <= WarmAt ? ThermalState.Warm : ThermalState.Hot,
            ThermalState.Warm => avg <= ClearAt && secondsBelowClear >= ClearHoldSeconds
                ? ThermalState.Normal : ThermalState.Warm,
            _ => avg >= WarmAt ? ThermalState.Warm : ThermalState.Normal,
        };
    }
}

/// Holds the rolling window and the below-clear timer. No clock inside: the caller passes elapsed seconds,
/// so tests can drive it deterministically.
public sealed class ThermalMonitor
{
    private readonly Queue<double> _window = new();
    private double _secondsBelowClear;

    public ThermalState State { get; private set; } = ThermalState.Normal;

    /// Null until the window is full, so one turbo spike right after start can't trigger a prompt.
    public double? Average => _window.Count == Thermal.WindowSamples ? _window.Average() : null;

    public ThermalState Update(double tempC, double elapsedSeconds)
    {
        _window.Enqueue(tempC);
        if (_window.Count > Thermal.WindowSamples) _window.Dequeue();
        if (Average is not double avg) return State;

        _secondsBelowClear = avg <= Thermal.ClearAt ? _secondsBelowClear + elapsedSeconds : 0;
        return State = Thermal.Next(State, avg, _secondsBelowClear);
    }

    /// Call when readings stop (unplugged, sensor failure) so stale samples never count later.
    public void Reset()
    {
        _window.Clear();
        _secondsBelowClear = 0;
        State = ThermalState.Normal;
    }
}
