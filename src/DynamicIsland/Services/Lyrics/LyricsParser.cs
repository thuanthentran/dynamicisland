using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace DynamicIsland.Services.Lyrics;

/// <summary>
/// Reduces every supported payload to line-synced text: LRC (one or more [mm:ss.xx] tags per line, optional [offset:]),
/// line-header formats [start,duration] used by QRC/YRC/Lyricify (syllable timings stripped), and QRC's XML wrapper.
/// Unsynced text yields null because it cannot follow playback.
/// </summary>
internal static partial class LyricsParser
{
    private static readonly TimeSpan CreditWindow = TimeSpan.FromSeconds(30);

    public static LyricsDocument? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var text = raw;
        var wrapped = QrcContent().Match(text);
        if (wrapped.Success)
            text = WebUtility.HtmlDecode(wrapped.Groups[1].Value);

        var lines = new List<LyricLine>();
        long offsetMs = 0;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '{')
                continue; // NetEase credit JSON

            var offset = Offset().Match(line);
            if (offset.Success)
            {
                offsetMs = long.Parse(offset.Groups[1].Value, CultureInfo.InvariantCulture);
                continue;
            }

            var header = LineHeader().Match(line);
            if (header.Success)
            {
                Add(lines, long.Parse(header.Groups[1].Value, CultureInfo.InvariantCulture), header.Groups[3].Value);
                continue;
            }

            var position = 0;
            var starts = new List<long>(1);
            while (LrcTag().Match(line, position) is { Success: true } tag && tag.Index == position)
            {
                starts.Add(ToMilliseconds(tag));
                position += tag.Length;
            }
            foreach (var start in starts)
                Add(lines, start - offsetMs, line[position..]);
        }

        if (lines.Count == 0 || lines.All(line => line.Start == TimeSpan.Zero))
            return null;
        return new LyricsDocument(lines.OrderBy(line => line.Start).ToList());
    }

    /// <summary>
    /// Drops the info block sources put before the lyrics: "label：value" credits (作词：…, 吉他：…, Produced by：…) and a
    /// "Title - Artist" header line, but only within the opening seconds so lyrics that contain a colon survive.
    /// </summary>
    public static LyricsDocument? WithoutInfoLines(LyricsDocument document, LyricsQuery query)
    {
        var title = query.Title.Trim();
        var lines = document.Lines.Where(line => line.Start >= CreditWindow || !IsInfoLine(line.Text, title, query.Artist)).ToList();
        return lines.Count == 0 ? null : new LyricsDocument(lines);
    }

    private static bool IsInfoLine(string text, string title, string artist)
    {
        if (CreditLabel().IsMatch(text))
            return true;
        return title.Length > 0 && text.Contains(title, StringComparison.OrdinalIgnoreCase) &&
               (text.Contains(" - ", StringComparison.Ordinal) ||
                (artist.Length > 0 && text.Contains(artist, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Serializes back to LRC for the disk cache.</summary>
    public static string ToLrc(LyricsDocument document)
    {
        var builder = new StringBuilder();
        foreach (var line in document.Lines)
            builder.Append(CultureInfo.InvariantCulture,
                $"[{(int)line.Start.TotalMinutes:00}:{line.Start.Seconds:00}.{line.Start.Milliseconds / 10:00}]{line.Text}\n");
        return builder.ToString();
    }

    private static void Add(List<LyricLine> lines, long startMs, string body)
    {
        var text = InlineTiming().Replace(body, string.Empty).Trim();
        if (text.Length > 0 && startMs >= 0)
            lines.Add(new LyricLine(TimeSpan.FromMilliseconds(startMs), text));
    }

    private static long ToMilliseconds(Match tag)
    {
        var minutes = long.Parse(tag.Groups[1].Value, CultureInfo.InvariantCulture);
        var seconds = long.Parse(tag.Groups[2].Value, CultureInfo.InvariantCulture);
        var fraction = tag.Groups[3].Value;
        var milliseconds = fraction.Length switch
        {
            0 => 0,
            1 => long.Parse(fraction, CultureInfo.InvariantCulture) * 100,
            2 => long.Parse(fraction, CultureInfo.InvariantCulture) * 10,
            _ => long.Parse(fraction[..3], CultureInfo.InvariantCulture)
        };
        return (minutes * 60 + seconds) * 1000 + milliseconds;
    }

    [GeneratedRegex(@"\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]")]
    private static partial Regex LrcTag();

    [GeneratedRegex(@"^\[(\d+),(\d+)\](.*)$")]
    private static partial Regex LineHeader();

    // QRC/YRC "(start,duration[,flag])", KRC "<start,duration,flag>", enhanced LRC "<mm:ss.xx>".
    [GeneratedRegex(@"\(\d+,\d+(?:,\d+)?\)|<\d+,\d+(?:,\d+)?>|<\d+:\d+(?:[.:]\d+)?>")]
    private static partial Regex InlineTiming();

    [GeneratedRegex(@"LyricContent=""([^""]*)""", RegexOptions.Singleline)]
    private static partial Regex QrcContent();

    [GeneratedRegex(@"^\[offset:\s*([+-]?\d+)\s*\]$", RegexOptions.IgnoreCase)]
    private static partial Regex Offset();

    // A short label (at most four words, no sentence punctuation) followed by a colon and a value.
    [GeneratedRegex(@"^\s*[\p{L}\p{N}&'./-]+(?:\s+[\p{L}\p{N}&'./-]+){0,3}\s*[:：]\s*\S", RegexOptions.CultureInvariant)]
    private static partial Regex CreditLabel();
}
