using System.Diagnostics;

namespace LocalFM.Playback.Tests;

/// <summary>Generates audio fixtures with the ffmpeg CLI (tests are skipped when it is missing).</summary>
public static class Fixtures
{
    public static readonly string Dir = Path.Combine(Path.GetTempPath(), "localfm-tests", "audio");

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

    /// <summary>Splits one continuous 440 Hz sine into consecutive files at exact sample positions.</summary>
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
            {
                string codec = ext == "mp3" ? "-c:a libmp3lame -b:a 192k" : "";
                Run($"-f lavfi -i \"sine=frequency=440:sample_rate={rate}:duration=60\" -ac 2 " +
                    $"-af \"atrim=start_sample={start}:end_sample={start + len},asetpts=N/SR/TB\" {codec} \"{files[i]}\"");
            }
            start += len;
        }
        return files;
    }

    /// <summary>A stereo sine of its own frequency (tracks that must not cancel each other when mixed).</summary>
    public static string Sine(string name, int frequency, int rate, double seconds)
    {
        Directory.CreateDirectory(Dir);
        string file = Path.Combine(Dir, $"{name}.flac");
        if (!File.Exists(file))
            Run($"-f lavfi -i \"sine=frequency={frequency}:sample_rate={rate}:duration={seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}\" -ac 2 \"{file}\"");
        return file;
    }
}
