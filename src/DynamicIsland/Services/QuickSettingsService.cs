using System.Diagnostics;
using System.Management;
using Windows.Devices.Radios;
using Windows.Foundation;
using Windows.System;
using Windows.System.Power;

namespace DynamicIsland.Services;

/// <param name="Present">The device has this radio.</param>
/// <param name="Controllable">Access was granted and no hardware switch or firmware has disabled it.</param>
public readonly record struct RadioInfo(bool Present, bool On, bool Controllable);

/// <summary>
/// The parts of Windows Quick Settings that have documented APIs: Wi-Fi and Bluetooth radios (Windows.Devices.Radios),
/// built-in display brightness (WMI WmiMonitorBrightness), and Energy saver state (PowerManager, read-only). Everything
/// else is reached through documented ms-settings: pages. Started lazily, the first time the tab is opened.
/// </summary>
public sealed class QuickSettingsService : IDisposable
{
    private const string WmiNamespace = @"root\WMI";

    private readonly object _gate = new();
    private readonly TypedEventHandler<Radio, object> _onRadioChanged;
    private readonly EventHandler<object> _onEnergySaverChanged;
    private readonly LatestValueWriter<int> _brightnessWriter;
    private Task? _start;
    private Radio? _wifi;
    private Radio? _bluetooth;
    private bool _radioAccess;
    private int? _brightness;
    private volatile bool _disposed;

    public QuickSettingsService()
    {
        _onRadioChanged = (_, _) => RaiseChanged();
        _onEnergySaverChanged = (_, _) => RaiseChanged();
        _brightnessWriter = new LatestValueWriter<int>(WriteBrightness, "QuickSettings");
    }

    /// <summary>Raised on a background thread whenever any state below may have changed.</summary>
    public event Action? Changed;

    public RadioInfo Wifi => Describe(_wifi);
    public RadioInfo Bluetooth => Describe(_bluetooth);

    /// <summary>Built-in display brightness in percent; null where the display driver does not expose it (most desktops).</summary>
    public int? Brightness
    {
        get
        {
            lock (_gate)
                return _brightness;
        }
    }

    public bool HasEnergySaver => PowerManager.BatteryStatus != BatteryStatus.NotPresent;
    public bool EnergySaverOn => PowerManager.EnergySaverStatus == EnergySaverStatus.On;

    /// <summary>Idempotent. WinRT and WMI calls run on the pool, never on the UI thread.</summary>
    public Task StartAsync() => _start ??= Task.Run(async () =>
    {
        try
        {
            // Reading radios is always allowed; changing them needs consent (and the "radios" capability when packaged).
            _radioAccess = await Radio.RequestAccessAsync() == RadioAccessStatus.Allowed;
            foreach (var radio in await Radio.GetRadiosAsync())
            {
                if (radio.Kind == RadioKind.WiFi && _wifi is null)
                    _wifi = radio;
                else if (radio.Kind == RadioKind.Bluetooth && _bluetooth is null)
                    _bluetooth = radio;
                else
                    continue;
                radio.StateChanged += _onRadioChanged;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[QuickSettings] radios unavailable: {ex.Message}");
        }

        PowerManager.EnergySaverStatusChanged += _onEnergySaverChanged;
        RefreshBrightness();
    });

    /// <summary>Brightness has no cheap change event; re-read whenever the tab is shown.</summary>
    public Task RefreshAsync() => Task.Run(RefreshBrightness);

    public async Task SetRadioAsync(RadioKind kind, bool on)
    {
        var radio = kind == RadioKind.WiFi ? _wifi : _bluetooth;
        if (radio is null || !_radioAccess)
            return;
        try
        {
            // Hardware switches or policy can refuse; StateChanged reports what actually happened.
            await radio.SetStateAsync(on ? RadioState.On : RadioState.Off);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[QuickSettings] {kind} toggle failed: {ex.Message}");
        }
        RaiseChanged();
    }

    public void SetBrightness(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        lock (_gate)
            _brightness = percent;
        _brightnessWriter.Post(percent);
    }

    /// <summary>Opens a page of the Settings app by its documented ms-settings: URI.</summary>
    public static async Task OpenSettingsAsync(string uri)
    {
        try
        {
            await Launcher.LaunchUriAsync(new Uri(uri));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[QuickSettings] could not open {uri}: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_wifi is not null)
            _wifi.StateChanged -= _onRadioChanged;
        if (_bluetooth is not null)
            _bluetooth.StateChanged -= _onRadioChanged;
        if (_start is not null)
            PowerManager.EnergySaverStatusChanged -= _onEnergySaverChanged;
    }

    private RadioInfo Describe(Radio? radio)
    {
        if (radio is null)
            return default;
        try
        {
            var state = radio.State;
            return new RadioInfo(true, state == RadioState.On, _radioAccess && state != RadioState.Disabled);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[QuickSettings] radio read failed: {ex.Message}");
            return default;
        }
    }

    private void RefreshBrightness()
    {
        int? level = null;
        try
        {
            using var searcher = new ManagementObjectSearcher(WmiNamespace, "SELECT CurrentBrightness FROM WmiMonitorBrightness WHERE Active = TRUE");
            using var results = searcher.Get();
            foreach (var item in results)
            {
                using (item)
                    level ??= Convert.ToInt32(item["CurrentBrightness"]);
            }
        }
        catch (Exception ex)
        {
            // "Not supported" on displays whose driver has no brightness control (external monitors, most desktops).
            Debug.WriteLine($"[QuickSettings] brightness unavailable: {ex.Message}");
        }

        lock (_gate)
        {
            if (_brightness == level)
                return;
            _brightness = level;
        }
        RaiseChanged();
    }

    private static void WriteBrightness(int percent)
    {
        using var searcher = new ManagementObjectSearcher(WmiNamespace, "SELECT * FROM WmiMonitorBrightnessMethods WHERE Active = TRUE");
        using var results = searcher.Get();
        foreach (ManagementObject item in results)
        {
            // WmiSetBrightness(Timeout in seconds, Brightness in percent).
            using (item)
                item.InvokeMethod("WmiSetBrightness", [1u, (byte)percent]);
        }
    }

    private void RaiseChanged()
    {
        if (!_disposed)
            Changed?.Invoke();
    }
}
