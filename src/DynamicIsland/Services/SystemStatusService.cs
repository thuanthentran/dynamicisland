using System.Diagnostics;
using NAudio.CoreAudioApi;
using Windows.Networking.Connectivity;
using Windows.System.Power;

namespace DynamicIsland.Services;

public enum NetworkKind
{
    None,
    Wifi,
    Ethernet,
    Cellular
}

public sealed record SystemStatus(
    NetworkKind Network,
    int SignalBars,
    bool HasInternet,
    float Volume,
    bool Muted,
    bool HasBattery,
    int BatteryPercent,
    bool Charging)
{
    public static SystemStatus Unknown { get; } = new(NetworkKind.None, 0, false, 0, false, false, 0, false);
}

/// <summary>
/// Network, battery and master-volume state for the island's status row. Sources notify on their own threads; the
/// signal strength has no change event, so the network is also re-read periodically.
/// </summary>
public sealed class SystemStatusService : IDisposable
{
    private static readonly TimeSpan NetworkPollInterval = TimeSpan.FromSeconds(20);

    private readonly object _gate = new();
    private readonly NetworkStatusChangedEventHandler _onNetworkChanged;
    private readonly EventHandler<object> _onPowerChanged;
    private readonly AudioEndpointVolumeNotificationDelegate _onVolumeChanged;
    private SystemStatus _status = SystemStatus.Unknown;
    private Timer? _networkTimer;
    private MMDeviceEnumerator? _enumerator;
    private MMDeviceNotificationClient? _deviceNotifications;
    private MMDevice? _device;
    private AudioEndpointVolume? _endpoint;
    private volatile bool _disposed;

    public SystemStatusService()
    {
        _onNetworkChanged = _ => RefreshNetwork();
        _onPowerChanged = (_, _) => RefreshPower();
        _onVolumeChanged = data => Update(status => status with { Volume = data.MasterVolume, Muted = data.Muted });
    }

    public event Action<SystemStatus>? StatusChanged;

    public SystemStatus Current
    {
        get
        {
            lock (_gate)
                return _status;
        }
    }

    // Core Audio and the WinRT sources are created on a pool (MTA) thread, never on the STA UI thread.
    public Task StartAsync() => Task.Run(() =>
    {
        if (_disposed)
            return;
        NetworkInformation.NetworkStatusChanged += _onNetworkChanged;
        PowerManager.RemainingChargePercentChanged += _onPowerChanged;
        PowerManager.BatteryStatusChanged += _onPowerChanged;
        PowerManager.PowerSupplyStatusChanged += _onPowerChanged;
        _networkTimer = new Timer(_ => RefreshNetwork(), null, NetworkPollInterval, NetworkPollInterval);

        RefreshNetwork();
        RefreshPower();
        BindAudio();
    });

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        NetworkInformation.NetworkStatusChanged -= _onNetworkChanged;
        PowerManager.RemainingChargePercentChanged -= _onPowerChanged;
        PowerManager.BatteryStatusChanged -= _onPowerChanged;
        PowerManager.PowerSupplyStatusChanged -= _onPowerChanged;
        _networkTimer?.Dispose();

        lock (_gate)
        {
            ReleaseEndpoint();
            _deviceNotifications?.Dispose();
            _deviceNotifications = null;
            _enumerator?.Dispose();
            _enumerator = null;
        }
    }

    private void RefreshNetwork()
    {
        try
        {
            var profile = NetworkInformation.GetInternetConnectionProfile();
            if (profile is null)
            {
                Update(status => status with { Network = NetworkKind.None, SignalBars = 0, HasInternet = false });
                return;
            }

            var kind = profile.IsWlanConnectionProfile ? NetworkKind.Wifi
                : profile.IsWwanConnectionProfile ? NetworkKind.Cellular
                : NetworkKind.Ethernet;
            var bars = kind is NetworkKind.Wifi or NetworkKind.Cellular ? profile.GetSignalBars() ?? 0 : 0;
            var internet = profile.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.InternetAccess;
            Update(status => status with { Network = kind, SignalBars = bars, HasInternet = internet });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Status] network read failed: {ex.Message}");
        }
    }

    private void RefreshPower()
    {
        try
        {
            var battery = PowerManager.BatteryStatus;
            Update(status => status with
            {
                HasBattery = battery != BatteryStatus.NotPresent,
                BatteryPercent = PowerManager.RemainingChargePercent,
                Charging = PowerManager.PowerSupplyStatus != PowerSupplyStatus.NotPresent
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Status] power read failed: {ex.Message}");
        }
    }

    private void BindAudio()
    {
        if (_disposed)
            return;
        try
        {
            AudioEndpointVolume endpoint;
            lock (_gate)
            {
                ReleaseEndpoint();
                _enumerator ??= new MMDeviceEnumerator();
                if (_deviceNotifications is null)
                {
                    _deviceNotifications = _enumerator.CreateNotificationClient(false);
                    // The callback thread must not call back into Core Audio; rebind on the pool.
                    _deviceNotifications.DefaultDeviceChanged += (_, _) => Task.Run(BindAudio);
                }

                if (!_enumerator.TryGetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia, out var device))
                {
                    _status = _status with { Volume = 0, Muted = true };
                    return;
                }

                _device = device;
                endpoint = _endpoint = device.AudioEndpointVolume;
                endpoint.OnVolumeNotification += _onVolumeChanged;
            }
            Update(status => status with { Volume = endpoint.MasterVolumeLevelScalar, Muted = endpoint.Mute });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Status] audio endpoint unavailable: {ex.Message}");
        }
    }

    private void ReleaseEndpoint()
    {
        if (_endpoint is not null)
        {
            _endpoint.OnVolumeNotification -= _onVolumeChanged;
            _endpoint.Dispose();
            _endpoint = null;
        }
        _device?.Dispose();
        _device = null;
    }

    private void Update(Func<SystemStatus, SystemStatus> change)
    {
        SystemStatus next;
        lock (_gate)
        {
            next = change(_status);
            if (next == _status)
                return;
            _status = next;
        }
        if (!_disposed)
            StatusChanged?.Invoke(next);
    }
}
