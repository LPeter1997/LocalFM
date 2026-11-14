using System.IO.Hashing;
using LocalFM.Native;

namespace LocalFM.Library.Import;

/// <summary>
/// Content identity of an audio file: xxHash128 over the audio stream's packet payloads only.
/// Editing tags or cover art does not change it; moving/renaming the file does not change it.
/// </summary>
public static class AudioIdentity
{
    public static string Compute(MediaProbe probe)
    {
        var h = new XxHash128();
        probe.ReadPackets(p => h.Append(p));
        return Convert.ToHexStringLower(h.GetCurrentHash());
    }

    public static string HashTags(IEnumerable<KeyValuePair<string, string>> tags, int pictureSize)
    {
        var h = new XxHash128();
        foreach (var (k, v) in tags.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            h.Append(System.Text.Encoding.UTF8.GetBytes(k));
            h.Append([0]);
            h.Append(System.Text.Encoding.UTF8.GetBytes(v));
            h.Append([0]);
        }
        h.Append(BitConverter.GetBytes(pictureSize));
        return Convert.ToHexStringLower(h.GetCurrentHash());
    }
}
