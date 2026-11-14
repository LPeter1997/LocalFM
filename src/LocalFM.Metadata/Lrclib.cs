using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using LocalFM.Library;
using LocalFM.Library.Enrich;

namespace LocalFM.Metadata;

/// <summary>LRCLIB: free synced lyrics, matched by artist/title/album/duration.</summary>
public sealed partial class Lrclib(CachedHttp http)
{
    public const string Provider = "lrclib";

    public sealed record Result(LyricsData? Synced, LyricsData? Plain, bool Instrumental);

    public async Task<Result?> GetAsync(string artist, string title, string? album, TimeSpan? duration, CancellationToken ct = default)
    {
        string url = $"https://lrclib.net/api/get?artist_name={Uri.EscapeDataString(artist)}&track_name={Uri.EscapeDataString(title)}";
        if (!string.IsNullOrWhiteSpace(album)) url += $"&album_name={Uri.EscapeDataString(album)}";
        if (duration is { } d) url += $"&duration={(int)Math.Round(d.TotalSeconds)}";
        var res = await http.GetAsync(Provider, url, TimeSpan.FromDays(60), ct: ct).ConfigureAwait(false);
        if (res.Ok)
        {
            using var doc = JsonDocument.Parse(res.Body);
            return Parse(doc.RootElement);
        }
        if (res.Status != 404) return null;

        // Exact match failed (album/duration differ): search, and accept a close duration.
        string search = $"https://lrclib.net/api/search?artist_name={Uri.EscapeDataString(artist)}&track_name={Uri.EscapeDataString(title)}";
        var sres = await http.GetAsync(Provider, search, TimeSpan.FromDays(30), ct: ct).ConfigureAwait(false);
        if (!sres.Ok) return null;
        using var sdoc = JsonDocument.Parse(sres.Body);
        if (sdoc.RootElement.ValueKind != JsonValueKind.Array) return null;
        var candidates = sdoc.RootElement.EnumerateArray()
            .Where(e => Normalize.Key(e.Str("trackName")) == Normalize.Key(title))
            .Where(e => duration is null || e.Double("duration") is not { } ds || Math.Abs(ds - duration.Value.TotalSeconds) <= 3)
            .OrderByDescending(e => e.Str("syncedLyrics") is not null)
            .ToList();
        return candidates.Count == 0 ? null : Parse(candidates[0]);
    }

    private static Result Parse(JsonElement e)
    {
        string? synced = e.Str("syncedLyrics");
        string? plain = e.Str("plainLyrics");
        var format = synced is not null && WordTag().IsMatch(synced) ? LyricsFormat.EnhancedLrc : LyricsFormat.Lrc;
        return new Result(
            synced is null ? null : new LyricsData(Provider, format, synced),
            plain is null ? null : new LyricsData(Provider, LyricsFormat.Plain, plain),
            e.Bool("instrumental"));
    }

    [GeneratedRegex(@"<\d{1,2}:\d{2}(\.\d{1,3})?>")]
    private static partial Regex WordTag();
}

/// <summary>One lyric line with its start time (null for unsynced text) and optional per-word timings.</summary>
public sealed record LyricLine(TimeSpan? Time, string Text, IReadOnlyList<(TimeSpan Time, string Word)> Words);

public static partial class Lrc
{
    /// <summary>Parses LRC / enhanced LRC ("[mm:ss.xx] text", "&lt;mm:ss.xx&gt; word") into time-ordered lines.</summary>
    public static List<LyricLine> Parse(string content, LyricsFormat format)
    {
        var lines = new List<LyricLine>();
        if (format == LyricsFormat.Plain)
        {
            foreach (var l in content.Replace("\r", "").Split('\n')) lines.Add(new LyricLine(null, l.TrimEnd(), []));
            return lines;
        }
        double offsetMs = 0;
        foreach (var raw in content.Replace("\r", "").Split('\n'))
        {
            var offset = Offset().Match(raw);
            if (offset.Success) { offsetMs = double.Parse(offset.Groups[1].Value, CultureInfo.InvariantCulture); continue; }
            var stamps = LineTag().Matches(raw);
            if (stamps.Count == 0) continue;
            string text = raw[(stamps[^1].Index + stamps[^1].Length)..];
            var words = new List<(TimeSpan, string)>();
            var wordTags = WordTag().Matches(text);
            if (wordTags.Count > 0)
            {
                for (int i = 0; i < wordTags.Count; i++)
                {
                    int start = wordTags[i].Index + wordTags[i].Length;
                    int end = i + 1 < wordTags.Count ? wordTags[i + 1].Index : text.Length;
                    string w = text[start..end];
                    if (w.Trim().Length > 0) words.Add((Time(wordTags[i]) - TimeSpan.FromMilliseconds(offsetMs), w));
                }
                text = WordTag().Replace(text, "");
            }
            text = Spaces().Replace(text, " ").Trim();
            foreach (Match s in stamps)
                lines.Add(new LyricLine(Time(s) - TimeSpan.FromMilliseconds(offsetMs), text, words));
        }
        lines.Sort((a, b) => a.Time!.Value.CompareTo(b.Time!.Value));
        return lines;
    }

    private static TimeSpan Time(Match m)
    {
        int min = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        double sec = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        return TimeSpan.FromSeconds(min * 60 + sec);
    }

    [GeneratedRegex(@"\[(\d{1,3}):(\d{1,2}(?:[.:]\d{1,3})?)\]")]
    private static partial Regex LineTag();

    [GeneratedRegex(@"<(\d{1,2}):(\d{2}(?:\.\d{1,3})?)>")]
    private static partial Regex WordTag();

    [GeneratedRegex(@"^\[offset:\s*([+-]?\d+)\]", RegexOptions.IgnoreCase)]
    private static partial Regex Offset();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
