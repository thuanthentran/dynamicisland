using static DynamicIsland.Platform.NativeMethods;

namespace DynamicIsland.Platform;

/// <summary>
/// Toggle state of Caps Lock and Num Lock: the low-order bit of <c>GetKeyState</c>. Polled from the island's tick; the
/// island never has keyboard focus, but the toggle bit still follows presses made in other apps.
/// </summary>
internal static class LockKeys
{
    public static bool CapsLock => (GetKeyState(VK_CAPITAL) & 1) != 0;

    public static bool NumLock => (GetKeyState(VK_NUMLOCK) & 1) != 0;
}
