using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DynamicIsland.Services;

namespace DynamicIsland.ViewModels;

/// <summary>One card in the large island's notification stack.</summary>
public sealed partial class NotificationItemViewModel : ObservableObject
{
    private readonly DateTimeOffset _time;

    [ObservableProperty]
    private string _timeText = string.Empty;

    public NotificationItemViewModel(IslandNotification notification, string fallbackAppName, Func<uint, Task> remove)
    {
        Id = notification.Id;
        AppName = string.IsNullOrWhiteSpace(notification.AppName) ? fallbackAppName : notification.AppName;
        Title = notification.Title;
        Body = notification.Body;
        Icon = ArtworkProcessor.DecodeIcon(notification.Icon);
        _time = notification.Time;
        RemoveCommand = new AsyncRelayCommand(() => remove(Id));
        RefreshTime(DateTimeOffset.Now);
    }

    public uint Id { get; }
    public string AppName { get; }
    public string Title { get; }
    public string Body { get; }
    public bool HasBody => Body.Length > 0;
    public ImageSource? Icon { get; }
    public bool HasIcon => Icon is not null;
    public IAsyncRelayCommand RemoveCommand { get; }

    public void RefreshTime(DateTimeOffset now)
    {
        var age = now - _time;
        TimeText = age.TotalMinutes < 1 ? "now"
            : age.TotalHours < 1 ? $"{(int)age.TotalMinutes}m ago"
            : age.TotalDays < 1 ? $"{(int)age.TotalHours}h ago"
            : _time.LocalDateTime.ToString("d", System.Globalization.CultureInfo.CurrentCulture);
    }
}
