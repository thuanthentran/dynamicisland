using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DynamicIsland.ViewModels;

/// <summary>Sharp artwork, a pre-blurred backdrop copy and the artwork's dominant colour.</summary>
public sealed record ProcessedArtwork(ImageSource Sharp, ImageSource Blurred, Color Dominant);

/// <summary>
/// Decodes artwork once per track. The backdrop is blurred in software on a tiny copy and then stretched, so no
/// BlurEffect runs per frame on the layered window.
/// </summary>
internal static class ArtworkProcessor
{
    private const int SharpWidth = 192;
    private const int BackdropWidth = 36;
    private const int BlurRadius = 3;
    private const int BlurPasses = 3;

    public static ProcessedArtwork? Process(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0)
            return null;

        try
        {
            var sharp = Decode(bytes, SharpWidth);
            var small = new FormatConvertedBitmap(Decode(bytes, BackdropWidth), PixelFormats.Bgra32, null, 0);
            int width = small.PixelWidth, height = small.PixelHeight, stride = width * 4;
            var pixels = new byte[height * stride];
            small.CopyPixels(pixels, stride, 0);
            var dominant = Dominant(pixels);
            for (var pass = 0; pass < BlurPasses; pass++)
                BoxBlur(pixels, width, height);

            var blurred = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            blurred.Freeze();
            return new ProcessedArtwork(sharp, blurred, dominant);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Artwork] decode failed: {ex.Message}");
            return null;
        }
    }

    public static ImageSource? DecodeIcon(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0)
            return null;
        try
        {
            return Decode(bytes, 64);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Artwork] icon decode failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Keeps the hue of <paramref name="color"/> but sets saturation and lightness so it stands out on the island surface:
    /// lighter on dark surfaces, deeper on light ones. Greyscale artwork stays grey.
    /// </summary>
    public static Color ReadableOn(Color color, bool lightSurface)
    {
        var (hue, saturation, _) = ToHsl(color.R / 255.0, color.G / 255.0, color.B / 255.0);
        var tuned = saturation < 0.08 ? saturation : Math.Clamp(saturation, 0.5, 0.9);
        var (r, g, b) = FromHsl(hue, tuned, lightSurface ? 0.42 : 0.64);
        return Color.FromRgb((byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255));
    }

    /// <summary>Average weighted toward saturated, bright pixels, so a vivid subject wins over a dull background.</summary>
    private static Color Dominant(byte[] bgra)
    {
        double red = 0, green = 0, blue = 0, total = 0;
        for (var i = 0; i < bgra.Length; i += 4)
        {
            double b = bgra[i] / 255.0, g = bgra[i + 1] / 255.0, r = bgra[i + 2] / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            var saturation = max <= 0 ? 0 : (max - min) / max;
            var weight = saturation * saturation * max + 0.01;
            red += r * weight;
            green += g * weight;
            blue += b * weight;
            total += weight;
        }
        return Color.FromRgb((byte)(red / total * 255), (byte)(green / total * 255), (byte)(blue / total * 255));
    }

    private static (double H, double S, double L) ToHsl(double r, double g, double b)
    {
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2;
        if (max - min < 1e-6)
            return (0, 0, l);
        var d = max - min;
        var s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        double h = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        return (h / 6, s, l);
    }

    private static (double R, double G, double B) FromHsl(double h, double s, double l)
    {
        if (s < 1e-6)
            return (l, l, l);
        var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        var p = 2 * l - q;
        return (Hue(p, q, h + 1.0 / 3), Hue(p, q, h), Hue(p, q, h - 1.0 / 3));

        static double Hue(double p, double q, double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            if (t < 1.0 / 6) return p + (q - p) * 6 * t;
            if (t < 0.5) return q;
            if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
            return p;
        }
    }

    private static BitmapImage Decode(byte[] bytes, int width)
    {
        using var stream = new MemoryStream(bytes);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = width;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static void BoxBlur(byte[] pixels, int width, int height)
    {
        var copy = (byte[])pixels.Clone();
        BlurAxis(copy, pixels, width, height, horizontal: true);
        Array.Copy(pixels, copy, pixels.Length);
        BlurAxis(copy, pixels, width, height, horizontal: false);
    }

    private static void BlurAxis(byte[] source, byte[] target, int width, int height, bool horizontal)
    {
        var span = 2 * BlurRadius + 1;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            int b = 0, g = 0, r = 0, a = 0;
            for (var k = -BlurRadius; k <= BlurRadius; k++)
            {
                var sx = horizontal ? Math.Clamp(x + k, 0, width - 1) : x;
                var sy = horizontal ? y : Math.Clamp(y + k, 0, height - 1);
                var index = (sy * width + sx) * 4;
                b += source[index];
                g += source[index + 1];
                r += source[index + 2];
                a += source[index + 3];
            }
            var target4 = (y * width + x) * 4;
            target[target4] = (byte)(b / span);
            target[target4 + 1] = (byte)(g / span);
            target[target4 + 2] = (byte)(r / span);
            target[target4 + 3] = (byte)(a / span);
        }
    }
}
