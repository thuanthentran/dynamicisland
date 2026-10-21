using System.Diagnostics;
using Windows.Foundation;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace DynamicIsland.Services;

/// <summary>Immutable view of the media session Windows currently considers active.</summary>
public sealed record MediaSnapshot(
    bool HasSession,
    string AppId,
    string AppName,
    byte[]? AppIcon,
    string Title,
    string Artist,
    string Album,
    bool IsPlaying,
    bool CanPrevious,
    bool CanPlayPause,
    bool CanNext,
    bool CanSeek,
    TimeSpan Position,
    TimeSpan Duration,
    DateTimeOffset PositionUpdatedAt,
    byte[]? Thumbnail)
{
    public static MediaSnapshot Empty { get; } = new(false, string.Empty, string.Empty, null, string.Empty, string.Empty,
        string.Empty, false, false, false, false, false, TimeSpan.Zero, TimeSpan.Zero, DateTimeOffset.MinValue, null);
}

/// <summary>
/// Reads and controls the current SMTC session. WinRT events arrive on background threads, so SnapshotChanged is raised off
/// the UI thread; a generation counter drops results that a newer refresh has already superseded.
/// </summary>
public sealed class MediaService : IDisposable
{
    private const ulong MaxThumbnailBytes = 4 * 1024 * 1024;

    private readonly object _gate = new();
    private readonly TypedEventHandler<GlobalSystemMediaTransportControlsSessionManager, CurrentSessionChangedEventArgs> _onCurrentSessionChanged;
    private readonly TypedEventHandler<GlobalSystemMediaTransportControlsSession, MediaPropertiesChangedEventArgs> _onMediaPropertiesChanged;
    private readonly TypedEventHandler<GlobalSystemMediaTransportControlsSession, PlaybackInfoChangedEventArgs> _onPlaybackInfoChanged;
    private readonly TypedEventHandler<GlobalSystemMediaTransportControlsSession, TimelinePropertiesChangedEventArgs> _onTimelineChanged;

    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private TimeSpan _timelineStart;
    private string? _thumbnailKey;
    private byte[]? _thumbnail;
    private readonly Dictionary<string, (string Name, byte[]? Icon)> _apps = new(StringComparer.OrdinalIgnoreCase);
    private int _generation;
    private volatile bool _disposed;

    public MediaService()
    {
        _onCurrentSessionChanged = (_, _) => AttachCurrentSession();
        _onMediaPropertiesChanged = (_, _) => QueueRefresh();
        _onPlaybackInfoChanged = (_, _) => QueueRefresh();
        _onTimelineChanged = (_, _) => QueueRefresh();
    }

    public event Action<MediaSnapshot>? SnapshotChanged;

    public async Task StartAsync()
    {
        var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        lock (_gate)
        {
            if (_disposed)
                return;
            _manager = manager;
            manager.CurrentSessionChanged += _onCurrentSessionChanged;
        }
        AttachCurrentSession();
    }

    public Task SkipPreviousAsync() => RunAsync(session => session.TrySkipPreviousAsync());

    public Task TogglePlayPauseAsync() => RunAsync(session => session.TryTogglePlayPauseAsync());

    public Task SkipNextAsync() => RunAsync(session => session.TrySkipNextAsync());

    /// <summary>Seeks to a position measured from the start of the track.</summary>
    public Task SeekAsync(TimeSpan position)
    {
        TimeSpan start;
        lock (_gate)
            start = _timelineStart;
        return RunAsync(session => session.TryChangePlaybackPositionAsync((start + position).Ticks));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            Interlocked.Increment(ref _generation);
            if (_manager is not null)
                _manager.CurrentSessionChanged -= _onCurrentSessionChanged;
            DetachSession();
            _manager = null;
        }
    }

    private void AttachCurrentSession()
    {
        GlobalSystemMediaTransportControlsSessionManager? manager;
        lock (_gate)
            manager = _disposed ? null : _manager;
        if (manager is null)
            return;

        var next = manager.GetCurrentSession();
        lock (_gate)
        {
            if (_disposed)
                return;
            DetachSession();
            _session = next;
            if (next is not null)
            {
                next.MediaPropertiesChanged += _onMediaPropertiesChanged;
                next.PlaybackInfoChanged += _onPlaybackInfoChanged;
                next.TimelinePropertiesChanged += _onTimelineChanged;
            }
        }
        QueueRefresh();
    }

    private void DetachSession()
    {
        if (_session is null)
            return;
        _session.MediaPropertiesChanged -= _onMediaPropertiesChanged;
        _session.PlaybackInfoChanged -= _onPlaybackInfoChanged;
        _session.TimelinePropertiesChanged -= _onTimelineChanged;
        _session = null;
    }

    private void QueueRefresh() => _ = RefreshAsync();

    private async Task RefreshAsync()
    {
        var generation = Interlocked.Increment(ref _generation);
        try
        {
            GlobalSystemMediaTransportControlsSession? session;
            lock (_gate)
                session = _session;

            var snapshot = session is null ? MediaSnapshot.Empty : await BuildSnapshotAsync(session).ConfigureAwait(false);
            if (_disposed || generation != Volatile.Read(ref _generation))
                return;
            SnapshotChanged?.Invoke(snapshot);
        }
        catch (Exception ex)
        {
            // The session can disappear between the event and the read; CurrentSessionChanged follows and refreshes again.
            Debug.WriteLine($"[Media] refresh failed: {ex.Message}");
        }
    }

    private async Task<MediaSnapshot> BuildSnapshotAsync(GlobalSystemMediaTransportControlsSession session)
    {
        var properties = await session.TryGetMediaPropertiesAsync();
        var playback = session.GetPlaybackInfo();
        var controls = playback.Controls;
        var timeline = session.GetTimelineProperties();

        var title = properties?.Title ?? string.Empty;
        var artist = properties?.Artist ?? string.Empty;
        var album = properties?.AlbumTitle ?? string.Empty;
        var appId = session.SourceAppUserModelId ?? string.Empty;
        var thumbnail = await GetThumbnailAsync($"{appId}\n{title}\n{artist}", properties?.Thumbnail).ConfigureAwait(false);
        var (appName, appIcon) = await GetSourceAppAsync(appId).ConfigureAwait(false);

        var duration = timeline.EndTime - timeline.StartTime;
        var position = timeline.Position - timeline.StartTime;
        lock (_gate)
            _timelineStart = timeline.StartTime;

        return new MediaSnapshot(
            HasSession: true,
            appId,
            appName,
            appIcon,
            title,
            artist,
            album,
            IsPlaying: playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            CanPrevious: controls.IsPreviousEnabled,
            CanPlayPause: controls.IsPlayPauseToggleEnabled || controls.IsPlayEnabled || controls.IsPauseEnabled,
            CanNext: controls.IsNextEnabled,
            CanSeek: controls.IsPlaybackPositionEnabled && duration > TimeSpan.Zero,
            Position: position < TimeSpan.Zero ? TimeSpan.Zero : position,
            Duration: duration < TimeSpan.Zero ? TimeSpan.Zero : duration,
            PositionUpdatedAt: timeline.LastUpdatedTime,
            thumbnail);
    }

    /// <summary>Playback-state events are frequent; the artwork is read again only when the track changes or was missing.</summary>
    private async Task<byte[]?> GetThumbnailAsync(string trackKey, IRandomAccessStreamReference? reference)
    {
        lock (_gate)
        {
            if (_thumbnailKey == trackKey && _thumbnail is not null)
                return _thumbnail;
        }

        var bytes = await StreamBytes.ReadAsync(reference, MaxThumbnailBytes).ConfigureAwait(false);
        lock (_gate)
        {
            _thumbnailKey = trackKey;
            _thumbnail = bytes;
        }
        return bytes;
    }

    /// <summary>
    /// Display name and logo of the playing app, as Quick Settings shows them. Packaged apps and desktop apps with a
    /// registered app id resolve through AppInfo; others fall back to the executable name.
    /// </summary>
    private async Task<(string Name, byte[]? Icon)> GetSourceAppAsync(string appId)
    {
        if (appId.Length == 0)
            return (string.Empty, null);
        lock (_gate)
        {
            if (_apps.TryGetValue(appId, out var known))
                return known;
        }

        var fallback = System.IO.Path.GetFileNameWithoutExtension(appId);
        (string Name, byte[]? Icon) app = (fallback.Length > 0 ? char.ToUpperInvariant(fallback[0]) + fallback[1..] : appId, null);
        try
        {
            var info = Windows.ApplicationModel.AppInfo.GetFromAppUserModelId(appId);
            var name = info?.DisplayInfo?.DisplayName;
            if (!string.IsNullOrWhiteSpace(name))
                app.Name = name;
            if (info?.DisplayInfo is { } display)
                app.Icon = await StreamBytes.ReadAsync(display.GetLogo(new Size(32, 32)), 256 * 1024).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Media] no app info for {appId}: {ex.Message}");
        }

        lock (_gate)
            _apps[appId] = app;
        return app;
    }

    private async Task RunAsync(Func<GlobalSystemMediaTransportControlsSession, IAsyncOperation<bool>> command)
    {
        GlobalSystemMediaTransportControlsSession? session;
        lock (_gate)
            session = _disposed ? null : _session;
        if (session is null)
            return;

        try
        {
            await command(session);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Media] command failed: {ex.Message}");
        }
    }
}
