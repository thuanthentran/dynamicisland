using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace DynamicIsland.Services;

public enum ThemePreference
{
    System,
    Light,
    Dark
}

public sealed record IslandSettings
{
    public ThemePreference Theme { get; init; } = ThemePreference.System;
    public int OpenDelayMs { get; init; } = 300;
    public int MediumTimeoutSeconds { get; init; } = 3;
    public int NotificationSeconds { get; init; } = 5;
    public bool LyricsEnabled { get; init; } = true;
    public bool QqMusicEnabled { get; init; } = true;
    public bool NetEaseEnabled { get; init; } = true;
    public bool LrclibEnabled { get; init; } = true;
    public bool StartWithWindows { get; init; }

    public IslandSettings Normalize() => this with
    {
        Theme = Enum.IsDefined(Theme) ? Theme : ThemePreference.System,
        OpenDelayMs = Math.Clamp(OpenDelayMs, 100, 1500),
        MediumTimeoutSeconds = Math.Clamp(MediumTimeoutSeconds, 1, 30),
        NotificationSeconds = Math.Clamp(NotificationSeconds, 2, 30)
    };
}

/// <summary>
/// Owns %LOCALAPPDATA%\DynamicIsland\settings.json. Updates happen on the UI thread; saving is debounced and written
/// atomically on the pool. An unreadable file falls back to defaults and is replaced on the next save.
/// </summary>
public sealed class SettingsService
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(500);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _path;
    private readonly object _saveGate = new();
    private int _saveVersion;

    private SettingsService(string path, IslandSettings current)
    {
        _path = path;
        Current = current;
    }

    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DynamicIsland");

    public IslandSettings Current { get; private set; }

    public event Action<IslandSettings>? Changed;

    public static SettingsService Load()
    {
        var path = Path.Combine(DataDirectory, "settings.json");
        var settings = new IslandSettings();
        try
        {
            if (File.Exists(path))
                settings = JsonSerializer.Deserialize<IslandSettings>(File.ReadAllText(path), JsonOptions) ?? settings;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Settings] unreadable, using defaults: {ex.Message}");
        }
        return new SettingsService(path, settings.Normalize());
    }

    public void Update(Func<IslandSettings, IslandSettings> change)
    {
        var next = change(Current).Normalize();
        if (next == Current)
            return;
        Current = next;
        Changed?.Invoke(next);
        _ = SaveLaterAsync(Interlocked.Increment(ref _saveVersion));
    }

    /// <summary>Writes pending changes synchronously; used on exit.</summary>
    public void Flush() => Save(Current);

    private async Task SaveLaterAsync(int version)
    {
        await Task.Delay(SaveDelay).ConfigureAwait(false);
        if (version == Volatile.Read(ref _saveVersion))
            Save(Current);
    }

    private void Save(IslandSettings settings)
    {
        lock (_saveGate)
        {
            try
            {
                Directory.CreateDirectory(DataDirectory);
                var temp = _path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
                File.Move(temp, _path, overwrite: true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Settings] save failed: {ex.Message}");
            }
        }
    }
}

/// <summary>Per-user autostart through the Run key; no admin rights or scheduled task needed.</summary>
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DynamicIsland";

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled && Environment.ProcessPath is { } path)
                key.SetValue(ValueName, $"\"{path}\"");
            else
                key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Startup] update failed: {ex.Message}");
        }
    }
}
