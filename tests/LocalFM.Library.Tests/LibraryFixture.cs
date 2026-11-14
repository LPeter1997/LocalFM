using System.Diagnostics;
using LocalFM.Library;
using LocalFM.Library.Import;

namespace LocalFM.Library.Tests;

/// <summary>A throwaway library (database + image store + music folder) per test.</summary>
public sealed class TempLibrary : IDisposable
{
    public TempLibrary()
    {
        Root = Path.Combine(Path.GetTempPath(), "localfm-tests", "lib-" + Guid.NewGuid().ToString("N")[..8]);
        Music = Path.Combine(Root, "music");
        Directory.CreateDirectory(Music);
        Db = new Database(Path.Combine(Root, "library.db"));
        Images = new ImageStore(Path.Combine(Root, "images"));
        Scanner = new Scanner(Db, Images);
        Queries = new LibraryQueries(Db, Images);
    }

    public string Root { get; }
    public string Music { get; }
    public Database Db { get; }
    public ImageStore Images { get; }
    public Scanner Scanner { get; }
    public LibraryQueries Queries { get; }

    public async Task ScanAsync()
    {
        var src = await Sources.AddFolderAsync(Db, Music);
        await Scanner.ScanAsync(src);
    }

    public static bool HaveFfmpeg { get; } = Probe();
    private static bool Probe() { try { Ffmpeg("-version"); return true; } catch { return false; } }

    public static void Ffmpeg(string args)
    {
        var psi = new ProcessStartInfo("ffmpeg", "-hide_banner -loglevel error -y " + args) { RedirectStandardError = true, UseShellExecute = false };
        using var p = Process.Start(psi)!;
        string err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"ffmpeg {args}: {err}");
    }

    /// <summary>Creates a short tagged audio file. Each call gets a distinct tone so content hashes differ.</summary>
    public string MakeTrack(string relPath, Dictionary<string, string> tags, double seconds = 1.0, string? coverPng = null)
    {
        string path = Path.Combine(Music, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        int freq = 200 + Math.Abs(relPath.GetHashCode() % 2000);
        string meta = string.Join(" ", tags.Select(kv => $"-metadata \"{kv.Key}={kv.Value}\""));
        string input = $"-f lavfi -i \"sine=frequency={freq}:duration={seconds}\"";
        if (coverPng is not null)
            Ffmpeg($"{input} -i \"{coverPng}\" -map 0:a -map 1:v -c:v copy -disposition:v attached_pic -ac 2 {meta} \"{path}\"");
        else
            Ffmpeg($"{input} -ac 2 {meta} \"{path}\"");
        return path;
    }

    public string MakePng()
    {
        string png = Path.Combine(Root, "cover.png");
        if (!File.Exists(png)) Ffmpeg($"-f lavfi -i \"color=c=red:s=64x64:d=1\" -frames:v 1 \"{png}\"");
        return png;
    }

    public void Dispose()
    {
        Db.Dispose();
        try { Directory.Delete(Root, true); } catch { }
    }
}
