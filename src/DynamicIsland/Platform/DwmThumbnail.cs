using static DynamicIsland.Platform.NativeMethods;

namespace DynamicIsland.Platform;

/// <summary>
/// A live preview of another window, drawn by DWM onto the island window (DWM Thumbnail API). DWM composes it above the
/// island's own content as a plain rectangle, unclipped by the island's shape, so the host shows it only while the shape
/// is at rest.
/// </summary>
internal sealed class DwmThumbnail : IDisposable
{
    private IntPtr _handle;

    private DwmThumbnail(IntPtr handle) => _handle = handle;

    public static DwmThumbnail? TryCreate(IntPtr destination, IntPtr source) =>
        DwmRegisterThumbnail(destination, source, out var handle) == 0 && handle != IntPtr.Zero ? new DwmThumbnail(handle) : null;

    /// <summary>Shows the source scaled to fit inside <paramref name="slot"/> (device pixels of the island window), centred.</summary>
    public bool Show(RECT slot)
    {
        if (_handle == IntPtr.Zero || DwmQueryThumbnailSourceSize(_handle, out var source) != 0 || source.Width <= 0 || source.Height <= 0)
            return false;

        var scale = Math.Min((double)slot.Width / source.Width, (double)slot.Height / source.Height);
        var width = (int)Math.Round(source.Width * scale);
        var height = (int)Math.Round(source.Height * scale);
        var left = slot.Left + (slot.Width - width) / 2;
        var top = slot.Top + (slot.Height - height) / 2;
        var properties = new DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = DWM_TNP_RECTDESTINATION | DWM_TNP_VISIBLE | DWM_TNP_OPACITY | DWM_TNP_SOURCECLIENTAREAONLY,
            rcDestination = new RECT { Left = left, Top = top, Right = left + width, Bottom = top + height },
            opacity = 255,
            fVisible = 1,
            fSourceClientAreaOnly = 0
        };
        return DwmUpdateThumbnailProperties(_handle, properties) == 0;
    }

    public void Hide()
    {
        if (_handle == IntPtr.Zero)
            return;
        var properties = new DWM_THUMBNAIL_PROPERTIES { dwFlags = DWM_TNP_VISIBLE, fVisible = 0 };
        DwmUpdateThumbnailProperties(_handle, properties);
    }

    public void Dispose()
    {
        if (_handle == IntPtr.Zero)
            return;
        DwmUnregisterThumbnail(_handle);
        _handle = IntPtr.Zero;
    }
}
