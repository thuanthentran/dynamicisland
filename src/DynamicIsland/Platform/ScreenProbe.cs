using System.Runtime.InteropServices;
using static DynamicIsland.Platform.NativeMethods;

namespace DynamicIsland.Platform;

/// <summary>A monitor in physical pixels together with its work area and effective DPI scale.</summary>
internal readonly record struct ScreenInfo(IntPtr Handle, RECT Bounds, RECT Work, double Scale);

/// <summary>
/// Cheap, synchronous desktop queries used by the island host on every tick. Only documented Win32 calls; no Explorer internals.
/// </summary>
internal static class ScreenProbe
{
    private const double DefaultTaskbarHeightDip = 48;

    public static ScreenInfo Primary() => FromMonitor(MonitorFromPoint(default, MONITOR_DEFAULTTOPRIMARY));

    /// <summary>
    /// Height of a bottom taskbar in DIP, read from the gap between monitor and work area. Auto-hide, top or side taskbars
    /// leave no usable gap and fall back to the Windows 11 default.
    /// </summary>
    public static double TaskbarHeightDip(in ScreenInfo screen)
    {
        var height = (screen.Bounds.Bottom - screen.Work.Bottom) / screen.Scale;
        return height is >= 24 and <= 96 ? height : DefaultTaskbarHeightDip;
    }

    /// <summary>A held button means a drag (window move, Snap, selection) rather than an intent to open the island.</summary>
    public static bool IsMouseButtonDown() =>
        (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0 || (GetAsyncKeyState(VK_RBUTTON) & 0x8000) != 0;

    /// <summary>
    /// Heuristic: the foreground window covers the whole monitor and is not merely maximized. Borderless-windowed games
    /// that leave a gap are not detected.
    /// </summary>
    public static bool IsFullscreenAppOn(in ScreenInfo screen)
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero || IsZoomed(foreground))
            return false;
        if (GetClassNameOf(foreground) is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
            return false;
        if (MonitorFromWindow(foreground, MONITOR_DEFAULTTONEAREST) != screen.Handle || !GetWindowRect(foreground, out var rect))
            return false;

        var bounds = screen.Bounds;
        return rect.Left <= bounds.Left && rect.Top <= bounds.Top && rect.Right >= bounds.Right && rect.Bottom >= bounds.Bottom;
    }

    /// <summary>Whether an unrequested pop-up (a notification) would interrupt a game, presentation or fullscreen app.</summary>
    public static bool IsNotificationQuiet(in ScreenInfo screen)
    {
        if (IsFullscreenAppOn(screen))
            return true;
        if (SHQueryUserNotificationState(out var state) != 0)
            return false;
        return state is QUNS_BUSY or QUNS_RUNNING_D3D_FULL_SCREEN or QUNS_PRESENTATION_MODE;
    }

    private static ScreenInfo FromMonitor(IntPtr monitor)
    {
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(monitor, ref info);
        var scale = GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0 ? dpiX / 96.0 : 1.0;
        return new ScreenInfo(monitor, info.rcMonitor, info.rcWork, scale);
    }

    internal static unsafe string GetClassNameOf(IntPtr hwnd)
    {
        const int capacity = 64;
        char* buffer = stackalloc char[capacity];
        var length = GetClassName(hwnd, buffer, capacity);
        return length > 0 ? new string(buffer, 0, length) : string.Empty;
    }
}
