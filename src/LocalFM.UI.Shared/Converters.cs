using Avalonia.Data.Converters;

namespace LocalFM.UI;

public static class Converters
{
    public static string Time(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    public static readonly IValueConverter Duration =
        new FuncValueConverter<long?, string>(ms => ms is { } v ? Time(TimeSpan.FromMilliseconds(v)) : "");

    public static readonly IValueConverter Year =
        new FuncValueConverter<string?, string>(d => d is { Length: >= 4 } ? d[..4] : "");

    /// <summary>"1 track", "12 tracks".</summary>
    public static string Count(long n, string noun) => n == 1 ? $"1 {noun}" : $"{n:N0} {noun}s";

    public static readonly IValueConverter Tracks = new FuncValueConverter<int, string>(n => Count(n, "track"));

    public static readonly IValueConverter ArtistCounts =
        new FuncValueConverter<LocalFM.Library.ArtistSummary?, string>(a => a is null ? "" : $"{Count(a.AlbumCount, "album")} · {Count(a.TrackCount, "track")}");

    /// <summary>"1998 · Remaster · 12 tracks": only the parts that are known, so no stray separators.</summary>
    public static readonly IValueConverter AlbumFacts =
        new FuncValueConverter<LocalFM.Library.AlbumSummary?, string>(a => a is null ? "" : string.Join("  ·  ",
            new[] { a.Date is { Length: >= 4 } d ? d[..4] : null, a.Edition, Count(a.TrackCount, "track") }.Where(x => !string.IsNullOrWhiteSpace(x))));

    public static readonly IValueConverter IsZero = new FuncValueConverter<int, bool>(n => n == 0);

    public static readonly IValueConverter Positive = new FuncValueConverter<int, bool>(n => n > 0);

    public static readonly IValueConverter NotNull =
        new FuncValueConverter<object?, bool>(o => o is not null);
}
