using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace DynamicIsland.Services.Lyrics;

/// <summary>
/// Finds lyrics for a track: QQ Music first; when it is missing or scores below <see cref="PreferredScore"/>, the remaining
/// enabled sources run in parallel and the highest score wins. Hits are cached on disk; misses only in memory, so a track
/// is retried after a restart or once a source recovers.
/// </summary>
public sealed class LyricsService
{
    private const int PreferredScore = 80;
    private const int MemoryCapacity = 64;
    private const int DiskCapacity = 500;
    private static readonly TimeSpan SourceTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MissRetryAfter = TimeSpan.FromMinutes(30);

    private readonly IReadOnlyList<ILyricsSource> _sources =
        [new QqMusicLyricsSource(), new NetEaseLyricsSource(), new LrclibLyricsSource()];
    private readonly Dictionary<string, (LyricsDocument? Document, DateTime At)> _memory = [];
    private readonly LinkedList<string> _recent = [];
    private readonly object _gate = new();
    private readonly string _cacheDirectory = Path.Combine(SettingsService.DataDirectory, "Cache", "Lyrics");

    /// <summary>Returns null when no enabled source has synced lyrics for the track.</summary>
    public async Task<LyricsDocument?> FindAsync(LyricsQuery query, IReadOnlySet<string> enabledSources, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(query.Title) || enabledSources.Count == 0)
            return null;

        var key = query.CacheKey;
        if (TryGetMemory(key, out var remembered))
            return remembered;
        if (await ReadDiskAsync(key, token).ConfigureAwait(false) is { } cached)
        {
            Remember(key, cached);
            return cached;
        }

        var active = _sources.Where(source => enabledSources.Contains(source.Id)).ToArray();
        var preferred = active.FirstOrDefault(source => source.Id == LyricsSourceIds.QqMusic);
        var results = new List<LyricsMatch>();

        if (preferred is not null && await RunAsync(preferred, query, token).ConfigureAwait(false) is { } first)
        {
            if (first.Score >= PreferredScore)
                return Store(key, query, first);
            results.Add(first);
        }

        var fallbacks = await Task.WhenAll(active.Where(source => source != preferred)
            .Select(source => RunAsync(source, query, token))).ConfigureAwait(false);
        results.AddRange(fallbacks.OfType<LyricsMatch>());
        token.ThrowIfCancellationRequested();

        var best = results.OrderByDescending(match => match.Score).ThenBy(match => match.Document is null).FirstOrDefault();
        if (best is null)
        {
            Remember(key, null);
            return null;
        }
        return Store(key, query, best);
    }

    private static async Task<LyricsMatch?> RunAsync(ILyricsSource source, LyricsQuery query, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(SourceTimeout);
        try
        {
            return await source.FindAsync(query, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            Debug.WriteLine($"[Lyrics] {source.Id} timed out");
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Debug.WriteLine($"[Lyrics] {source.Id} failed: {ex.Message}");
            return null;
        }
    }

    private LyricsDocument? Store(string key, LyricsQuery query, LyricsMatch match)
    {
        var document = match.Document is { } found ? LyricsParser.WithoutInfoLines(found, query) : null;
        Remember(key, document);
        if (document is not null)
            _ = WriteDiskAsync(key, match.SourceId, document);
        return document;
    }

    private bool TryGetMemory(string key, out LyricsDocument? document)
    {
        lock (_gate)
        {
            if (_memory.TryGetValue(key, out var entry) &&
                (entry.Document is not null || DateTime.UtcNow - entry.At < MissRetryAfter))
            {
                document = entry.Document;
                return true;
            }
        }
        document = null;
        return false;
    }

    private void Remember(string key, LyricsDocument? document)
    {
        lock (_gate)
        {
            if (_memory.ContainsKey(key))
                _recent.Remove(key);
            _memory[key] = (document, DateTime.UtcNow);
            _recent.AddFirst(key);
            while (_recent.Count > MemoryCapacity)
            {
                _memory.Remove(_recent.Last!.Value);
                _recent.RemoveLast();
            }
        }
    }

    private string PathFor(string key) =>
        Path.Combine(_cacheDirectory, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key))) + ".lrc");

    private async Task<LyricsDocument?> ReadDiskAsync(string key, CancellationToken token)
    {
        try
        {
            var path = PathFor(key);
            return File.Exists(path) ? LyricsParser.Parse(await File.ReadAllTextAsync(path, token).ConfigureAwait(false)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async Task WriteDiskAsync(string key, string sourceId, LyricsDocument document)
    {
        try
        {
            Directory.CreateDirectory(_cacheDirectory);
            await File.WriteAllTextAsync(PathFor(key), $"[re:{sourceId}]\n{LyricsParser.ToLrc(document)}").ConfigureAwait(false);

            var files = new DirectoryInfo(_cacheDirectory).GetFiles("*.lrc");
            if (files.Length > DiskCapacity)
                foreach (var stale in files.OrderBy(file => file.LastWriteTimeUtc).Take(files.Length - DiskCapacity))
                    stale.Delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"[Lyrics] cache write failed: {ex.Message}");
        }
    }
}
