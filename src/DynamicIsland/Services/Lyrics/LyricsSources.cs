using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Lyricify.Lyrics.Searchers;
using NeteaseApi = Lyricify.Lyrics.Providers.Web.Netease.Api;
using QqMusicApi = Lyricify.Lyrics.Providers.Web.QQMusic.Api;

namespace DynamicIsland.Services.Lyrics;

/// <summary>QQ Music: search by title and artist, then the decrypted QRC endpoint, falling back to the legacy LRC endpoint.</summary>
internal sealed class QqMusicLyricsSource : ILyricsSource
{
    private readonly QqMusicApi _api = new();

    public string Id => LyricsSourceIds.QqMusic;

    public async Task<LyricsMatch?> FindAsync(LyricsQuery query, CancellationToken token)
    {
        var candidate = await LyricsMatcher.SearchAsync(query, SearchersHelper.GetSearcher(Searchers.QQMusic), token)
            .ConfigureAwait(false);
        if (candidate?.Result is not QQMusicSearchResult hit)
            return null;

        if (!string.IsNullOrWhiteSpace(hit.Id))
        {
            try
            {
                var qrc = await _api.GetLyricsAsync(hit.Id).WaitAsync(token).ConfigureAwait(false);
                if (LyricsParser.Parse(qrc?.Lyrics) is { } document)
                    return new LyricsMatch(Id, candidate.Score, document);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Lyrics] QQ QRC endpoint failed: {ex.Message}");
            }
        }

        if (string.IsNullOrWhiteSpace(hit.Mid))
            return null;
        var legacy = (await _api.GetLyric(hit.Mid).WaitAsync(token).ConfigureAwait(false))?.Decode();
        if (legacy is not { Code: 0 })
            return null;
        if (string.IsNullOrWhiteSpace(legacy.Lyric))
            return new LyricsMatch(Id, candidate.Score, null);
        return LyricsParser.Parse(legacy.Lyric) is { } lrc ? new LyricsMatch(Id, candidate.Score, lrc) : null;
    }
}

/// <summary>NetEase Cloud Music: search for the song id, then the line-level LRC endpoint.</summary>
internal sealed class NetEaseLyricsSource : ILyricsSource
{
    private readonly NeteaseApi _api = new();

    public string Id => LyricsSourceIds.NetEase;

    public async Task<LyricsMatch?> FindAsync(LyricsQuery query, CancellationToken token)
    {
        var candidate = await LyricsMatcher.SearchAsync(query, SearchersHelper.GetSearcher(Searchers.Netease), token)
            .ConfigureAwait(false);
        if (candidate?.Result is not NeteaseSearchResult hit || string.IsNullOrWhiteSpace(hit.Id))
            return null;

        var result = await _api.GetLyric(hit.Id).WaitAsync(token).ConfigureAwait(false);
        if (result is null)
            return null;
        if (result.Nolyric)
            return new LyricsMatch(Id, candidate.Score, null);
        return LyricsParser.Parse(result.Lrc?.Lyric) is { } document ? new LyricsMatch(Id, candidate.Score, document) : null;
    }
}

/// <summary>
/// LRCLIB: public API returning candidates with synced LRC; scored with the same metadata policy. Called directly because
/// the Lyricify 0.2.0 LRCLIB client returns null for every search (verified against the live API on 2026-10-21).
/// </summary>
internal sealed class LrclibLyricsSource : ILyricsSource
{
    private static readonly HttpClient Http = CreateClient();

    public string Id => LyricsSourceIds.Lrclib;

    public async Task<LyricsMatch?> FindAsync(LyricsQuery query, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(query.Title))
            return null;

        // Duration is scored locally rather than sent: preview-length tracks must still find the full song.
        var url = $"api/search?track_name={Uri.EscapeDataString(query.Title)}" +
                  $"&artist_name={Uri.EscapeDataString(LyricsMatcher.SplitArtists(query.Artist)[0])}";
        var results = await Http.GetFromJsonAsync<List<LrclibTrack>>(url, token).ConfigureAwait(false);
        var best = (results ?? [])
            .Select(item => (Item: item, Score: LyricsMatcher.Score(query, item.TrackName, [item.ArtistName ?? string.Empty], item.AlbumName, item.Duration)))
            .Where(entry => entry.Score >= LyricsMatcher.MinimumScore)
            .OrderByDescending(entry => entry.Score)
            .FirstOrDefault();
        if (best.Item is null)
            return null;
        if (best.Item.Instrumental)
            return new LyricsMatch(Id, best.Score, null);
        return LyricsParser.Parse(best.Item.SyncedLyrics) is { } document ? new LyricsMatch(Id, best.Score, document) : null;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { BaseAddress = new Uri("https://lrclib.net/"), Timeout = TimeSpan.FromSeconds(10) };
        // LRCLIB asks clients to identify themselves.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DynamicIsland/0.2 (+https://lrclib.net)");
        return client;
    }

    private sealed record LrclibTrack(
        [property: JsonPropertyName("trackName")] string? TrackName,
        [property: JsonPropertyName("artistName")] string? ArtistName,
        [property: JsonPropertyName("albumName")] string? AlbumName,
        [property: JsonPropertyName("duration")] double? Duration,
        [property: JsonPropertyName("instrumental")] bool Instrumental,
        [property: JsonPropertyName("syncedLyrics")] string? SyncedLyrics);
}
