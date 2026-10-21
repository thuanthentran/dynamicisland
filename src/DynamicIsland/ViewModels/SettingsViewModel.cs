using CommunityToolkit.Mvvm.ComponentModel;
using DynamicIsland.Services;

namespace DynamicIsland.ViewModels;

/// <summary>Two-way bindable mirror of <see cref="IslandSettings"/>; every change is committed to the settings service.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _service;
    private bool _loading;

    [ObservableProperty] private ThemePreference _theme;
    [ObservableProperty] private int _openDelayMs;
    [ObservableProperty] private int _mediumTimeoutSeconds;
    [ObservableProperty] private int _notificationSeconds;
    [ObservableProperty] private bool _lyricsEnabled;
    [ObservableProperty] private bool _qqMusicEnabled;
    [ObservableProperty] private bool _netEaseEnabled;
    [ObservableProperty] private bool _lrclibEnabled;
    [ObservableProperty] private bool _startWithWindows;

    public SettingsViewModel(SettingsService service)
    {
        _service = service;
        Load(service.Current);
    }

    private void Load(IslandSettings settings)
    {
        _loading = true;
        Theme = settings.Theme;
        OpenDelayMs = settings.OpenDelayMs;
        MediumTimeoutSeconds = settings.MediumTimeoutSeconds;
        NotificationSeconds = settings.NotificationSeconds;
        LyricsEnabled = settings.LyricsEnabled;
        QqMusicEnabled = settings.QqMusicEnabled;
        NetEaseEnabled = settings.NetEaseEnabled;
        LrclibEnabled = settings.LrclibEnabled;
        StartWithWindows = settings.StartWithWindows;
        _loading = false;
    }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_loading)
            return;
        _service.Update(current => current with
        {
            Theme = Theme,
            OpenDelayMs = OpenDelayMs,
            MediumTimeoutSeconds = MediumTimeoutSeconds,
            NotificationSeconds = NotificationSeconds,
            LyricsEnabled = LyricsEnabled,
            QqMusicEnabled = QqMusicEnabled,
            NetEaseEnabled = NetEaseEnabled,
            LrclibEnabled = LrclibEnabled,
            StartWithWindows = StartWithWindows
        });
    }
}
