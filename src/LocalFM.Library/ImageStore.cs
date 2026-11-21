using System.IO.Hashing;
using LocalFM.Core;

namespace LocalFM.Library;

/// <summary>Content-addressed image files under &lt;data&gt;/images, indexed by the image table.</summary>
public sealed class ImageStore(string root)
{
    public string Root { get; } = root;

    /// <summary>
    /// SQL ORDER BY for picking an album's front cover among several (alias <c>ei</c>): one chosen by hand, then the
    /// one that came with metadata applied online, then a folder image, then art embedded in the files (for video
    /// downloads that is a thumbnail). Ties go to the newest.
    /// </summary>
    public const string FrontCoverOrder =
        "CASE ei.source WHEN 'user' THEN 0 WHEN 'folder' THEN 2 WHEN 'embedded' THEN 3 ELSE 1 END, ei.image_id DESC";

    /// <summary>Sources a cover is not downloaded from: replacing an online cover leaves these alone.</summary>
    public const string LocalCoverSources = "('user','folder','embedded')";

    public string FullPath(string relPath) => Path.Combine(Root, relPath);

    /// <summary>Writes the bytes (if new) and returns the image id. Must run on the writer connection.</summary>
    public Ulid Store(Tx tx, ReadOnlySpan<byte> data, string mime)
    {
        string hash = Convert.ToHexStringLower(XxHash128.Hash(data));
        var existing = tx.Single("SELECT id FROM image WHERE hash = $1", r => r.IdN(0), hash);
        if (existing is { } id) return id;

        string ext = mime switch
        {
            "image/png" => "png",
            "image/webp" => "webp",
            "image/gif" => "gif",
            "image/bmp" => "bmp",
            _ => "jpg",
        };
        string rel = Path.Combine(hash[..2], hash + "." + ext);
        string full = FullPath(rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        if (!File.Exists(full))
        {
            string tmp = full + ".tmp";
            File.WriteAllBytes(tmp, data.ToArray());
            File.Move(tmp, full, overwrite: true);
        }
        var newId = Ulid.NewUlid();
        tx.Exec("INSERT INTO image(id, hash, mime, rel_path) VALUES ($1,$2,$3,$4)", newId, hash, mime, rel);
        return newId;
    }
}
