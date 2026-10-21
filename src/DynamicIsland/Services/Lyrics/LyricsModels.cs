namespace DynamicIsland.Services.Lyrics;

public sealed record LyricsQuery(string Title, string Artist, string Album, double? DurationSeconds)
{
    /// <summary>Stable cache key; case and surrounding whitespace do not create separate entries.</summary>
    public string CacheKey =>
        $"{Title.Trim().ToLowerInvariant()}\n{Artist.Trim().ToLowerInvariant()}\n{Album.Trim().ToLowerInvariant()}\n{Math.Round(DurationSeconds ?? 0)}";
}

public sealed record LyricLine(TimeSpan Start, string Text);

/// <summary>Line-synced lyrics ordered by start time.</summary>
public sealed class LyricsDocument(IReadOnlyList<LyricLine> lines)
{
    public IReadOnlyList<LyricLine> Lines { get; } = lines;

    /// <summary>Index of the line playing at <paramref name="position"/>, or -1 before the first line.</summary>
    public int IndexAt(TimeSpan position)
    {
        int low = 0, high = Lines.Count - 1, found = -1;
        while (low <= high)
        {
            var middle = (low + high) / 2;
            if (Lines[middle].Start <= position)
            {
                found = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }
        return found;
    }
}

/// <summary>A source's best answer: lyrics, or a confirmed "this track has none" when Document is null.</summary>
public sealed record LyricsMatch(string SourceId, int Score, LyricsDocument? Document);

public interface ILyricsSource
{
    string Id { get; }

    /// <summary>Returns null when nothing usable matched or the request failed.</summary>
    Task<LyricsMatch?> FindAsync(LyricsQuery query, CancellationToken token);
}

public static class LyricsSourceIds
{
    public const string QqMusic = "QQMusic";
    public const string NetEase = "NetEase";
    public const string Lrclib = "LRCLIB";
}
