using System.Windows.Media;

namespace DynamicIsland.ViewModels;

/// <summary>
/// Frozen brushes for one theme, taken from the Windows 11 (Fluent) colour tokens so the island reads like a system
/// flyout. The host copies them into window resources, so styles follow through DynamicResource.
/// </summary>
public sealed record IslandPalette(
    Brush Surface,
    Brush Tint,
    Brush Stroke,
    Color Shadow,
    Brush Foreground,
    Brush Secondary,
    Brush Tertiary,
    Brush Card,
    Brush CardStroke,
    Brush Hover,
    Brush Pressed,
    Brush ControlFill,
    Brush ControlStrong,
    Brush Track,
    Brush Thumb,
    Brush Accent,
    Brush OnAccent)
{
    /// <summary>Dark acrylic flyout; Tint lets ~20% of the blurred artwork show through.</summary>
    public static IslandPalette Dark(Color accent) => new(
        Surface: Solid(0xFF, 0x24, 0x24, 0x24),
        Tint: Solid(0xCC, 0x24, 0x24, 0x24),
        Stroke: Solid(0x24, 0xFF, 0xFF, 0xFF),
        Shadow: Color.FromArgb(0x30, 0, 0, 0),
        Foreground: Solid(0xFF, 0xFF, 0xFF, 0xFF),
        Secondary: Solid(0xC5, 0xFF, 0xFF, 0xFF),
        Tertiary: Solid(0x87, 0xFF, 0xFF, 0xFF),
        Card: Solid(0x0D, 0xFF, 0xFF, 0xFF),
        CardStroke: Solid(0x12, 0xFF, 0xFF, 0xFF),
        Hover: Solid(0x0F, 0xFF, 0xFF, 0xFF),
        Pressed: Solid(0x0A, 0xFF, 0xFF, 0xFF),
        ControlFill: Solid(0x15, 0xFF, 0xFF, 0xFF),
        ControlStrong: Solid(0x8B, 0xFF, 0xFF, 0xFF),
        Track: Solid(0x60, 0xFF, 0xFF, 0xFF),
        Thumb: Solid(0xFF, 0x45, 0x45, 0x45),
        Accent: Solid(accent),
        OnAccent: Solid(0xFF, 0x00, 0x00, 0x00));

    public static IslandPalette Light(Color accent) => new(
        Surface: Solid(0xFF, 0xF3, 0xF3, 0xF3),
        Tint: Solid(0xD9, 0xF3, 0xF3, 0xF3),
        Stroke: Solid(0x1A, 0x00, 0x00, 0x00),
        Shadow: Color.FromArgb(0x22, 0, 0, 0),
        Foreground: Solid(0xE4, 0x00, 0x00, 0x00),
        Secondary: Solid(0x9E, 0x00, 0x00, 0x00),
        Tertiary: Solid(0x72, 0x00, 0x00, 0x00),
        Card: Solid(0xB3, 0xFF, 0xFF, 0xFF),
        CardStroke: Solid(0x0F, 0x00, 0x00, 0x00),
        Hover: Solid(0x09, 0x00, 0x00, 0x00),
        Pressed: Solid(0x06, 0x00, 0x00, 0x00),
        ControlFill: Solid(0xB3, 0xFF, 0xFF, 0xFF),
        ControlStrong: Solid(0x72, 0x00, 0x00, 0x00),
        Track: Solid(0x59, 0x00, 0x00, 0x00),
        Thumb: Solid(0xFF, 0xFF, 0xFF, 0xFF),
        Accent: Solid(accent),
        OnAccent: Solid(0xFF, 0xFF, 0xFF, 0xFF));

    private static SolidColorBrush Solid(byte a, byte r, byte g, byte b) => Solid(Color.FromArgb(a, r, g, b));

    private static SolidColorBrush Solid(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
