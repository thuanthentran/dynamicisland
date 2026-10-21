using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DynamicIsland.Platform;
using DynamicIsland.Services;
using DynamicIsland.Services.Lyrics;

namespace DynamicIsland.ViewModels;

/// <summary>
/// Bindable island state. Service events are marshalled onto the UI thread through the SynchronizationContext captured at
/// construction; the host owns size, mode and motion and calls <see cref="Tick"/> from its timer.
/// </summary>
public sealed partial class IslandViewModel : ObservableObject, IDisposable
{
    private const string NoMediaTitle = "Nothing playing";
    private const string UnknownTitle = "Unknown title";
    private const string FallbackAppName = "Notification";
    private const int VisibleNotificationLimit = 3;
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan LyricLead = TimeSpan.FromMilliseconds(250);

    private readonly MediaService _media;
    private readonly NotificationService _notifications;
    private readonly SystemStatusService _status;
    private readonly AudioSpectrumService _spectrum;
    private readonly LyricsService _lyrics;
    private readonly SettingsService _settings;
    private readonly Action _exit;
    private readonly SynchronizationContext _ui;

    private MediaSnapshot _snapshot = MediaSnapshot.Empty;
    private byte[]? _thumbnailBytes;
    private TimeSpan _positionAnchor;
    private DateTimeOffset _anchoredAt;
    private DateTimeOffset _lastProgressAt;
    private int _lastClockMinute = -1;
    private string? _lyricsKey;
    private CancellationTokenSource? _lyricsCts;
    private LyricsDocument? _lyricsDocument;
    private int _lyricIndex = int.MinValue;
    private byte[]? _appIconBytes;
    private Color? _artworkColor;
    private bool _spectrumVisible;
    private bool _disposed;

    [ObservableProperty] private string _title = NoMediaTitle;
    [ObservableProperty] private string _artist = string.Empty;
    [ObservableProperty] private string _album = string.Empty;
    [ObservableProperty] private string _sourceAppName = string.Empty;
    [ObservableProperty] private ImageSource? _sourceAppIcon;
    [ObservableProperty] private ImageSource? _artwork;
    [ObservableProperty] private ImageSource? _blurredArtwork;
    [ObservableProperty] private bool _hasMedia;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayPauseGlyph))]
    private bool _isPlaying;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviousCommand))]
    private bool _canPrevious;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PlayPauseCommand))]
    private bool _canPlayPause;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    private bool _canNext;

    [ObservableProperty] private bool _canSeek;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _positionText = "0:00";
    [ObservableProperty] private string _durationText = "0:00";

    [ObservableProperty] private bool _hasLyrics;
    [ObservableProperty] private string _previousLyric = string.Empty;
    [ObservableProperty] private string _currentLyric = string.Empty;
    [ObservableProperty] private string _nextLyric = string.Empty;

    [ObservableProperty] private string _clockText = string.Empty;
    [ObservableProperty] private string _networkGlyph = "";
    [ObservableProperty] private string _networkBaseGlyph = string.Empty;
    [ObservableProperty] private double _networkOpacity = 1;
    [ObservableProperty] private string _volumeGlyph = "";
    [ObservableProperty] private bool _hasBattery;
    [ObservableProperty] private string _batteryGlyph = "";
    [ObservableProperty] private string _batteryText = string.Empty;

    [ObservableProperty] private bool _hasNotifications;
    [ObservableProperty] private string _moreNotificationsText = string.Empty;
    [ObservableProperty] private bool _hasMoreNotifications;
    [ObservableProperty] private string _notificationStatusText = "Notifications: starting…";

    [ObservableProperty] private string _bannerApp = string.Empty;
    [ObservableProperty] private string _bannerTitle = string.Empty;
    [ObservableProperty] private string _bannerBody = string.Empty;
    [ObservableProperty] private ImageSource? _bannerIcon;

    [ObservableProperty] private bool _isSettingsOpen;
    [ObservableProperty] private IslandPalette _palette = IslandPalette.Dark(Colors.DodgerBlue);

    /// <summary>Equalizer colour: the artwork's dominant colour tuned for the theme, or the system accent without artwork.</summary>
    [ObservableProperty] private Brush _equalizerBrush = Brushes.DodgerBlue;

    public IslandViewModel(
        MediaService media,
        NotificationService notifications,
        SystemStatusService status,
        AudioSpectrumService spectrum,
        LyricsService lyrics,
        SettingsService settings,
        Action exit)
    {
        _ui = SynchronizationContext.Current
            ?? throw new InvalidOperationException("IslandViewModel must be created on the UI thread.");
        _media = media;
        _notifications = notifications;
        _status = status;
        _spectrum = spectrum;
        _lyrics = lyrics;
        _settings = settings;
        _exit = exit;
        Settings = new SettingsViewModel(settings);

        _media.SnapshotChanged += OnSnapshotChanged;
        _notifications.NotificationArrived += OnNotificationArrived;
        _notifications.NotificationsChanged += OnNotificationsChanged;
        _status.StatusChanged += OnStatusChanged;
        _settings.Changed += OnSettingsChanged;
        RefreshPalette();
        Tick(DateTimeOffset.Now);
    }

    /// <summary>Raised on the UI thread when a new notification should be shown as a banner for the given time.</summary>
    public event Action<TimeSpan>? BannerRequested;

    /// <summary>Raised on the UI thread after settings change; the host re-reads timing values.</summary>
    public event Action<IslandSettings>? SettingsChanged;

    public SettingsViewModel Settings { get; }
    public IslandSettings CurrentSettings => _settings.Current;
    public ObservableCollection<NotificationItemViewModel> VisibleNotifications { get; } = [];
    public bool HasBannerIcon => BannerIcon is not null;
    public bool HasSourceAppIcon => SourceAppIcon is not null;

    // Segoe Fluent Icons, the solid set Quick Settings uses: PauseSolid / PlaySolid.
    public string PlayPauseGlyph => IsPlaying ? "" : "";

    public async Task StartAsync()
    {
        StartupRegistration.Apply(_settings.Current.StartWithWindows);
        await StartSafelyAsync("status", _status.StartAsync);
        OnStatusChanged(_status.Current);
        await StartSafelyAsync("media", _media.StartAsync);

        var access = await _notifications.StartAsync();
        NotificationStatusText = access switch
        {
            NotificationAccess.Allowed => "Notifications: on",
            NotificationAccess.Denied => "Notifications: allow access in Windows Settings",
            NotificationAccess.NoPackageIdentity => "Notifications: register the package (see README)",
            _ => "Notifications: failed to start"
        };
    }

    /// <summary>Advances the clock, playback position and lyric line. Called by the host timer on the UI thread.</summary>
    public void Tick(DateTimeOffset now)
    {
        if (now.Minute != _lastClockMinute)
        {
            _lastClockMinute = now.Minute;
            // Short time in the user's format, as the taskbar shows it.
            ClockText = now.ToString("t", System.Globalization.CultureInfo.CurrentCulture);
            foreach (var item in VisibleNotifications)
                item.RefreshTime(now);
        }

        if (!HasMedia || now - _lastProgressAt < ProgressInterval)
            return;
        _lastProgressAt = now;

        var position = CurrentPosition(now);
        var duration = _snapshot.Duration;
        Progress = duration > TimeSpan.Zero ? Math.Clamp(position / duration, 0, 1) : 0;
        PositionText = Format(position);
        DurationText = Format(duration);
        UpdateLyricLine(position);
    }

    /// <summary>Tells the spectrum service whether the equalizer is on screen; capture runs only while it is and media plays.</summary>
    public void SetSpectrumVisible(bool visible)
    {
        _spectrumVisible = visible;
        _spectrum.SetDemand(visible && IsPlaying, _snapshot.AppId);
    }

    public bool ReadSpectrum(Span<float> levels) => _spectrum.ReadBands(levels);

    public void RefreshTheme() => RefreshPalette();

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _lyricsCts?.Cancel();
        _media.SnapshotChanged -= OnSnapshotChanged;
        _notifications.NotificationArrived -= OnNotificationArrived;
        _notifications.NotificationsChanged -= OnNotificationsChanged;
        _status.StatusChanged -= OnStatusChanged;
        _settings.Changed -= OnSettingsChanged;
    }

    [RelayCommand(CanExecute = nameof(CanPrevious))]
    private Task PreviousAsync() => _media.SkipPreviousAsync();

    [RelayCommand(CanExecute = nameof(CanPlayPause))]
    private Task PlayPauseAsync() => _media.TogglePlayPauseAsync();

    [RelayCommand(CanExecute = nameof(CanNext))]
    private Task NextAsync() => _media.SkipNextAsync();

    [RelayCommand]
    private async Task SeekAsync(double fraction)
    {
        if (!CanSeek)
            return;
        var target = TimeSpan.FromTicks((long)(_snapshot.Duration.Ticks * Math.Clamp(fraction, 0, 1)));
        // Show the new position immediately; the session's next timeline event confirms it.
        _positionAnchor = target;
        _anchoredAt = DateTimeOffset.Now;
        _lastProgressAt = DateTimeOffset.MinValue;
        Tick(DateTimeOffset.Now);
        await _media.SeekAsync(target);
    }

    [RelayCommand]
    private Task ClearNotificationsAsync() => _notifications.ClearAsync();

    [RelayCommand]
    private void OpenSettings() => IsSettingsOpen = true;

    [RelayCommand]
    private void CloseSettings() => IsSettingsOpen = false;

    [RelayCommand]
    private void Exit() => _exit();

    partial void OnBannerIconChanged(ImageSource? value) => OnPropertyChanged(nameof(HasBannerIcon));

    partial void OnSourceAppIconChanged(ImageSource? value) => OnPropertyChanged(nameof(HasSourceAppIcon));

    partial void OnIsPlayingChanged(bool value) => _spectrum.SetDemand(_spectrumVisible && value, _snapshot.AppId);

    private static async Task StartSafelyAsync(string name, Func<Task> start)
    {
        try
        {
            await start();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Island] {name} unavailable: {ex.Message}");
        }
    }

    private void Post(Action action) => _ui.Post(_ =>
    {
        if (!_disposed)
            action();
    }, null);

    private void OnSnapshotChanged(MediaSnapshot snapshot) => Post(() => Apply(snapshot));

    private void OnNotificationArrived(IslandNotification notification) => Post(() => PresentBanner(notification));

    private void OnNotificationsChanged(IReadOnlyList<IslandNotification> notifications) => Post(() => SyncNotifications(notifications));

    private void OnStatusChanged(SystemStatus status) => Post(() => ApplyStatus(status));

    private void OnSettingsChanged(IslandSettings settings)
    {
        StartupRegistration.Apply(settings.StartWithWindows);
        RefreshPalette();
        // Toggling lyrics or a source invalidates the current result.
        _lyricsKey = null;
        RequestLyrics();
        SettingsChanged?.Invoke(settings);
    }

    private void Apply(MediaSnapshot snapshot)
    {
        var previous = _snapshot;
        _snapshot = snapshot;
        HasMedia = snapshot.HasSession && (snapshot.Title.Length > 0 || snapshot.Artist.Length > 0);
        Title = !HasMedia ? NoMediaTitle : string.IsNullOrWhiteSpace(snapshot.Title) ? UnknownTitle : snapshot.Title;
        Artist = snapshot.Artist;
        Album = snapshot.Album;
        SourceAppName = snapshot.AppName;
        if (!ReferenceEquals(snapshot.AppIcon, _appIconBytes))
        {
            _appIconBytes = snapshot.AppIcon;
            SourceAppIcon = ArtworkProcessor.DecodeIcon(snapshot.AppIcon);
        }
        IsPlaying = snapshot.IsPlaying;
        CanPrevious = snapshot.CanPrevious;
        CanPlayPause = snapshot.CanPlayPause;
        CanNext = snapshot.CanNext;
        CanSeek = snapshot.CanSeek;

        // Some players never stamp the timeline; anchor those to the moment the snapshot arrived.
        var stamp = snapshot.PositionUpdatedAt;
        var now = DateTimeOffset.Now;
        _positionAnchor = snapshot.Position;
        _anchoredAt = stamp.Year < 2000 || stamp > now ? now : stamp;
        _lastProgressAt = DateTimeOffset.MinValue;

        if (!ReferenceEquals(snapshot.Thumbnail, _thumbnailBytes))
        {
            _thumbnailBytes = snapshot.Thumbnail;
            var processed = ArtworkProcessor.Process(snapshot.Thumbnail);
            Artwork = processed?.Sharp;
            BlurredArtwork = processed?.Blurred;
            _artworkColor = processed?.Dominant;
            RefreshPalette();
        }

        if (previous.AppId != snapshot.AppId)
            _spectrum.SetDemand(_spectrumVisible && IsPlaying, snapshot.AppId);

        RequestLyrics();
        Tick(now);
    }

    private TimeSpan CurrentPosition(DateTimeOffset now)
    {
        var position = IsPlaying ? _positionAnchor + (now - _anchoredAt) : _positionAnchor;
        if (position < TimeSpan.Zero)
            return TimeSpan.Zero;
        return _snapshot.Duration > TimeSpan.Zero && position > _snapshot.Duration ? _snapshot.Duration : position;
    }

    private void RequestLyrics()
    {
        var settings = _settings.Current;
        var query = new LyricsQuery(_snapshot.Title, _snapshot.Artist, _snapshot.Album,
            _snapshot.Duration > TimeSpan.Zero ? _snapshot.Duration.TotalSeconds : null);
        var key = HasMedia && settings.LyricsEnabled ? query.CacheKey : string.Empty;
        if (key == _lyricsKey)
            return;

        _lyricsKey = key;
        _lyricsCts?.Cancel();
        _lyricsCts = null;
        SetLyrics(null);
        if (key.Length == 0)
            return;

        var enabled = new HashSet<string>();
        if (settings.QqMusicEnabled) enabled.Add(LyricsSourceIds.QqMusic);
        if (settings.NetEaseEnabled) enabled.Add(LyricsSourceIds.NetEase);
        if (settings.LrclibEnabled) enabled.Add(LyricsSourceIds.Lrclib);

        var cts = new CancellationTokenSource();
        _lyricsCts = cts;
        _ = LoadLyricsAsync(query, key, enabled, cts.Token);
    }

    private async Task LoadLyricsAsync(LyricsQuery query, string key, IReadOnlySet<string> enabled, CancellationToken token)
    {
        try
        {
            var document = await Task.Run(() => _lyrics.FindAsync(query, enabled, token), token);
            if (!token.IsCancellationRequested && !_disposed && key == _lyricsKey)
                SetLyrics(document);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Island] lyrics failed: {ex.Message}");
        }
    }

    private void SetLyrics(LyricsDocument? document)
    {
        _lyricsDocument = document;
        _lyricIndex = int.MinValue;
        HasLyrics = document is not null;
        if (document is null)
            PreviousLyric = CurrentLyric = NextLyric = string.Empty;
        else
            UpdateLyricLine(CurrentPosition(DateTimeOffset.Now));
    }

    private void UpdateLyricLine(TimeSpan position)
    {
        if (_lyricsDocument is not { } document)
            return;
        var index = document.IndexAt(position + LyricLead);
        if (index == _lyricIndex)
            return;
        _lyricIndex = index;

        var lines = document.Lines;
        PreviousLyric = index >= 1 ? lines[index - 1].Text : string.Empty;
        CurrentLyric = index >= 0 ? lines[index].Text : "♪";
        NextLyric = index + 1 < lines.Count ? lines[index + 1].Text : string.Empty;
    }

    private void PresentBanner(IslandNotification notification)
    {
        BannerApp = string.IsNullOrWhiteSpace(notification.AppName) ? FallbackAppName : notification.AppName;
        BannerTitle = notification.Title;
        BannerBody = notification.Body;
        BannerIcon = ArtworkProcessor.DecodeIcon(notification.Icon);
        BannerRequested?.Invoke(TimeSpan.FromSeconds(_settings.Current.NotificationSeconds));
    }

    /// <summary>Keeps existing cards (and their entrance animation state) when the newest-first list shifts.</summary>
    private void SyncNotifications(IReadOnlyList<IslandNotification> notifications)
    {
        var visible = notifications.Take(VisibleNotificationLimit).ToArray();
        for (var index = 0; index < visible.Length; index++)
        {
            var id = visible[index].Id;
            if (index < VisibleNotifications.Count && VisibleNotifications[index].Id == id)
                continue;

            var existing = -1;
            for (var probe = index + 1; probe < VisibleNotifications.Count; probe++)
            {
                if (VisibleNotifications[probe].Id == id)
                {
                    existing = probe;
                    break;
                }
            }

            if (existing >= 0)
                VisibleNotifications.Move(existing, index);
            else
                VisibleNotifications.Insert(index, new NotificationItemViewModel(visible[index], FallbackAppName, _notifications.RemoveAsync));
        }
        while (VisibleNotifications.Count > visible.Length)
            VisibleNotifications.RemoveAt(VisibleNotifications.Count - 1);

        HasNotifications = notifications.Count > 0;
        var more = notifications.Count - visible.Length;
        HasMoreNotifications = more > 0;
        MoreNotificationsText = more > 0 ? $"+{more} more" : string.Empty;
    }

    private void ApplyStatus(SystemStatus status)
    {
        // Wi-Fi is drawn like the taskbar: the full fan faded behind, the received bars on top.
        NetworkBaseGlyph = status.Network is NetworkKind.Wifi or NetworkKind.Cellular ? "" : string.Empty;
        NetworkGlyph = status.Network switch
        {
            NetworkKind.None => "",
            NetworkKind.Ethernet => "",
            _ => status.SignalBars switch
            {
                <= 1 => "",
                2 => "",
                3 => "",
                _ => ""
            }
        };
        NetworkOpacity = status.Network == NetworkKind.None || status.HasInternet ? 1 : 0.55;

        VolumeGlyph = status.Muted || status.Volume <= 0.001f ? ""
            : status.Volume < 0.34f ? ""
            : status.Volume < 0.67f ? ""
            : "";

        HasBattery = status.HasBattery;
        var step = Math.Clamp((int)Math.Round(status.BatteryPercent / 10.0), 0, 10);
        // Taskbar battery set: MobBattery0–10 (EBA0–EBAA), MobBatteryCharging0–10 (EBAB–EBB5).
        BatteryGlyph = ((char)((status.Charging ? 0xEBAB : 0xEBA0) + step)).ToString();
        BatteryText = $"{status.BatteryPercent}%";
    }

    private void RefreshPalette()
    {
        var light = _settings.Current.Theme switch
        {
            ThemePreference.Light => true,
            ThemePreference.Dark => false,
            _ => SystemTheme.AppsUseLightTheme()
        };
        var accent = SystemTheme.Accent(forDarkSurface: !light) ?? (light ? Color.FromRgb(0x00, 0x5F, 0xB8) : Color.FromRgb(0x60, 0xCD, 0xFF));
        Palette = light ? IslandPalette.Light(accent) : IslandPalette.Dark(accent);

        if (_artworkColor is { } artwork)
        {
            var brush = new SolidColorBrush(ArtworkProcessor.ReadableOn(artwork, light));
            brush.Freeze();
            EqualizerBrush = brush;
        }
        else
        {
            EqualizerBrush = Palette.Accent;
        }
    }

    private static string Format(TimeSpan value) =>
        value.TotalHours >= 1 ? value.ToString(@"h\:mm\:ss") : value.ToString(@"m\:ss");
}
