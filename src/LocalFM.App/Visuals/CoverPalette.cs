namespace LocalFM.App.Visuals;

/// <summary>Picks an accent colour from cover art that reads well on the app's dark background.</summary>
public static class CoverPalette
{
    /// <summary>
    /// <paramref name="rgba"/> is a small (e.g. 48×48) RGBA8888 image. Returns null for covers without a clear
    /// colour (greyscale, near-black), so the default accent stays.
    /// </summary>
    public static (byte R, byte G, byte B)? Accent(ReadOnlySpan<byte> rgba)
    {
        const int buckets = 24;
        Span<double> weight = stackalloc double[buckets];
        Span<double> sumR = stackalloc double[buckets], sumG = stackalloc double[buckets], sumB = stackalloc double[buckets];
        int pixels = 0;
        double chromatic = 0;
        for (int i = 0; i + 3 < rgba.Length; i += 4)
        {
            if (rgba[i + 3] < 128) continue;
            pixels++;
            double r = rgba[i] / 255.0, g = rgba[i + 1] / 255.0, b = rgba[i + 2] / 255.0;
            var (h, s, v) = Hsv(r, g, b);
            if (s < 0.25 || v < 0.2) continue;
            // Prefer vivid, mid-to-bright colours over dark or washed-out ones.
            double w = s * s * (1 - Math.Abs(v - 0.7));
            int k = (int)(h / 360 * buckets) % buckets;
            weight[k] += w;
            sumR[k] += r * w; sumG[k] += g * w; sumB[k] += b * w;
            chromatic += w;
        }
        if (pixels == 0 || chromatic / pixels < 0.02) return null;

        // The strongest hue, smoothed with its neighbours (hues near a bucket edge should not split).
        int best = 0;
        double bestW = -1;
        for (int k = 0; k < buckets; k++)
        {
            double w = weight[k] + 0.5 * (weight[(k + 1) % buckets] + weight[(k + buckets - 1) % buckets]);
            if (w > bestW) { bestW = w; best = k; }
        }
        if (weight[best] <= 0) return null;
        double cr = sumR[best] / weight[best], cg = sumG[best] / weight[best], cb = sumB[best] / weight[best];

        // Readable on #111114 with white text on top: keep saturation lively and lightness in a narrow band.
        var (hue, sat, light) = Hsl(cr, cg, cb);
        sat = Math.Clamp(sat, 0.45, 0.85);
        light = Math.Clamp(light, 0.52, 0.66);
        var (rr, gg, bb) = FromHsl(hue, sat, light);
        return ((byte)Math.Round(rr * 255), (byte)Math.Round(gg * 255), (byte)Math.Round(bb * 255));
    }

    private static (double H, double S, double V) Hsv(double r, double g, double b)
    {
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = d == 0 ? 0 : max == r ? 60 * (((g - b) / d) % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        if (h < 0) h += 360;
        return (h, max == 0 ? 0 : d / max, max);
    }

    public static (double H, double S, double L) Hsl(double r, double g, double b)
    {
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), l = (max + min) / 2, d = max - min;
        if (d == 0) return (0, 0, l);
        double s = d / (1 - Math.Abs(2 * l - 1));
        double h = max == r ? 60 * (((g - b) / d) % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        if (h < 0) h += 360;
        return (h, s, l);
    }

    public static (double R, double G, double B) FromHsl(double h, double s, double l)
    {
        double c = (1 - Math.Abs(2 * l - 1)) * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = l - c / 2;
        var (r, g, b) = h switch
        {
            < 60 => (c, x, 0.0), < 120 => (x, c, 0.0), < 180 => (0.0, c, x),
            < 240 => (0.0, x, c), < 300 => (x, 0.0, c), _ => (c, 0.0, x),
        };
        return (r + m, g + m, b + m);
    }
}
