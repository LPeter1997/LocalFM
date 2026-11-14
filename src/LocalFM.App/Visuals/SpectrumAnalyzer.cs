namespace LocalFM.App.Visuals;

/// <summary>
/// Turns the audio just heard into bar heights (0..1) on a logarithmic frequency axis, with quick attack,
/// slow fall and falling peak caps, like a hardware analyzer. Allocation-free after construction;
/// meant to be called once per frame from the UI thread.
/// </summary>
public sealed class SpectrumAnalyzer
{
    public const int FftSize = 4096;

    private readonly double[] _re = new double[FftSize];
    private readonly double[] _im = new double[FftSize];
    private readonly double[] _window = new double[FftSize];
    private readonly int[] _bandLo, _bandHi;
    private readonly double[] _centreBin;   // fractional bin at the band's centre (narrow bands interpolate there)
    private readonly double[] _mag = new double[FftSize / 2 + 1];
    private readonly float[] _levels, _peaks, _peakHold;

    public SpectrumAnalyzer(int bands = 48, int sampleRate = 48000, double minHz = 35, double maxHz = 16000)
    {
        Bands = bands;
        SampleRate = sampleRate;
        _levels = new float[bands];
        _peaks = new float[bands];
        _peakHold = new float[bands];
        for (int i = 0; i < FftSize; i++) _window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (FftSize - 1)); // Hann
        _bandLo = new int[bands];
        _bandHi = new int[bands];
        _centreBin = new double[bands];
        maxHz = Math.Min(maxHz, sampleRate / 2.0 - 1);
        double binHz = (double)sampleRate / FftSize;
        for (int b = 0; b < bands; b++)
        {
            double f0 = minHz * Math.Pow(maxHz / minHz, (double)b / bands);
            double f1 = minHz * Math.Pow(maxHz / minHz, (double)(b + 1) / bands);
            _bandLo[b] = Math.Clamp((int)Math.Floor(f0 / binHz), 1, FftSize / 2 - 1);
            _bandHi[b] = Math.Clamp((int)Math.Ceiling(f1 / binHz), _bandLo[b] + 1, FftSize / 2);
            _centreBin[b] = Math.Sqrt(f0 * f1) / binHz;
        }
    }

    public int Bands { get; }
    public int SampleRate { get; }
    public ReadOnlySpan<float> Levels => _levels;
    public ReadOnlySpan<float> Peaks => _peaks;

    /// <summary>Feeds the latest <see cref="FftSize"/> stereo frames; <paramref name="dt"/> is the time since the last call.</summary>
    public void Update(ReadOnlySpan<float> interleavedStereo, double dt)
    {
        int frames = Math.Min(FftSize, interleavedStereo.Length / 2);
        int offset = FftSize - frames;
        for (int i = 0; i < FftSize; i++)
        {
            double s = i < offset ? 0 : 0.5 * (interleavedStereo[(i - offset) * 2] + interleavedStereo[(i - offset) * 2 + 1]);
            _re[i] = s * _window[i];
            _im[i] = 0;
        }
        Fft(_re, _im);

        // Normalised so a full-scale sine reads 0 dB.
        double norm = 2.0 / (FftSize * 0.5);
        float fall = (float)Math.Clamp(dt * 1.8, 0, 1);       // full scale to zero in ~0.55 s
        float peakFall = (float)Math.Clamp(dt * 0.6, 0, 1);
        for (int k = 0; k <= FftSize / 2; k++) _mag[k] = Math.Sqrt(_re[k] * _re[k] + _im[k] * _im[k]) * norm;
        for (int b = 0; b < Bands; b++)
        {
            double max = 0;
            if (_bandHi[b] - _bandLo[b] <= 2)
            {
                // Narrower than a couple of bins (the bass end): read the spectrum at the band's centre,
                // so neighbouring bars do not all show the same bin.
                int k = Math.Clamp((int)_centreBin[b], 0, FftSize / 2 - 1);
                double f = _centreBin[b] - k;
                max = _mag[k] * (1 - f) + _mag[k + 1] * f;
            }
            else
                for (int k = _bandLo[b]; k < _bandHi[b]; k++) max = Math.Max(max, _mag[k]);
            double db = 20 * Math.Log10(max + 1e-9);
            // Tilt +3 dB/octave so music (which falls off with frequency) reads evenly across the bars.
            double centre = Math.Sqrt(_bandLo[b] * (double)_bandHi[b]) * SampleRate / FftSize;
            db += 3 * Math.Log2(Math.Max(centre, 20) / 1000.0);
            float level = (float)Math.Clamp((db + 66) / 60, 0, 1);

            _levels[b] = level > _levels[b] ? level : Math.Max(level, _levels[b] - fall);
            if (_levels[b] >= _peaks[b]) { _peaks[b] = _levels[b]; _peakHold[b] = 0.35f; }
            else if ((_peakHold[b] -= (float)dt) <= 0) _peaks[b] = Math.Max(_levels[b], _peaks[b] - peakFall);
        }
    }

    /// <summary>The band whose range contains <paramref name="hz"/> (for tests and labels).</summary>
    public int BandOf(double hz)
    {
        double bin = hz * FftSize / SampleRate;
        int best = 0;
        for (int b = 1; b < Bands; b++)
            if (Math.Abs(Math.Log(_centreBin[b] / bin)) < Math.Abs(Math.Log(_centreBin[best] / bin))) best = b;
        return best;
    }

    // In-place iterative radix-2 FFT.
    private static void Fft(double[] re, double[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2 * Math.PI / len;
            double wr = Math.Cos(ang), wi = Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = a + len / 2;
                    double tr = re[b] * cr - im[b] * ci, ti = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - tr; im[b] = im[a] - ti;
                    re[a] += tr; im[a] += ti;
                    (cr, ci) = (cr * wr - ci * wi, cr * wi + ci * wr);
                }
            }
        }
    }
}
