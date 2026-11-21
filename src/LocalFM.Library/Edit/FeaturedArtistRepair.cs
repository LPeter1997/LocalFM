using LocalFM.Core;
using LocalFM.Library.Import;

namespace LocalFM.Library.Edit;

/// <summary>
/// Undoes what older imports did with featured artists: "Slash, Chris Cornell" as one artist on "Promise (feat. Chris
/// Cornell)", with an album of its own. Such songs are credited to the lead artist again and their albums, split per
/// guest, become one. Songs and albums with any curated field (edited by hand, matched online) are left alone.
/// Idempotent and cheap when there is nothing to do, so it can run at every start.
/// </summary>
public static class FeaturedArtistRepair
{
    private const string Source = "tags";

    /// <summary>Returns how many songs were re-credited.</summary>
    public static int Run(Tx tx)
    {
        long now = Sql.Now();
        // Single-name credits only: a credit built from separate artists (ARTISTS tags, MusicBrainz) is deliberate.
        var rows = tx.Query("""
            SELECT t.id, t.title, t.artist_credit_id, ac.display_name, t.recording_id,
                   r.id, r.artist_credit_id, r.title_key, r.date, r.release_group_id, r.created_at
            FROM track t
            JOIN artist_credit ac ON ac.id = t.artist_credit_id
            JOIN medium m ON m.id = t.medium_id
            JOIN release r ON r.id = m.release_id
            WHERE (SELECT COUNT(*) FROM artist_credit_name n WHERE n.credit_id = ac.id) = 1
              AND NOT EXISTS (SELECT 1 FROM field_provenance p WHERE p.entity_type = 'track' AND p.entity_id = t.id)
              AND NOT EXISTS (SELECT 1 FROM external_id e WHERE e.entity_type = 'track' AND e.entity_id = t.id AND e.provider = 'musicbrainz')
            ORDER BY r.created_at, t.position
            """, r => (Track: r.Id(0), Title: r.GetString(1), Credit: r.Id(2), Artist: r.GetString(3), Recording: r.Id(4),
                Release: r.Id(5), ReleaseCredit: r.IdN(6), AlbumKey: r.GetString(7), Date: r.Str(8), Group: r.Id(9)))
            .Select(x => (Row: x, Lead: Cleanup.WithoutFeatured(x.Artist, x.Title)))
            .Where(x => x.Lead is not null)
            .ToList();
        if (rows.Count == 0) return 0;

        int fixedCount = 0;
        foreach (var album in rows.GroupBy(x => x.Row.Release))
        {
            var first = album.First().Row;
            var leads = album.Select(x => Normalize.Key(x.Lead)).Distinct().ToList();
            Ulid lead = Importer.ResolveCredit(tx, album.First().Lead!, [], [], now);

            foreach (var (row, l) in album)
            {
                Ulid credit = Normalize.Key(l) == Normalize.Key(album.First().Lead) ? lead : Importer.ResolveCredit(tx, l!, [], [], now);
                tx.Exec("UPDATE track SET artist_credit_id = $2, updated_at = $3 WHERE id = $1", row.Track, credit, now);
                tx.Exec("""
                    UPDATE recording SET artist_credit_id = $3, updated_at = $4 WHERE id = $1 AND artist_credit_id = $2
                      AND NOT EXISTS (SELECT 1 FROM field_provenance p WHERE p.entity_type = 'recording' AND p.entity_id = $1 AND p.field = 'artist_credit_id')
                    """, row.Recording, row.Credit, credit, now);
                fixedCount++;
            }

            // The album took its artist from these songs (no album-artist tag): it is the lead's album, possibly one
            // that another guest's song already started. Only when every song on it agrees, and nothing is curated.
            bool albumFromSongs = first.ReleaseCredit == first.Credit && leads.Count == 1
                && tx.Long("SELECT COUNT(*) FROM track t JOIN medium m ON m.id = t.medium_id WHERE m.release_id = $1", first.Release) == album.Count()
                && tx.Long("SELECT COUNT(*) FROM field_provenance WHERE entity_type IN ('release','release_group') AND entity_id IN ($1,$2)", first.Release, first.Group) == 0
                && tx.Long("SELECT COUNT(*) FROM external_id WHERE entity_type = 'release' AND entity_id = $1 AND provider = 'musicbrainz'", first.Release) == 0;
            if (!albumFromSongs) continue;

            string? year = Year(first.Date);
            var into = tx.Query("""
                    SELECT id, date FROM release WHERE title_key = $1 AND artist_credit_id = $2 AND id <> $3 ORDER BY created_at
                    """, r => (Id: r.Id(0), Date: r.Str(1)), first.AlbumKey, lead, first.Release)
                .FirstOrDefault(c => year is null || Year(c.Date) is not { } cy || cy == year);
            if (into.Id != default)
            {
                MetadataEditor.MoveTracks(tx, album.Select(x => x.Row.Track).ToList(), into.Id, Source, lockIt: false);
                Log.Info("Repair", $"Joined “{album.First().Row.Title}” to its album (featured artist split it off)");
            }
            else
            {
                tx.Exec("UPDATE release SET artist_credit_id = $2, updated_at = $3 WHERE id = $1", first.Release, lead, now);
                tx.Exec("UPDATE release_group SET artist_credit_id = $3, updated_at = $4 WHERE id = $1 AND artist_credit_id = $2",
                    first.Group, first.Credit, lead, now);
            }
        }
        MetadataEditor.PruneOrphans(tx);
        Log.Info("Repair", $"Credited {fixedCount} song(s) with featured artists to their lead artist");
        return fixedCount;
    }

    private static string? Year(string? date) => date is { Length: >= 4 } d ? d[..4] : null;
}
