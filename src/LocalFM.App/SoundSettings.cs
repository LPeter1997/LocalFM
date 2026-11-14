using System.Globalization;
using LocalFM.Library;
using LocalFM.Native;

namespace LocalFM.App;

public sealed record EqPreset(string Name, float[] Gains, bool BuiltIn = false);

/// <summary>
/// Equalizer, crossfade and loudness settings, persisted in the library's key-value store.
/// The equalizer is a 10-band graphic EQ (ISO octave centres) whose outer bands are shelves,
/// so they double as bass and treble controls.
/// </summary>
public sealed class SoundSettings
{
    public static readonly float[] Frequencies = [31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000];
    public const float MaxGainDb = 12;

    public static readonly IReadOnlyList<EqPreset> BuiltIn =
    [
        new("Flat", [0, 0, 0, 0, 0, 0, 0, 0, 0, 0], true),
        new("Bass boost", [6, 5, 4, 2, 0, 0, 0, 0, 0, 0], true),
        new("Treble boost", [0, 0, 0, 0, 0, 0, 2, 4, 5, 6], true),
        new("Loudness", [5, 3, 0, 0, -1, 0, 0, 1, 3, 4], true),
        new("Rock", [4, 3, 2, 0, -1, -1, 0, 2, 3, 4], true),
        new("Pop", [-1, 0, 2, 3, 4, 3, 1, 0, -1, -1], true),
        new("Jazz", [3, 2, 1, 2, -1, -1, 0, 1, 2, 3], true),
        new("Classical", [3, 2, 1, 0, 0, 0, 0, 1, 2, 3], true),
        new("Electronic", [5, 4, 1, 0, -2, 1, 0, 1, 4, 5], true),
        new("Vocal", [-2, -2, -1, 0, 2, 4, 4, 2, 0, -1], true),
        new("Spoken word", [-4, -3, -1, 1, 3, 4, 4, 2, 0, -2], true),
    ];

    public bool EqEnabled { get; set; }
    public float[] Gains { get; set; } = new float[10];
    public float PreampDb { get; set; }
    /// <summary>Lowers the preamp by the largest boost so the EQ cannot clip.</summary>
    public bool PreventClipping { get; set; } = true;
    public string Preset { get; set; } = "Flat";
    public List<EqPreset> CustomPresets { get; set; } = [];

    public double CrossfadeSeconds { get; set; }
    /// <summary>Consecutive tracks of one album always play gaplessly, even with crossfade on.</summary>
    public bool GaplessAlbums { get; set; } = true;
    public ReplayGainMode ReplayGain { get; set; } = ReplayGainMode.Album;

    public IEnumerable<EqPreset> AllPresets => BuiltIn.Concat(CustomPresets);

    public float EffectivePreampDb => !EqEnabled ? 0 : PreampDb - (PreventClipping ? Math.Max(0, Gains.Max()) : 0);

    /// <summary>The filters to run; empty when the EQ is off.</summary>
    public EqBand[] Bands()
    {
        if (!EqEnabled) return [];
        var bands = new EqBand[Frequencies.Length];
        for (int i = 0; i < bands.Length; i++)
        {
            var type = i == 0 ? EqBandType.LowShelf : i == bands.Length - 1 ? EqBandType.HighShelf : EqBandType.Peak;
            // Shelves sit an octave inward of their label so the label frequency gets the full gain.
            float freq = type switch { EqBandType.LowShelf => Frequencies[i] * 2, EqBandType.HighShelf => Frequencies[i] / 2, _ => Frequencies[i] };
            bands[i] = new EqBand(type, freq, type == EqBandType.Peak ? 1.41f : 0.71f, Math.Clamp(Gains[i], -MaxGainDb, MaxGainDb));
        }
        return bands;
    }

    // ------------------------------------------------------------------ persistence

    private const string Key = "sound.settings";
    private const string PresetsKey = "sound.presets";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static SoundSettings Load(History kv)
    {
        var s = new SoundSettings();
        try
        {
            if (kv.Get(Key) is { } raw)
                foreach (var part in raw.Split(';'))
                {
                    int eq = part.IndexOf('=');
                    if (eq < 0) continue;
                    string k = part[..eq], v = part[(eq + 1)..];
                    switch (k)
                    {
                        case "eq": s.EqEnabled = v == "1"; break;
                        case "gains": s.Gains = ParseGains(v) ?? s.Gains; break;
                        case "pre": s.PreampDb = float.Parse(v, Inv); break;
                        case "noclip": s.PreventClipping = v == "1"; break;
                        case "preset": s.Preset = v; break;
                        case "xfade": s.CrossfadeSeconds = double.Parse(v, Inv); break;
                        case "gapless": s.GaplessAlbums = v == "1"; break;
                        case "rg": s.ReplayGain = Enum.TryParse<ReplayGainMode>(v, out var m) ? m : s.ReplayGain; break;
                    }
                }
            if (kv.Get(PresetsKey) is { } presets)
                foreach (var line in presets.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    int bar = line.LastIndexOf('|');
                    if (bar > 0 && ParseGains(line[(bar + 1)..]) is { } g) s.CustomPresets.Add(new EqPreset(line[..bar], g));
                }
        }
        catch (FormatException) { /* corrupt settings: keep defaults */ }
        return s;
    }

    public Task SaveAsync(History kv)
    {
        string settings = string.Join(";",
            $"eq={(EqEnabled ? 1 : 0)}", $"gains={FormatGains(Gains)}", $"pre={PreampDb.ToString(Inv)}",
            $"noclip={(PreventClipping ? 1 : 0)}", $"preset={Preset.Replace(";", "").Replace("=", "")}",
            $"xfade={CrossfadeSeconds.ToString(Inv)}", $"gapless={(GaplessAlbums ? 1 : 0)}", $"rg={ReplayGain}");
        string presets = string.Join("\n", CustomPresets.Select(p => $"{p.Name.Replace("\n", " ").Replace("|", "/")}|{FormatGains(p.Gains)}"));
        return Task.WhenAll(kv.SetAsync(Key, settings), kv.SetAsync(PresetsKey, presets));
    }

    private static string FormatGains(float[] g) => string.Join(",", g.Select(x => x.ToString("0.#", Inv)));

    private static float[]? ParseGains(string v)
    {
        var parts = v.Split(',');
        if (parts.Length != Frequencies.Length) return null;
        var g = new float[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            if (!float.TryParse(parts[i], NumberStyles.Float, Inv, out g[i])) return null;
        return g;
    }

    // ------------------------------------------------------------------ response curve (for drawing)

    /// <summary>The EQ's gain in dB at <paramref name="freq"/> (what the curve in the UI shows).</summary>
    public double ResponseDb(double freq, double sampleRate = 48000)
    {
        double db = EffectivePreampDb;
        foreach (var b in Bands())
        {
            if (Math.Abs(b.GainDb) < 0.01) continue;
            var (b0, b1, b2, a1, a2) = Design(b, sampleRate);
            double w = 2 * Math.PI * freq / sampleRate;
            // |H(e^jw)|² for a normalized biquad
            double cw = Math.Cos(w), c2w = Math.Cos(2 * w);
            double num = b0 * b0 + b1 * b1 + b2 * b2 + 2 * (b0 * b1 + b1 * b2) * cw + 2 * b0 * b2 * c2w;
            double den = 1 + a1 * a1 + a2 * a2 + 2 * (a1 + a1 * a2) * cw + 2 * a2 * c2w;
            db += 10 * Math.Log10(Math.Max(num, 1e-12) / Math.Max(den, 1e-12));
        }
        return db;
    }

    /// <summary>Same RBJ cookbook design as the native output (native/localfm_native/output.c).</summary>
    private static (double, double, double, double, double) Design(EqBand band, double fs)
    {
        double f = Math.Clamp(band.Frequency, 10, fs * 0.45), q = Math.Max(band.Q, 0.05);
        double A = Math.Pow(10, band.GainDb / 40), w0 = 2 * Math.PI * f / fs, cw = Math.Cos(w0), alpha = Math.Sin(w0) / (2 * q), sA = Math.Sqrt(A);
        double b0, b1, b2, a0, a1, a2;
        switch (band.Type)
        {
            case EqBandType.LowShelf:
                b0 = A * ((A + 1) - (A - 1) * cw + 2 * sA * alpha); b1 = 2 * A * ((A - 1) - (A + 1) * cw);
                b2 = A * ((A + 1) - (A - 1) * cw - 2 * sA * alpha); a0 = (A + 1) + (A - 1) * cw + 2 * sA * alpha;
                a1 = -2 * ((A - 1) + (A + 1) * cw); a2 = (A + 1) + (A - 1) * cw - 2 * sA * alpha;
                break;
            case EqBandType.HighShelf:
                b0 = A * ((A + 1) + (A - 1) * cw + 2 * sA * alpha); b1 = -2 * A * ((A - 1) + (A + 1) * cw);
                b2 = A * ((A + 1) + (A - 1) * cw - 2 * sA * alpha); a0 = (A + 1) - (A - 1) * cw + 2 * sA * alpha;
                a1 = 2 * ((A - 1) - (A + 1) * cw); a2 = (A + 1) - (A - 1) * cw - 2 * sA * alpha;
                break;
            default:
                b0 = 1 + alpha * A; b1 = -2 * cw; b2 = 1 - alpha * A; a0 = 1 + alpha / A; a1 = -2 * cw; a2 = 1 - alpha / A;
                break;
        }
        return (b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);
    }
}
