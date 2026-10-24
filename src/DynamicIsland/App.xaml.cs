using System.Diagnostics;
using System.Windows;
using DynamicIsland.Services;
using DynamicIsland.Services.Lyrics;
using DynamicIsland.ViewModels;
using DynamicIsland.Views;

namespace DynamicIsland;

/// <summary>
/// Composition root: creates services, the view model and the island host, and releases them in reverse order on exit.
/// </summary>
public partial class App : Application
{
    private const string InstanceMutexName = @"Local\DynamicIsland.SingleInstance";

    private Mutex? _instanceMutex;
    private bool _ownsInstanceMutex;
    private SettingsService? _settings;
    private MediaService? _media;
    private NotificationService? _notifications;
    private SystemStatusService? _status;
    private AudioSpectrumService? _spectrum;
    private QuickSettingsService? _quickSettings;
    private IslandViewModel? _viewModel;
    private IslandWindow? _island;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out _ownsInstanceMutex);
        if (!_ownsInstanceMutex)
        {
            Shutdown();
            return;
        }

        _settings = SettingsService.Load();
        _media = new MediaService();
        _notifications = new NotificationService();
        _status = new SystemStatusService();
        _spectrum = new AudioSpectrumService();
        _quickSettings = new QuickSettingsService();
        _viewModel = new IslandViewModel(_media, _notifications, _status, _spectrum, _quickSettings, new LyricsService(), _settings, Shutdown);
        _island = new IslandWindow(_viewModel);
        _island.Start();

        try
        {
            await _viewModel.StartAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[App] startup failed: {ex}");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _island?.Stop();
        _viewModel?.Dispose();
        _quickSettings?.Dispose();
        _spectrum?.Dispose();
        _status?.Dispose();
        _notifications?.Dispose();
        _media?.Dispose();
        _settings?.Flush();

        if (_ownsInstanceMutex)
            _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();

        base.OnExit(e);
    }
}
