// Adapted from AF Media Bar (LyricsMetadataScore, LyricsSearch, LyricsSearchQueryPolicy), MIT License,
// Copyright (c) 2026 AmorFate. See THIRD-PARTY-NOTICES.md.
using System.Diagnostics;
using System.Text.RegularExpressions;
using F23.StringSimilarity;
using Lyricify.Lyrics.Searchers;

namespace DynamicIsland.Services.Lyrics;

/// <summary>
/// Application-owned candidate scoring: Jaro-Winkler similarity on title and artist (40% each), album and a linear
/// duration decay (10% each). The library's own metadata ranking is never used.
/// </summary>
internal static partial class LyricsMatcher
{
    public const int MinimumScore = 60;
    private const double TitleWeight = 0.40;
    private const double ArtistWeight = 0.40;
    private const double AlbumWeight = 0.10;
    private const double DurationWeight = 0.10;
    private static readonly JaroWinkler Similarity = new();
    private static readonly string[] ArtistSeparators = [", ", ",", " & ", "&", "、", " / ", "/", ";", " feat. ", " ft. ", " x "];

    public sealed record Candidate(ISearchResult Result, int Score);

    /// <summary>Runs the query variants against a searcher and returns the best candidate at or above <paramref name="minimum"/>.</summary>
    public static async Task<Candidate?> SearchAsync(LyricsQuery query, ISearcher searcher, CancellationToken token,
        int minimum = MinimumScore)
    {
        Candidate? best = null;
        foreach (var text in BuildQueries(query))
        {
            token.ThrowIfCancellationRequested();
            List<ISearchResult>? results;
            try
            {
                // The string overload returns raw candidates; the ITrackMetadata overload applies the library's own ranking.
                results = await searcher.SearchForResults(text).WaitAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Lyrics] {searcher.Name} search failed: {ex.Message}");
                continue;
            }

            foreach (var result in results ?? [])
            {
                var score = Score(query, result.Title, result.Artists, result.Album, result.DurationMs / 1000d);
                if (score >= minimum && (best is null || score > best.Score))
                    best = new Candidate(result, score);
            }
            if (best is not null)
                break;
        }
        return best;
    }

    public static int Score(LyricsQuery query, string? title, IEnumerable<string>? artists, string? album, double? durationSeconds)
    {
        var requestArtists = SplitArtists(query.Artist);
        var candidateArtists = (artists ?? []).SelectMany(SplitArtists).ToArray();

        var titleScore = Compare(WithoutDecoration(query.Title), WithoutDecoration(title));
        var artistScore = (from requested in requestArtists
                           from candidate in candidateArtists
                           select Compare(requested, candidate)).DefaultIfEmpty(0).Max();
        var albumScore = Compare(query.Album, album);
        var durationScore = CompareDuration(query.DurationSeconds, durationSeconds);
        return (int)Math.Round(100 * (TitleWeight * titleScore + ArtistWeight * artistScore +
                                      AlbumWeight * albumScore + DurationWeight * durationScore));
    }

    public static string[] SplitArtists(string? artist)
    {
        var parts = (artist ?? string.Empty).Split(ArtistSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 0 ? [string.Empty] : parts.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IEnumerable<string> BuildQueries(LyricsQuery query)
    {
        var title = WithoutDecoration(query.Title);
        var artists = SplitArtists(query.Artist);
        return new[]
            {
                $"{title} {string.Join(" ", artists)}".Trim(),
                $"{title} {artists[0]}".Trim(),
                title
            }
            .Where(text => text.Length > 0)
            .Distinct(StringComparer.Ordinal);
    }

    /// <summary>Drops a trailing "(Official Video)" / "[Lyrics]" / "- Topic" style suffix that players and YouTube add.</summary>
    private static string WithoutDecoration(string? title)
    {
        var value = (title ?? string.Empty).Trim();
        value = Decoration().Replace(value, string.Empty).Trim();
        return value.Length > 0 ? value : (title ?? string.Empty).Trim();
    }

    private static double Compare(string? left, string? right)
    {
        left = left?.Trim().ToLowerInvariant() ?? string.Empty;
        right = right?.Trim().ToLowerInvariant() ?? string.Empty;
        if (left.Length == 0 && right.Length == 0)
            return 1;
        if (left.Length == 0 || right.Length == 0)
            return 0;
        return Similarity.Similarity(left, right);
    }

    private static double CompareDuration(double? local, double? remote)
    {
        if (local is not > 0 || remote is not > 0 || !double.IsFinite(local.Value) || !double.IsFinite(remote.Value))
            return 0;
        var difference = Math.Abs(local.Value - remote.Value);
        if (difference <= 1)
            return 1;
        if (difference >= 10)
            return 0;
        return 1 - (difference - 1) / 9;
    }

    [GeneratedRegex(@"\s*[\(\[【](official|lyric|lyrics|audio|video|mv|m/v|visualizer|hd|4k)[^\)\]】]*[\)\]】]\s*$|\s+-\s+topic$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Decoration();
}
