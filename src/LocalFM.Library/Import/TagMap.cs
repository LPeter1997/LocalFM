using System.Globalization;
using System.Text;

namespace LocalFM.Library.Import;

/// <summary>
/// Tags of one file, normalized across ID3/Vorbis/MP4/APE naming. Values keep their original text;
/// multi-valued fields are split. Unknown tags stay available in <see cref="Raw"/>.
/// </summary>
public sealed record FileTags
{
    public string? Title { get; init; }
    public string? Artist { get; init; }
    public IReadOnlyList<string> Artists { get; init; } = [];
    public string? AlbumArtist { get; init; }
    public string? Album { get; init; }
    public string? Date { get; init; }
    public string? OriginalDate { get; init; }
    public int? TrackNumber { get; init; }
    public int? TrackTotal { get; init; }
    public int? DiscNumber { get; init; }
    public int? DiscTotal { get; init; }
    public string? DiscSubtitle { get; init; }
    public IReadOnlyList<string> Genres { get; init; } = [];
    public string? Composer { get; init; }
    public string? Label { get; init; }
    public string? CatalogNumber { get; init; }
    public string? Barcode { get; init; }
    public string? Isrc { get; init; }
    public string? Country { get; init; }
    public string? ReleaseType { get; init; }
    public string? ReleaseStatus { get; init; }
    public string? Media { get; init; }
    public bool Compilation { get; init; }
    public string? Lyrics { get; init; }
    public string? Bpm { get; init; }
    public string? MusicalKey { get; init; }

    public string? MbRecordingId { get; init; }
    public string? MbTrackId { get; init; }
    public string? MbReleaseId { get; init; }
    public string? MbReleaseGroupId { get; init; }
    public IReadOnlyList<string> MbArtistIds { get; init; } = [];
    public IReadOnlyList<string> MbAlbumArtistIds { get; init; } = [];
    public string? MbWorkId { get; init; }

    public double? TrackGainDb { get; init; }
    public double? TrackPeak { get; init; }
    public double? AlbumGainDb { get; init; }
    public double? AlbumPeak { get; init; }

    public IReadOnlyDictionary<string, string> Raw { get; init; } = new Dictionary<string, string>();

    /// <summary>Parses raw FFmpeg tag pairs. Keys are matched case- and punctuation-insensitively.</summary>
    public static FileTags Parse(IEnumerable<KeyValuePair<string, string>> tags)
    {
        var raw = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var norm = new Dictionary<string, string>();
        foreach (var (k, v) in tags)
        {
            if (string.IsNullOrWhiteSpace(v)) continue;
            raw.TryAdd(k, v);
            string nk = NormKey(k);
            // lyrics-eng, lyrics-XXX (ID3 USLT with language) → lyrics
            if (nk.StartsWith("lyrics", StringComparison.Ordinal)) nk = "lyrics";
            norm.TryAdd(nk, v.Trim());
        }

        string? Get(params string[] keys)
        {
            foreach (var k in keys)
                if (norm.TryGetValue(k, out var v) && v.Length > 0) return v;
            return null;
        }

        var (track, trackTotal) = SplitNumber(Get("track", "tracknumber", "trck"));
        var (disc, discTotal) = SplitNumber(Get("disc", "discnumber", "tpos", "disk"));
        string? artist = Get("artist", "tpe1");

        return new FileTags
        {
            Title = Get("title", "tit2"),
            Artist = artist,
            Artists = SplitMulti(Get("artists")),
            AlbumArtist = Get("albumartist", "tpe2", "band"),
            Album = Get("album", "talb"),
            Date = Get("date", "year", "tdrc", "tyer", "releasedate"),
            OriginalDate = Get("originaldate", "originalyear", "tdor", "tory"),
            TrackNumber = track,
            TrackTotal = trackTotal ?? ParseInt(Get("tracktotal", "totaltracks")),
            DiscNumber = disc,
            DiscTotal = discTotal ?? ParseInt(Get("disctotal", "totaldiscs")),
            DiscSubtitle = Get("discsubtitle", "tsst", "setsubtitle"),
            Genres = SplitMulti(Get("genre", "tcon")),
            Composer = Get("composer", "tcom"),
            Label = Get("label", "publisher", "organization", "tpub"),
            CatalogNumber = Get("catalognumber", "catalog"),
            Barcode = Get("barcode", "upc", "ean"),
            Isrc = Get("isrc", "tsrc"),
            Country = Get("releasecountry", "musicbrainzalbumreleasecountry"),
            ReleaseType = Get("releasetype", "musicbrainzalbumtype"),
            ReleaseStatus = Get("releasestatus", "musicbrainzalbumstatus"),
            Media = Get("media", "tmed"),
            Compilation = Get("compilation", "tcmp", "cpil") is "1" or "true",
            Lyrics = Get("lyrics", "unsyncedlyrics", "uslt"),
            Bpm = Get("bpm", "tbpm", "tmpo"),
            MusicalKey = Get("initialkey", "key", "tkey"),

            MbRecordingId = Get("musicbrainztrackid", "musicbrainzrecordingid"),
            MbTrackId = Get("musicbrainzreleasetrackid"),
            MbReleaseId = Get("musicbrainzalbumid", "musicbrainzreleaseid"),
            MbReleaseGroupId = Get("musicbrainzreleasegroupid"),
            MbArtistIds = SplitMulti(Get("musicbrainzartistid"), splitSlash: true),
            MbAlbumArtistIds = SplitMulti(Get("musicbrainzalbumartistid"), splitSlash: true),
            MbWorkId = Get("musicbrainzworkid"),

            TrackGainDb = ParseGain(Get("replaygaintrackgain")),
            TrackPeak = ParseDouble(Get("replaygaintrackpeak")),
            AlbumGainDb = ParseGain(Get("replaygainalbumgain")),
            AlbumPeak = ParseDouble(Get("replaygainalbumpeak")),

            Raw = raw,
        };
    }

    /// <summary>Lowercase, letters and digits only: "MusicBrainz Album Id" → "musicbrainzalbumid".</summary>
    public static string NormKey(string key)
    {
        var sb = new StringBuilder(key.Length);
        foreach (char c in key)
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        // MP4 atoms come through as "©nam" etc. in some builds; FFmpeg maps them, but be lenient.
        return sb.ToString();
    }

    private static IReadOnlyList<string> SplitMulti(string? v, bool splitSlash = false)
    {
        if (string.IsNullOrWhiteSpace(v)) return [];
        char[] seps = splitSlash ? [';', '\0', '/'] : [';', '\0'];
        return v.Split(seps, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static (int?, int?) SplitNumber(string? v)
    {
        if (v is null) return (null, null);
        var parts = v.Split('/', 2);
        return (ParseInt(parts[0]), parts.Length > 1 ? ParseInt(parts[1]) : null);
    }

    private static int? ParseInt(string? v)
    {
        if (v is null) return null;
        v = v.Trim();
        int end = 0;
        while (end < v.Length && char.IsDigit(v[end])) end++;
        return end > 0 && int.TryParse(v.AsSpan(0, end), NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : null;
    }

    private static double? ParseDouble(string? v) =>
        v is not null && double.TryParse(v.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : null;

    private static double? ParseGain(string? v) =>
        v is null ? null : ParseDouble(v.Replace("dB", "", StringComparison.OrdinalIgnoreCase).Trim());
}
