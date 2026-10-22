using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DynamicIsland.Platform;

namespace DynamicIsland.ViewModels;

/// <summary>
/// The Windows tab: open app windows on the current virtual desktop, like Task View. Opening the tab takes a fresh
/// z-order snapshot; while it stays open, closed windows drop out and new ones are appended without reshuffling the rest,
/// so a card never jumps away from the pointer.
/// </summary>
public sealed partial class WindowsViewModel : ObservableObject
{
    private const int Limit = 12;

    private readonly Action _collapse;
    private readonly Dictionary<IntPtr, WindowItemViewModel> _known = [];

    [ObservableProperty] private bool _hasWindows;
    [ObservableProperty] private bool _hasMore;
    [ObservableProperty] private string _moreText = string.Empty;

    public WindowsViewModel(Action collapse) => _collapse = collapse;

    public ObservableCollection<WindowItemViewModel> Items { get; } = [];

    /// <summary>The island's own window, never listed.</summary>
    public IntPtr ExcludedWindow { get; set; }

    /// <summary>Raised after cards were added, removed or reordered, so the host re-places live thumbnails.</summary>
    public event Action? LayoutChanged;

    /// <param name="snapshot">True when the tab opens: adopt the current z-order (most recently used first).</param>
    public void Refresh(bool snapshot)
    {
        List<WindowEntry> found;
        try
        {
            found = WindowList.Enumerate(ExcludedWindow);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Windows] enumeration failed: {ex.Message}");
            return;
        }

        var visible = found.Take(Limit).ToList();
        var order = snapshot ? visible : StableOrder(visible);
        var changed = order.Count != Items.Count;
        for (var index = 0; index < order.Count; index++)
        {
            var entry = order[index];
            if (!_known.TryGetValue(entry.Handle, out var item))
                _known[entry.Handle] = item = new WindowItemViewModel(entry.Handle, Activate, Close);
            item.Title = entry.Title;

            if (index < Items.Count && Items[index] == item)
                continue;
            changed = true;
            var existing = Items.IndexOf(item);
            if (existing >= 0)
                Items.Move(existing, index);
            else
                Items.Insert(index, item);
        }
        while (Items.Count > order.Count)
            Items.RemoveAt(Items.Count - 1);

        var live = found.Select(entry => entry.Handle).ToHashSet();
        foreach (var gone in _known.Keys.Where(handle => !live.Contains(handle)).ToList())
            _known.Remove(gone);

        HasWindows = Items.Count > 0;
        var more = found.Count - visible.Count;
        HasMore = more > 0;
        MoreText = more > 0 ? $"+{more} more" : string.Empty;
        if (changed)
            LayoutChanged?.Invoke();
    }

    /// <summary>Cards already shown keep their places; windows that appeared since go at the end.</summary>
    private List<WindowEntry> StableOrder(List<WindowEntry> visible)
    {
        var byHandle = visible.ToDictionary(entry => entry.Handle);
        var order = Items.Where(item => byHandle.ContainsKey(item.Handle)).Select(item => byHandle[item.Handle]).ToList();
        var placed = order.Select(entry => entry.Handle).ToHashSet();
        order.AddRange(visible.Where(entry => !placed.Contains(entry.Handle)));
        return order;
    }

    private void Activate(WindowItemViewModel item)
    {
        WindowList.Activate(item.Handle);
        _collapse();
    }

    private void Close(WindowItemViewModel item)
    {
        WindowList.Close(item.Handle);
        // The app may take a moment (or ask to save); the next refresh drops it once it is gone.
    }
}

public sealed partial class WindowItemViewModel : ObservableObject
{
    private readonly Action<WindowItemViewModel> _activate;
    private readonly Action<WindowItemViewModel> _close;

    [ObservableProperty] private string _title = string.Empty;

    public WindowItemViewModel(IntPtr handle, Action<WindowItemViewModel> activate, Action<WindowItemViewModel> close)
    {
        Handle = handle;
        _activate = activate;
        _close = close;
        Icon = LoadIcon(handle);
    }

    public IntPtr Handle { get; }
    public ImageSource? Icon { get; }
    public bool HasIcon => Icon is not null;

    [RelayCommand]
    private void Activate() => _activate(this);

    [RelayCommand]
    private void Close() => _close(this);

    private static ImageSource? LoadIcon(IntPtr hwnd)
    {
        try
        {
            var icon = WindowList.IconOf(hwnd);
            if (icon == IntPtr.Zero)
                return null;
            // Copies the pixels; the HICON stays the window's.
            var image = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return image;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Windows] icon unavailable: {ex.Message}");
            return null;
        }
    }
}
