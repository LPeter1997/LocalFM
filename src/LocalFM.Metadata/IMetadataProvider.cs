using LocalFM.Core;

namespace LocalFM.Metadata;

/// <summary>What we know locally when asking a provider about something.</summary>
public sealed record LookupQuery(
    string EntityType,              // recording, release, artist…
    Ulid EntityId,
    string? Title,
    string? Artist,
    string? Album,
    long? DurationMs,
    IReadOnlyDictionary<string, string> ExternalIds);

/// <summary>
/// One provider's proposal, already normalized to the knowledge-base shape. Field names match KB columns
/// (e.g. "title", "date", "label") or attribute keys; images and external ids are carried alongside.
/// The user picks or merges candidates; locked fields are never overwritten.
/// </summary>
public sealed record Candidate(
    string Provider,
    double Confidence,
    IReadOnlyDictionary<string, string> Fields,
    IReadOnlyDictionary<string, string> ExternalIds,
    IReadOnlyList<Uri> Images);

/// <summary>
/// A metadata source (MusicBrainz, Cover Art Archive, iTunes, Discogs, Wikidata, LRCLIB…).
/// Implementations must go through the shared HTTP cache (table http_cache) and respect each service's rate limit.
/// Next phase; this round only fixes the contract.
/// </summary>
public interface IMetadataProvider
{
    string Name { get; }
    IReadOnlySet<string> SupportedEntityTypes { get; }
    Task<IReadOnlyList<Candidate>> LookupAsync(LookupQuery query, CancellationToken ct);
}
