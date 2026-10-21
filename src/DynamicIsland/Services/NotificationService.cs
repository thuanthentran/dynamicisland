using System.Diagnostics;
using DynamicIsland.Platform;
using Windows.Foundation;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace DynamicIsland.Services;

public enum NotificationAccess
{
    NoPackageIdentity,
    Denied,
    Allowed,
    Failed
}

/// <summary>One toast as shown on the island. AppName may be empty when the source app has no display name.</summary>
public sealed record IslandNotification(
    uint Id,
    string AppName,
    string Title,
    string Body,
    DateTimeOffset Time,
    byte[]? Icon);

/// <summary>
/// Mirrors the toasts currently in Notification Center. Requires package identity and the user's consent; events are
/// raised off the UI thread.
/// </summary>
public sealed class NotificationService : IDisposable
{
    // NotificationChanged is not reliably delivered to desktop processes, so polling remains the source of truth and the
    // event only shortens latency. Ids already seen are never announced twice.
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private const ulong MaxIconBytes = 512 * 1024;
    private static readonly Size IconSize = new(64, 64);

    private readonly CancellationTokenSource _stop = new();
    private readonly TypedEventHandler<UserNotificationListener, UserNotificationChangedEventArgs> _onNotificationChanged;
    private readonly Dictionary<uint, IslandNotification> _described = [];
    private readonly Dictionary<string, byte[]?> _icons = new(StringComparer.OrdinalIgnoreCase);
    // Toasts without generic text (legacy templates, images only) are skipped once instead of parsed every poll.
    private HashSet<uint> _unreadable = [];
    private UserNotificationListener? _listener;
    private bool _subscribed;
    private bool _baselineTaken;
    private int _syncing;
    private volatile bool _disposed;

    public NotificationService()
    {
        _onNotificationChanged = (sender, args) => _ = SyncAsync();
    }

    /// <summary>A toast that appeared since the previous sync; only the newest of a burst is announced.</summary>
    public event Action<IslandNotification>? NotificationArrived;

    /// <summary>All toasts currently in Notification Center, newest first.</summary>
    public event Action<IReadOnlyList<IslandNotification>>? NotificationsChanged;

    /// <summary>Call on the UI thread: the first access request may show a consent prompt.</summary>
    public async Task<NotificationAccess> StartAsync()
    {
        if (!PackageIdentity.IsPresent)
            return NotificationAccess.NoPackageIdentity;

        try
        {
            var listener = UserNotificationListener.Current;
            if (await listener.RequestAccessAsync() != UserNotificationListenerAccessStatus.Allowed)
                return NotificationAccess.Denied;
            if (_disposed)
                return NotificationAccess.Failed;

            _listener = listener;
            await SyncAsync();
            TrySubscribe(listener);
            _ = Task.Run(() => PollAsync(_stop.Token));
            return NotificationAccess.Allowed;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Notifications] start failed: {ex}");
            return NotificationAccess.Failed;
        }
    }

    /// <summary>Removes one toast from Notification Center as well.</summary>
    public async Task RemoveAsync(uint id)
    {
        try
        {
            _listener?.RemoveNotification(id);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Notifications] remove {id} failed: {ex.Message}");
        }
        await SyncAsync().ConfigureAwait(false);
    }

    public async Task ClearAsync()
    {
        try
        {
            _listener?.ClearNotifications();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Notifications] clear failed: {ex.Message}");
        }
        await SyncAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _stop.Cancel();

        if (_subscribed && _listener is not null)
        {
            try
            {
                _listener.NotificationChanged -= _onNotificationChanged;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Notifications] unsubscribe failed: {ex.Message}");
            }
        }
    }

    private void TrySubscribe(UserNotificationListener listener)
    {
        try
        {
            listener.NotificationChanged += _onNotificationChanged;
            _subscribed = true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Notifications] change event unavailable, polling only: {ex.Message}");
        }
    }

    private async Task PollAsync(CancellationToken token)
    {
        try
        {
            using var timer = new PeriodicTimer(PollInterval);
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                await SyncAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task SyncAsync()
    {
        var listener = _listener;
        if (listener is null || _disposed || Interlocked.Exchange(ref _syncing, 1) == 1)
            return;

        try
        {
            var current = await listener.GetNotificationsAsync(NotificationKinds.Toast);
            var described = new List<IslandNotification>(current.Count);
            IslandNotification? newest = null;
            var changed = current.Count != _described.Count + _unreadable.Count;

            var unreadable = new HashSet<uint>();
            foreach (var toast in current)
            {
                if (_described.TryGetValue(toast.Id, out var known))
                {
                    described.Add(known);
                    continue;
                }
                if (_unreadable.Contains(toast.Id))
                {
                    unreadable.Add(toast.Id);
                    continue;
                }

                changed = true;
                var item = await DescribeAsync(toast).ConfigureAwait(false);
                if (item is null)
                {
                    unreadable.Add(toast.Id);
                    continue;
                }
                described.Add(item);
                if (_baselineTaken && (newest is null || item.Time > newest.Time))
                    newest = item;
            }

            // Toasts dismissed from Notification Center drop out, so the cache does not grow without bound.
            _described.Clear();
            foreach (var item in described)
                _described[item.Id] = item;
            _unreadable = unreadable;
            _baselineTaken = true;

            if (_disposed)
                return;
            if (changed)
                NotificationsChanged?.Invoke(described.OrderByDescending(item => item.Time).ToArray());
            if (newest is not null)
                NotificationArrived?.Invoke(newest);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Notifications] sync failed: {ex.Message}");
        }
        finally
        {
            Volatile.Write(ref _syncing, 0);
        }
    }

    private async Task<IslandNotification?> DescribeAsync(UserNotification toast)
    {
        try
        {
            var texts = toast.Notification?.Visual?
                .GetBinding(KnownNotificationBindings.ToastGeneric)?
                .GetTextElements();
            if (texts is null || texts.Count == 0)
                return null;

            var app = toast.AppInfo;
            return new IslandNotification(
                toast.Id,
                app?.DisplayInfo?.DisplayName ?? string.Empty,
                texts[0].Text,
                string.Join(" ", texts.Skip(1).Select(text => text.Text)),
                toast.CreationTime,
                await GetIconAsync(app).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Notifications] unreadable toast {toast.Id}: {ex.Message}");
            return null;
        }
    }

    private async Task<byte[]?> GetIconAsync(Windows.ApplicationModel.AppInfo? app)
    {
        var key = app?.AppUserModelId;
        if (app is null || string.IsNullOrEmpty(key))
            return null;
        if (_icons.TryGetValue(key, out var cached))
            return cached;

        byte[]? bytes = null;
        try
        {
            bytes = await StreamBytes.ReadAsync(app.DisplayInfo.GetLogo(IconSize), MaxIconBytes).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Notifications] no logo for {key}: {ex.Message}");
        }
        _icons[key] = bytes;
        return bytes;
    }
}
