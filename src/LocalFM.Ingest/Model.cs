namespace LocalFM.Ingest;

/// <summary>Whether an integration can be used right now, and what to tell the person about it.</summary>
public sealed record ProviderStatus(bool Available, string? Version = null, string? Location = null, string? Note = null);

/// <summary>One thing that can be imported: a video, an album track, a file in a zip.</summary>
public sealed record ImportCandidate(string ProviderId, string Input, string Title)
{
    public string? ExternalId { get; init; }
    public string? Url { get; init; }
    public string? Artist { get; init; }
    public string? AlbumArtist { get; init; }
    public string? Album { get; init; }
    public int? TrackNumber { get; init; }
    public string? Date { get; init; }
    public long? DurationMs { get; init; }
    public string? ThumbnailUrl { get; init; }
    /// <summary>Short licence label when the source states one ("CC BY").</summary>
    public string? License { get; init; }
    public bool AlreadyInLibrary { get; init; }

    /// <summary>Fills this candidate's gaps and refreshes known fields from richer data fetched later.</summary>
    public ImportCandidate Refine(ImportCandidate richer) => this with
    {
        Title = richer.Title is { Length: > 0 } t ? t : Title,
        ExternalId = richer.ExternalId ?? ExternalId,
        Url = richer.Url ?? Url,
        Artist = richer.Artist ?? Artist,
        AlbumArtist = richer.AlbumArtist ?? AlbumArtist,
        Album = richer.Album ?? Album,
        TrackNumber = richer.TrackNumber ?? TrackNumber,
        Date = richer.Date ?? Date,
        DurationMs = richer.DurationMs ?? DurationMs,
        ThumbnailUrl = richer.ThumbnailUrl ?? ThumbnailUrl,
        License = richer.License ?? License,
    };
}

/// <summary>What an input resolved to: one or more candidates, plus anything worth saying (warnings).</summary>
public sealed record ImportPreview(string Title, IReadOnlyList<ImportCandidate> Items, string? Note = null);

/// <summary>A fetched file in the job's staging folder, with what is now known about it.</summary>
public sealed record FetchedFile(string Path, ImportCandidate Info, string? CoverPath = null);

/// <summary>An integration that turns an input (a link, a zip) into audio files.</summary>
public interface IImportProvider
{
    string Id { get; }
    string Name { get; }

    Task<ProviderStatus> DetectAsync(CancellationToken ct = default);

    /// <summary>Cheap, offline check whether this input is for this provider.</summary>
    bool CanHandle(string input);

    Task<ImportPreview> ResolveAsync(string input, CancellationToken ct = default);

    /// <summary>Puts the audio for <paramref name="candidate"/> into <paramref name="stagingDir"/>. Progress is 0..1.</summary>
    Task<FetchedFile> FetchAsync(ImportCandidate candidate, string stagingDir, IProgress<double> progress, CancellationToken ct = default);
}

/// <summary>A failure worth showing as-is (missing tool, the site said no).</summary>
public sealed class ImportException(string message) : Exception(message);
