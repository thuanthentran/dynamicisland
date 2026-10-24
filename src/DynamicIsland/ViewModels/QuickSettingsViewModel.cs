using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DynamicIsland.Services;
using Windows.Devices.Radios;

namespace DynamicIsland.ViewModels;

/// <summary>
/// The Quick settings tab: radio and Energy saver tiles, brightness and volume sliders, and links to Settings pages for
/// what has no public API. Service events arrive on pool threads and are posted to the UI thread.
/// </summary>
public sealed partial class QuickSettingsViewModel : ObservableObject
{
    private readonly QuickSettingsService _service;
    private readonly SystemStatusService _status;
    private readonly SynchronizationContext _ui;
    private readonly Action _collapse;

    [ObservableProperty] private bool _hasWifi;
    [ObservableProperty] private bool _wifiOn;
    [ObservableProperty] private bool _canToggleWifi;
    [ObservableProperty] private bool _hasBluetooth;
    [ObservableProperty] private bool _bluetoothOn;
    [ObservableProperty] private bool _canToggleBluetooth;
    [ObservableProperty] private bool _hasEnergySaver;
    [ObservableProperty] private bool _energySaverOn;
    [ObservableProperty] private bool _hasBrightness;
    [ObservableProperty] private double _brightness;
    [ObservableProperty] private double _volume;
    [ObservableProperty] private string _volumeGlyph = "";

    public QuickSettingsViewModel(QuickSettingsService service, SystemStatusService status, SynchronizationContext ui, Action collapse)
    {
        _service = service;
        _status = status;
        _ui = ui;
        _collapse = collapse;
        _service.Changed += () => _ui.Post(_ => Sync(), null);
    }

    /// <summary>Called each time the tab is shown: starts the service on first use and re-reads brightness.</summary>
    public async Task ActivateAsync()
    {
        await _service.StartAsync();
        await _service.RefreshAsync();
        Sync();
    }

    /// <summary>Volume comes from the status service the island already listens to.</summary>
    public void ApplyStatus(SystemStatus status, string volumeGlyph)
    {
        Volume = status.Volume;
        VolumeGlyph = volumeGlyph;
    }

    [RelayCommand]
    private Task ToggleWifiAsync() => CanToggleWifi ? _service.SetRadioAsync(RadioKind.WiFi, !WifiOn) : Task.CompletedTask;

    [RelayCommand]
    private Task ToggleBluetoothAsync() => CanToggleBluetooth ? _service.SetRadioAsync(RadioKind.Bluetooth, !BluetoothOn) : Task.CompletedTask;

    [RelayCommand]
    private void SetBrightness(double fraction)
    {
        Brightness = fraction;
        _service.SetBrightness((int)Math.Round(fraction * 100));
    }

    [RelayCommand]
    private void SetVolume(double fraction) => _status.SetVolume((float)fraction);

    [RelayCommand]
    private void ToggleMute() => _status.ToggleMute();

    /// <summary>Opens a Settings page and collapses the island so the page is not covered.</summary>
    [RelayCommand]
    private async Task OpenSettingsAsync(string uri)
    {
        _collapse();
        await QuickSettingsService.OpenSettingsAsync(uri);
    }

    private void Sync()
    {
        var wifi = _service.Wifi;
        (HasWifi, WifiOn, CanToggleWifi) = (wifi.Present, wifi.On, wifi.Controllable);
        var bluetooth = _service.Bluetooth;
        (HasBluetooth, BluetoothOn, CanToggleBluetooth) = (bluetooth.Present, bluetooth.On, bluetooth.Controllable);
        HasEnergySaver = _service.HasEnergySaver;
        EnergySaverOn = _service.EnergySaverOn;
        var brightness = _service.Brightness;
        HasBrightness = brightness.HasValue;
        if (brightness is { } level)
            Brightness = level / 100.0;
    }
}
