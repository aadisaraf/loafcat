using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace LoafCat.Interop;

/// The displays, and the part of each one the cat is allowed to use.
///
/// `Work` is the direct counterpart of `NSScreen.visibleFrame`: the monitor rectangle
/// minus the taskbar and any docked appbar. The macOS build learned the hard way to
/// never use the full frame — that display reports a 33pt notch, and a cat walking the
/// top edge gets bisected by it. Windows has the same class of problem at the bottom,
/// where a cat placed against `Monitor` rather than `Work` starts life underneath the
/// taskbar and looks like it failed to launch.
[SupportedOSPlatform("windows")]
public static class Screens
{
    public readonly record struct Display(Rect Frame, Rect Work, bool Primary, uint Dpi);

    public static List<Display> All()
    {
        var found = new List<Display>();
        Win32.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (h, _, _, _) =>
        {
            if (Describe(h) is { } d) found.Add(d);
            return true;
        }, IntPtr.Zero);

        if (found.Count == 0)
        {
            // EnumDisplayMonitors returns nothing in a session with no attached
            // display, which is exactly what a CI runner can look like. A single
            // notional 1080p screen keeps every geometry calculation defined.
            found.Add(new Display(
                new Rect(0, 0, 1920, 1080), new Rect(0, 0, 1920, 1080), true, 96));
        }
        return found;
    }

    private static Display? Describe(IntPtr monitor)
    {
        var info = new Win32.MonitorInfo
        {
            Size = (uint)Marshal.SizeOf<Win32.MonitorInfo>(),
        };
        if (!Win32.GetMonitorInfo(monitor, ref info)) return null;

        return new Display(
            ToRect(info.Monitor),
            ToRect(info.Work),
            (info.Flags & 1) != 0,      // MONITORINFOF_PRIMARY
            96);
    }

    private static Rect ToRect(Win32.RectL r) =>
        new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);

    public static Display Primary()
    {
        var all = All();
        foreach (var d in all)
        {
            if (d.Primary) return d;
        }
        return all[0];
    }

    /// The display a rectangle mostly sits on, by area of overlap.
    ///
    /// Deliberately not "the display with the cursor on it" and not the primary: an
    /// automatic stretch break must never yank the cat onto whichever monitor the user
    /// happens to be pointing at, because that looks like the app moved their window.
    public static Display Holding(Rect frame)
    {
        Display best = default;
        double bestArea = -1;
        foreach (var d in All())
        {
            double area = d.Frame.IntersectionArea(frame);
            if (area > bestArea) { bestArea = area; best = d; }
        }
        return bestArea < 0 ? Primary() : best;
    }

    /// The DPI of the display holding a window, for the first-run scale guess.
    public static uint DpiFor(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return 96;
        uint dpi = Win32.GetDpiForWindow(hwnd);
        return dpi == 0 ? 96 : dpi;
    }
}
