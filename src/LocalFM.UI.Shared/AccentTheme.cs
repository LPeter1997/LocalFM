using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using LocalFM.App.Visuals;
using LocalFM.Core;

namespace LocalFM.UI;

/// <summary>
/// Tints the app with a colour from the playing album's cover. The shared accent brush is recoloured in place
/// (so every StaticResource user follows), with a short cross-fade; Fluent's own accent keys follow at the end.
/// The cover is read off the UI thread at thumbnail size.
/// </summary>
public sealed class AccentTheme
{
    private readonly Color _default;
    private readonly SolidColorBrush? _brush;
    private readonly DispatcherTimer _timer;
    private Color _from, _to;
    private DateTime _start;
    private string? _coverPath;
    private int _version;
    private static readonly TimeSpan FadeTime = TimeSpan.FromMilliseconds(600);

    public AccentTheme()
    {
        var app = Application.Current!;
        _default = app.TryGetResource("AccentColor", null, out var c) && c is Color col ? col : Color.Parse("#E0567A");
        _brush = app.TryGetResource("Accent", null, out var b) ? b as SolidColorBrush : null;
        _from = _to = _default;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) => Step());
    }

    public bool Enabled { get; private set; } = true;
    public Color Current => _brush?.Color ?? _to;

    public void SetEnabled(bool on)
    {
        Enabled = on;
        _ = UpdateAsync(_coverPath, force: true);
    }

    /// <summary>Call when the playing cover changes (null = no cover).</summary>
    public Task CoverChangedAsync(string? coverPath) => UpdateAsync(coverPath, force: false);

    private async Task UpdateAsync(string? coverPath, bool force)
    {
        if (!force && coverPath == _coverPath) return;
        _coverPath = coverPath;
        int version = ++_version;
        Color target = _default;
        if (Enabled && coverPath is not null)
        {
            var picked = await Task.Run(() => Pick(coverPath));
            if (version != _version) return; // a newer cover arrived meanwhile
            if (picked is { } p) target = Color.FromRgb(p.R, p.G, p.B);
        }
        FadeTo(target);
    }

    private static (byte R, byte G, byte B)? Pick(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var fs = File.OpenRead(path);
            using var bmp = Bitmap.DecodeToWidth(fs, 48, BitmapInterpolationMode.MediumQuality);
            var size = bmp.PixelSize;
            var buf = new byte[size.Width * size.Height * 4];
            var handle = GCHandle.Alloc(buf, GCHandleType.Pinned);
            try { bmp.CopyPixels(new PixelRect(size), handle.AddrOfPinnedObject(), buf.Length, size.Width * 4); }
            finally { handle.Free(); }
            if (bmp.Format is not { } f || f == PixelFormats.Bgra8888)
                for (int i = 0; i + 3 < buf.Length; i += 4) (buf[i], buf[i + 2]) = (buf[i + 2], buf[i]);
            return CoverPalette.Accent(buf);
        }
        catch (Exception ex)
        {
            Log.Debug("Theme", $"No colour from {path}: {ex.Message}");
            return null;
        }
    }

    private void FadeTo(Color target)
    {
        if (target == _to && !_timer.IsEnabled) return;
        _from = Current;
        _to = target;
        _start = DateTime.UtcNow;
        _timer.Start();
    }

    private void Step()
    {
        double t = Math.Clamp((DateTime.UtcNow - _start) / FadeTime, 0, 1);
        t = t * t * (3 - 2 * t); // ease in-out
        var c = Color.FromRgb(Lerp(_from.R, _to.R, t), Lerp(_from.G, _to.G, t), Lerp(_from.B, _to.B, t));
        if (_brush is not null) _brush.Color = c;
        if (t >= 1)
        {
            _timer.Stop();
            ApplyFluentAccent(_to);
        }
    }

    private static byte Lerp(byte a, byte b, double t) => (byte)Math.Round(a + (b - a) * t);

    /// <summary>Fluent controls (sliders, toggles, focus rings) use these keys through DynamicResource.</summary>
    private static void ApplyFluentAccent(Color c)
    {
        var res = Application.Current!.Resources;
        var (h, s, l) = CoverPalette.Hsl(c.R / 255.0, c.G / 255.0, c.B / 255.0);
        Color Shade(double dl)
        {
            var (r, g, b) = CoverPalette.FromHsl(h, s, Math.Clamp(l + dl, 0, 1));
            return Color.FromRgb((byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255));
        }
        res["SystemAccentColor"] = c;
        res["SystemAccentColorLight1"] = Shade(0.06);
        res["SystemAccentColorLight2"] = Shade(0.12);
        res["SystemAccentColorLight3"] = Shade(0.18);
        res["SystemAccentColorDark1"] = Shade(-0.06);
        res["SystemAccentColorDark2"] = Shade(-0.12);
        res["SystemAccentColorDark3"] = Shade(-0.18);
    }
}
