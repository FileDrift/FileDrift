// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows.Threading;

namespace FileDrift.App.Settings;

/// <summary>Debounces persisting the live-refresh interval. Slider drags fire ValueChanged on every
/// snap point, and each save was a settings.json read+write on the UI thread. Both slider surfaces
/// (Verify page and Settings) funnel here; the value saved is whatever <see cref="RuntimeOptions"/>
/// holds when the quiet period elapses, so the last position in a drag always wins.</summary>
internal static class ThrottleSettingSaver
{
    private static readonly DispatcherTimer Timer = CreateTimer();

    private static DispatcherTimer CreateTimer()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            var s = SettingsStore.Load();
            s.LogThrottleSeconds = RuntimeOptions.LogThrottle.TotalSeconds;
            SettingsStore.Save(s);
        };
        return t;
    }

    /// <summary>Schedules a save for shortly after the last change (restarts the quiet period).</summary>
    public static void RequestSave()
    {
        Timer.Stop();
        Timer.Start();
    }
}
