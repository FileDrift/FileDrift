using System.Windows;

namespace FileDrift.App.Settings;

/// <summary>Persists and restores the main window's size, position, and maximized state. A restored
/// position is clamped to the visible virtual screen, so a window saved on a now-disconnected monitor
/// still appears on screen instead of off in the void.</summary>
public static class WindowPlacement
{
    private const double MinVisiblePatch = 80; // px of the window that must overlap a monitor to be reachable

    public static void Restore(Window window, AppSettings s)
    {
        if (s.WindowWidth >= window.MinWidth) window.Width = s.WindowWidth;
        if (s.WindowHeight >= window.MinHeight) window.Height = s.WindowHeight;

        if (s.WindowLeft is { } left && s.WindowTop is { } top && IsReachable(left, top, window.Width, window.Height))
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = left;
            window.Top = top;
        }

        if (s.WindowMaximized) window.WindowState = WindowState.Maximized;
    }

    public static void Save(Window window, AppSettings s)
    {
        bool maximized = window.WindowState == WindowState.Maximized;
        // RestoreBounds carries the normal (un-maximized) rect; use current bounds when already normal.
        var b = window.WindowState == WindowState.Normal
            ? new Rect(window.Left, window.Top, window.Width, window.Height)
            : window.RestoreBounds;
        if (b.IsEmpty) return; // nothing meaningful to record yet

        s.WindowLeft = b.Left;
        s.WindowTop = b.Top;
        s.WindowWidth = b.Width;
        s.WindowHeight = b.Height;
        s.WindowMaximized = maximized;
    }

    /// <summary>True if a grabbable slice of the rect overlaps the virtual screen (spanning all monitors).</summary>
    private static bool IsReachable(double left, double top, double width, double height)
    {
        var virtualScreen = new Rect(
            SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var win = new Rect(left, top, width, height);
        win.Intersect(virtualScreen);
        return win.Width >= MinVisiblePatch && win.Height >= MinVisiblePatch;
    }
}
