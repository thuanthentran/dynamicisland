using System.Diagnostics;
using Microsoft.Win32;
using Windows.UI.ViewManagement;
using MediaColor = System.Windows.Media.Color;

namespace DynamicIsland.Platform;

/// <summary>Reads the user's app theme and accent colour. The host re-queries on WM_SETTINGCHANGE "ImmersiveColorSet".</summary>
internal static class SystemTheme
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public static bool AppsUseLightTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
        // Windows treats a missing value as light.
        return key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
    }

    public static MediaColor? Accent(bool forDarkSurface)
    {
        try
        {
            var color = new UISettings().GetColorValue(forDarkSurface ? UIColorType.AccentLight2 : UIColorType.AccentDark1);
            return MediaColor.FromArgb(color.A, color.R, color.G, color.B);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Theme] accent unavailable: {ex.Message}");
            return null;
        }
    }
}
