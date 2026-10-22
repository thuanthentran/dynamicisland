using System.Runtime.InteropServices;
using static DynamicIsland.Platform.NativeMethods;

namespace DynamicIsland.Platform;

internal readonly record struct WindowEntry(IntPtr Handle, string Title);

/// <summary>
/// The app windows a task switcher would list, through documented calls only: EnumWindows (top of the z-order first),
/// visibility, owner and extended styles, and DWM's cloaked flag, which hides windows on other virtual desktops and
/// suspended or background shell surfaces. The filter is our own approximation of Alt+Tab, not Windows' exact rule.
/// </summary>
internal static unsafe class WindowList
{
    private static readonly HashSet<string> ShellClasses = ["Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd"];

    [ThreadStatic] private static List<IntPtr>? _collected;

    public static List<WindowEntry> Enumerate(IntPtr exclude)
    {
        var handles = _collected = [];
        try
        {
            EnumWindows(&Collect, IntPtr.Zero);
        }
        finally
        {
            _collected = null;
        }

        var windows = new List<WindowEntry>();
        foreach (var hwnd in handles)
        {
            if (hwnd != exclude && IsSwitchable(hwnd) && TitleOf(hwnd) is { Length: > 0 } title)
                windows.Add(new WindowEntry(hwnd, title));
        }
        return windows;
    }

    public static string TitleOf(IntPtr hwnd)
    {
        var length = GetWindowTextLength(hwnd);
        if (length <= 0)
            return string.Empty;
        Span<char> buffer = length < 256 ? stackalloc char[length + 1] : new char[length + 1];
        fixed (char* text = buffer)
        {
            var copied = GetWindowText(hwnd, text, buffer.Length);
            return new string(text, 0, Math.Max(0, copied));
        }
    }

    /// <summary>The window's own icon (WM_GETICON, then its class icon). Owned by the window: never destroy it.</summary>
    public static IntPtr IconOf(IntPtr hwnd)
    {
        foreach (var kind in (ReadOnlySpan<int>)[ICON_BIG, ICON_SMALL2, ICON_SMALL])
        {
            // A hung window must not stall the island.
            if (SendMessageTimeout(hwnd, WM_GETICON, kind, IntPtr.Zero, SMTO_ABORTIFHUNG, 50, out var icon) != IntPtr.Zero && icon != IntPtr.Zero)
                return icon;
        }
        return GetClassLongPtr(hwnd, GCLP_HICON);
    }

    /// <summary>Brings the window forward, restoring it if minimized. Allowed because the user just clicked the island.</summary>
    public static void Activate(IntPtr hwnd)
    {
        if (IsIconic(hwnd))
            ShowWindow(hwnd, SW_RESTORE);
        SetForegroundWindow(hwnd);
    }

    /// <summary>Asks the window to close, as its close button would; the app may still prompt or refuse.</summary>
    public static void Close(IntPtr hwnd) => PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

    private static bool IsSwitchable(IntPtr hwnd)
    {
        if (!IsWindowVisible(hwnd))
            return false;
        var exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        var appWindow = (exStyle & WS_EX_APPWINDOW) != 0;
        if (!appWindow && ((exStyle & WS_EX_TOOLWINDOW) != 0 || GetWindow(hwnd, GW_OWNER) != IntPtr.Zero))
            return false;
        if ((exStyle & WS_EX_NOACTIVATE) != 0)
            return false;
        if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0)
            return false;
        return !ShellClasses.Contains(ScreenProbe.GetClassNameOf(hwnd));
    }

    [UnmanagedCallersOnly]
    private static int Collect(IntPtr hwnd, IntPtr _)
    {
        _collected?.Add(hwnd);
        return 1;
    }
}
