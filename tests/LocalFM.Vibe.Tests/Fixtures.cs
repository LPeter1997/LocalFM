using System.Diagnostics;
using System.Globalization;
using LocalFM.Vibe.Client;

namespace LocalFM.Vibe.Tests;

/// <summary>Audio fixtures made with the ffmpeg CLI (tests skip without it).</summary>
public static class Fixtures
{
    public static readonly string Dir = Path.Combine(Path.GetTempPath(), "localfm-tests", "vibe");

    public static bool HaveFfmpeg { get; } = Probe();

    private static bool Probe()
    {
        try { Run("-version"); return true; } catch { return false; }
    }

    public static void Run(string args)
    {
        var psi = new ProcessStartInfo("ffmpeg", "-hide_banner -loglevel error -y " + args)
        {
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var p = Process.Start(psi)!;
        string err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"ffmpeg {args} failed: {err}");
    }

    /// <summary>A stereo sweep (so positions are distinguishable) in the given format.</summary>
    public static string Sweep(string name, string ext, double seconds, string codec = "")
    {
        Directory.CreateDirectory(Dir);
        string file = Path.Combine(Dir, $"{name}.{ext}");
        if (!File.Exists(file))
            Run($"-f lavfi -i \"aevalsrc='0.2*sin(2*PI*(200+100*t)*t)':s=44100:d={seconds.ToString(CultureInfo.InvariantCulture)}\" -ac 2 {codec} \"{file}\"");
        return file;
    }

    /// <summary>Splits one continuous sine into consecutive files at exact sample positions.</summary>
    public static string[] SplitSine(string name, string ext, int rate, params double[] seconds)
    {
        Directory.CreateDirectory(Dir);
        var files = new string[seconds.Length];
        long start = 0;
        for (int i = 0; i < seconds.Length; i++)
        {
            long len = (long)(seconds[i] * rate);
            files[i] = Path.Combine(Dir, $"{name}-{i}.{ext}");
            if (!File.Exists(files[i]))
                Run($"-f lavfi -i \"sine=frequency=440:sample_rate={rate}:duration=60\" -ac 2 " +
                    $"-af \"atrim=start_sample={start}:end_sample={start + len},asetpts=N/SR/TB\" \"{files[i]}\"");
            start += len;
        }
        return files;
    }
}

/// <summary>Serves local files as if from a host, optionally slowly; records what was asked for.</summary>
public sealed class FakeHost : IRangeSource
{
    private readonly Dictionary<string, byte[]> _files = new();
    public TimeSpan Delay { get; set; }
    public List<(string Id, long Offset)> Requests { get; } = new();
    public bool Fail { get; set; }

    public string Add(string id, string path)
    {
        _files[id] = File.ReadAllBytes(path);
        return id;
    }

    public long Length(string id) => _files[id].Length;

    public async ValueTask<int> ReadAsync(string mediaId, long offset, Memory<byte> buffer, CancellationToken ct)
    {
        lock (Requests) Requests.Add((mediaId, offset));
        if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
        if (Fail) throw new IOException("connection lost");
        var data = _files[mediaId];
        int n = (int)Math.Min(buffer.Length, data.Length - offset);
        data.AsMemory((int)offset, n).CopyTo(buffer);
        return n;
    }
}
