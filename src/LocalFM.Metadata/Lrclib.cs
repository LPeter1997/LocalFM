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

    public sealed record Result(LyricsData? Synced, LyricsData? Plain, bool Instrumental, long Id = 0);

    /// <summary>The best entry: see <see cref="CandidatesAsync"/>.</summary>
    public async Task<Result?> GetAsync(string artist, string title, string? album, TimeSpan? duration, CancellationToken ct = default) =>
        (await CandidatesAsync(artist, title, album, duration, ct).ConfigureAwait(false)).FirstOrDefault();

    /// <summary>
    /// Every LRCLIB entry for the song whose length fits, best first. LRCLIB is crowd-sourced: entries for the same
    /// recording sometimes carry another version's timing. So synced entries are ranked by how many of the others
    /// agree with their timing; LRCLIB's own exact match only breaks ties.
    /// </summary>
    public async Task<List<Result>> CandidatesAsync(string artist, string title, string? album, TimeSpan? duration, CancellationToken ct = default)
    {
        var found = new List<(Result R, bool Exact)>();
        string url = $"https://lrclib.net/api/get?artist_name={Uri.EscapeDataString(artist)}&track_name={Uri.EscapeDataString(title)}";
        if (!string.IsNullOrWhiteSpace(album)) url += $"&album_name={Uri.EscapeDataString(album)}";
        if (duration is { } d) url += $"&duration={(int)Math.Round(d.TotalSeconds)}";
        var res = await http.GetAsync(Provider, url, TimeSpan.FromDays(60), ct: ct).ConfigureAwait(false);
        if (res.Ok)
        {
            using var doc = JsonDocument.Parse(res.Body);
            found.Add((Parse(doc.RootElement), true));
        }

        string search = $"https://lrclib.net/api/search?artist_name={Uri.EscapeDataString(artist)}&track_name={Uri.EscapeDataString(title)}";
        var sres = await http.GetAsync(Provider, search, TimeSpan.FromDays(30), ct: ct).ConfigureAwait(false);
        if (sres.Ok)
        {
            using var sdoc = JsonDocument.Parse(sres.Body);
            if (sdoc.RootElement.ValueKind == JsonValueKind.Array)
                foreach (var e in sdoc.RootElement.EnumerateArray())
                {
                    if (Normalize.Key(e.Str("trackName")) != Normalize.Key(title)) continue;
                    if (duration is { } dur && e.Double("duration") is { } ds && Math.Abs(ds - dur.TotalSeconds) > 3) continue;
                    var r = Parse(e);
                    if (!found.Any(f => f.R.Id == r.Id && r.Id != 0)) found.Add((r, false));
                }
        }
        return Rank(found);
    }

    private static List<Result> Rank(List<(Result R, bool Exact)> found)
    {
        var timings = found.Select(f => f.R.Synced is { } s ? Lrc.Parse(s.Content, s.Format) : null).ToList();
        var scored = found.Select((f, i) =>
        {
            double agreement = 0;
            if (timings[i] is { } mine)
                for (int j = 0; j < found.Count; j++)
                    if (j != i && timings[j] is { } other) agreement += TimingAgreement(mine, other);
            return (f.R, f.Exact, Agreement: agreement, Index: i);
        });
        return scored
            .OrderByDescending(x => x.R.Synced is not null)
            .ThenByDescending(x => Math.Round(x.Agreement, 1))
            .ThenByDescending(x => x.Exact)
            .ThenBy(x => x.Index)
            .Select(x => x.R)
            .ToList();
    }

    /// <summary>Fraction (0..1) of the lines two synced lyrics share whose timestamps are within a second.</summary>
    public static double TimingAgreement(IReadOnlyList<LyricLine> a, IReadOnlyList<LyricLine> b)
    {
        var byText = new Dictionary<string, TimeSpan>();
        foreach (var l in b)
            if (l.Time is { } t && Normalize.Key(l.Text) is { Length: > 0 } k) byText.TryAdd(k, t);
        int shared = 0, close = 0;
        var seen = new HashSet<string>();
        foreach (var l in a)
        {
            if (l.Time is not { } t || Normalize.Key(l.Text) is not { Length: > 0 } k || !seen.Add(k)) continue;
            if (!byText.TryGetValue(k, out var u)) continue;
            shared++;
            if (Math.Abs((t - u).TotalSeconds) <= 1.0) close++;
        }
        return shared == 0 ? 0 : (double)close / shared;
    }

    private static Result Parse(JsonElement e)
    {
        string? synced = e.Str("syncedLyrics");
        string? plain = e.Str("plainLyrics");
        var format = synced is not null && WordTag().IsMatch(synced) ? LyricsFormat.EnhancedLrc : LyricsFormat.Lrc;
        return new Result(
            synced is null ? null : new LyricsData(Provider, format, synced),
            plain is null ? null : new LyricsData(Provider, LyricsFormat.Plain, plain),
            e.Bool("instrumental"),
            e.Long("id") ?? 0);
    }

    [GeneratedRegex(@"<\d{1,2}:\d{2}(\.\d{1,3})?>")]
    private static partial Regex WordTag();
}

/// <summary>A sung word: when it starts (and ends, when the lyrics say), and where it sits in the line's text.</summary>
public sealed record LyricWord(TimeSpan Start, TimeSpan? End, int CharStart, int CharLength);

/// <summary>One lyric line with its start time (null for unsynced text) and optional per-word timings.</summary>
public sealed record LyricLine(TimeSpan? Time, string Text, IReadOnlyList<LyricWord> Words);

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
            var shift = TimeSpan.FromMilliseconds(offsetMs);
            var (text, words) = SplitWords(raw[(stamps[^1].Index + stamps[^1].Length)..], shift);
            // A line stamped more than once (a repeated chorus) is sung again later: move its words along with it.
            var first = Time(stamps[0]);
            foreach (Match s in stamps)
            {
                var delta = Time(s) - first;
                var moved = delta == TimeSpan.Zero ? words
                    : words.Select(w => w with { Start = w.Start + delta, End = w.End + delta }).ToList();
                lines.Add(new LyricLine(Time(s) - shift, text, moved));
            }
        }
        lines.Sort((a, b) => a.Time!.Value.CompareTo(b.Time!.Value));
        return lines;
    }

    /// <summary>
    /// Removes the word tags of an enhanced LRC line, keeping where each word ends up in the tidied text.
    /// A word ends where the next tag starts; a tag with nothing after it only marks the end of the word before.
    /// </summary>
    private static (string Text, List<LyricWord> Words) SplitWords(string text, TimeSpan shift)
    {
        var tags = WordTag().Matches(text);
        if (tags.Count == 0) return (Spaces().Replace(text, " ").Trim(), []);

        var sb = new System.Text.StringBuilder();
        var words = new List<LyricWord>();
        void Append(string piece)
        {
            piece = Spaces().Replace(piece, " ");
            if (sb.Length > 0 && sb[^1] == ' ' && piece.StartsWith(' ')) piece = piece[1..];
            sb.Append(piece);
        }
        Append(text[..tags[0].Index]);
        for (int i = 0; i < tags.Count; i++)
        {
            int from = tags[i].Index + tags[i].Length;
            int to = i + 1 < tags.Count ? tags[i + 1].Index : text.Length;
            string piece = text[from..to];
            if (piece.Trim().Length == 0) { Append(piece); continue; }
            Append(piece);
            string tidy = Spaces().Replace(piece, " ");
            int end = sb.Length - (tidy.Length - tidy.TrimEnd().Length);
            int length = tidy.Trim().Length;
            TimeSpan? until = i + 1 < tags.Count ? Time(tags[i + 1]) - shift : null;
            words.Add(new LyricWord(Time(tags[i]) - shift, until, end - length, length));
        }
        int lead = sb.Length - sb.ToString().TrimStart().Length;
        var result = sb.ToString().Trim();
        return (result, words.Select(w => w with { CharStart = w.CharStart - lead })
            .Where(w => w.CharStart >= 0 && w.CharStart + w.CharLength <= result.Length).ToList());
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
