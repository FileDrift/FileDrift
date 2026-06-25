namespace FileDrift.App.Settings;

/// <summary>In-memory mirror of settings that a run reads live on each tick, so a change in the
/// Settings page takes effect mid-run without a disk read. Seeded from <see cref="AppSettings"/> at
/// startup and updated by the Settings slider.</summary>
public static class RuntimeOptions
{
    private const double FloorSeconds = 0.5;

    /// <summary>Activity-log on-screen refresh interval. Read on every progress tick (verify and reconcile).</summary>
    public static TimeSpan LogThrottle { get; private set; } = TimeSpan.FromSeconds(3.0);

    /// <summary>Set the throttle from a seconds value, clamping up to the 0.5s floor.</summary>
    public static void SetLogThrottle(double seconds) =>
        LogThrottle = TimeSpan.FromSeconds(Math.Max(FloorSeconds, seconds));
}
