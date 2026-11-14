using System.IO.Hashing;
using LocalFM.Core;

namespace LocalFM.Library;

/// <summary>Content-addressed image files under &lt;data&gt;/images, indexed by the image table.</summary>
public sealed class ImageStore(string root)
{
    public string Root { get; } = root;

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
