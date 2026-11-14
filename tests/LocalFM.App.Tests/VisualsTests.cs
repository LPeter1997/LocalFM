using LocalFM.App.Visuals;

namespace LocalFM.App.Tests;

public class VisualsTests
{
    [Theory]
    [InlineData(100)]
    [InlineData(1000)]
    [InlineData(6000)]
    public void Spectrum_peaks_at_the_played_frequency(double hz)
    {
        var a = new SpectrumAnalyzer(48, 48000);
        var buf = new float[SpectrumAnalyzer.FftSize * 2];
        for (int i = 0; i < SpectrumAnalyzer.FftSize; i++) buf[i * 2] = buf[i * 2 + 1] = 0.5f * MathF.Sin((float)(2 * Math.PI * hz * i / 48000));
        a.Update(buf, 1 / 60.0);
        var levels = a.Levels.ToArray();
        int loudest = Array.IndexOf(levels, levels.Max());
        Assert.InRange(loudest, a.BandOf(hz) - 1, a.BandOf(hz) + 1);
        Assert.True(levels[loudest] > 0.6f);
        Assert.True(levels.Where((_, i) => Math.Abs(i - loudest) > 4).All(l => l < levels[loudest] * 0.5f), $"{hz}: {string.Join(" ", levels.Select(l => l.ToString("0.00")))}");

        // Silence: bars fall, peaks linger a moment, then fall too.
        Array.Clear(buf);
        a.Update(buf, 0.1);
        Assert.True(a.Levels[loudest] < levels[loudest]);
        Assert.True(a.Peaks[loudest] >= levels[loudest] - 0.01f);
        for (int i = 0; i < 40; i++) a.Update(buf, 0.1);
        Assert.Equal(0f, a.Peaks[loudest], 2);
    }

    private static byte[] Image(params (byte R, byte G, byte B, int Count)[] parts)
    {
        var list = new List<byte>();
        foreach (var (r, g, b, n) in parts)
            for (int i = 0; i < n; i++) list.AddRange([r, g, b, 255]);
        return list.ToArray();
    }

    [Fact]
    public void Picks_the_vivid_colour_and_keeps_it_readable()
    {
        // Mostly dark navy with a strong orange title: orange wins, lightened for the dark UI.
        var c = CoverPalette.Accent(Image((10, 12, 30, 1800), (240, 120, 20, 400)))!.Value;
        var (h, s, l) = CoverPalette.Hsl(c.R / 255.0, c.G / 255.0, c.B / 255.0);
        Assert.InRange(h, 20, 40);
        Assert.InRange(l, 0.5, 0.68);
        Assert.True(s >= 0.45);

        // Black-and-white covers keep the default accent.
        Assert.Null(CoverPalette.Accent(Image((0, 0, 0, 1000), (255, 255, 255, 1000), (128, 128, 128, 304))));
    }
}
