namespace LocalFM.Library.Enrich;

// The common, provider-independent shape of external release data. Providers (MusicBrainz, Discogs, …)
// translate their responses into these records; MetadataApplier writes them into the knowledge base.
// Ids are the provider's own ids; Provider says whose.

public sealed record ArtistRef(
    string Name,
    string? Id = null,
    string? SortName = null,
    string? Type = null,
    string? Country = null,
    string? Disambiguation = null);

public sealed record CreditPart(ArtistRef Artist, string CreditedName, string JoinPhrase);

public sealed record RecordingData(
    string? Id,
    string Title,
    long? LengthMs,
    IReadOnlyList<CreditPart> Credit,
    IReadOnlyList<string> Isrcs,
    IReadOnlyList<string> Genres,
    string? FirstReleaseDate);

public sealed record TrackData(
    int Position,
    string? Number,
    string Title,
    long? LengthMs,
    string? Id,
    IReadOnlyList<CreditPart> Credit,
    RecordingData Recording);

public sealed record MediumData(int Position, string? Format, string? Title, IReadOnlyList<TrackData> Tracks);

public sealed record ReleaseGroupData(
    string? Id,
    string Title,
    string? PrimaryType,
    IReadOnlyList<string> SecondaryTypes,
    string? FirstReleaseDate,
    IReadOnlyList<CreditPart> Credit,
    IReadOnlyList<string> Genres);

public sealed record ReleaseData(
    string Provider,
    string Id,
    string Title,
    IReadOnlyList<CreditPart> Credit,
    string? Date,
    string? Country,
    string? Label,
    string? CatalogNumber,
    string? Barcode,
    string? Status,
    string? Disambiguation,
    ReleaseGroupData Group,
    IReadOnlyList<MediumData> Media,
    IReadOnlyList<string> Genres)
{
    public int TrackCount => Media.Sum(m => m.Tracks.Count);

    public static string CreditText(IReadOnlyList<CreditPart> credit) =>
        string.Concat(credit.Select(c => c.CreditedName + c.JoinPhrase));
}

/// <summary>A search hit, enough to let a person pick the right edition.</summary>
public sealed record ReleaseCandidate(
    string Provider,
    string Id,
    string Title,
    string Artist,
    string? Date,
    string? Country,
    string? Label,
    string? CatalogNumber,
    string? Barcode,
    string? Format,
    int TrackCount,
    string? Status,
    string? Disambiguation,
    string? ReleaseGroupId,
    string? ReleaseGroupType,
    double Score);

public enum LyricsFormat { Plain, Lrc, EnhancedLrc }

public sealed record LyricsData(string Provider, LyricsFormat Format, string Content, string? Language = null);
