using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using LocalFM.App;
using LocalFM.App.Visuals;

namespace LocalFM.UI.Controls;

public enum VisualizerMode { Spectrum, Mirror, Wave, Off }

/// <summary>
/// Draws the audio as it is heard, in the accent colour:
/// <list type="bullet">
/// <item>Spectrum: gradient bars with falling peaks and a soft reflection.</item>
/// <item>Mirror: the spectrum folded around the centre (bass in the middle), growing up and down.</item>
/// <item>Wave: a steady oscilloscope trace (locked to the waveform's rising edge) with a glow.</item>
/// </list>
/// Runs on the compositor's frame clock only while <see cref="Active"/>; it reads the engine's lock-free tap,
/// so it can never slow playback. Silence draws nothing.
/// </summary>
public sealed class Visualizer : Control
{
    public static readonly StyledProperty<PlayerService?> SourceProperty = AvaloniaProperty.Register<Visualizer, PlayerService?>(nameof(Source));
    public static readonly StyledProperty<VisualizerMode> ModeProperty = AvaloniaProperty.Register<Visualizer, VisualizerMode>(nameof(Mode));
    public static readonly StyledProperty<bool> ActiveProperty = AvaloniaProperty.Register<Visualizer, bool>(nameof(Active));
    public static readonly StyledProperty<IBrush?> BarBrushProperty = AvaloniaProperty.Register<Visualizer, IBrush?>(nameof(BarBrush));
    public static readonly StyledProperty<int> BandsProperty = AvaloniaProperty.Register<Visualizer, int>(nameof(Bands), 64);

    private readonly float[] _buffer = new float[SpectrumAnalyzer.FftSize * 2];
    private SpectrumAnalyzer? _analyzer;
    private float[] _shown = [];
    private long _lastTotal = -1;
    private TimeSpan? _lastFrame;
    private TimeSpan _lastNewAudio;
    private double _waveGain = 1;
    private bool _running, _attached;

    public PlayerService? Source { get => GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public VisualizerMode Mode { get => GetValue(ModeProperty); set => SetValue(ModeProperty, value); }
    public bool Active { get => GetValue(ActiveProperty); set => SetValue(ActiveProperty, value); }
    public IBrush? BarBrush { get => GetValue(BarBrushProperty); set => SetValue(BarBrushProperty, value); }
    public int Bands { get => GetValue(BandsProperty); set => SetValue(BandsProperty, value); }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        UpdateRunning();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        _running = false;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ActiveProperty || change.Property == ModeProperty || change.Property == SourceProperty)
            UpdateRunning();
        if (change.Property == BandsProperty || change.Property == SourceProperty) _analyzer = null;
        if (change.Property == ModeProperty || change.Property == BarBrushProperty) InvalidateVisual();
    }

    private void UpdateRunning()
    {
        bool want = _attached && Active && Mode != VisualizerMode.Off && Source is not null;
        if (want == _running) return;
        _running = want;
        _lastFrame = null;
        if (want) TopLevel.GetTopLevel(this)?.RequestAnimationFrame(OnFrame);
        else InvalidateVisual();
    }

    private void OnFrame(TimeSpan now)
    {
        if (!_running || Source is not { } source) return;
        double dt = _lastFrame is { } last ? Math.Clamp((now - last).TotalSeconds, 0.001, 0.25) : 1 / 60.0;
        _lastFrame = now;
        _analyzer ??= new SpectrumAnalyzer(Math.Clamp(Bands, 8, 128), source.SampleRate);

        long total = source.ReadVisualization(_buffer);
        // Audio arrives in device periods (often longer than a frame), so "nothing new" only means stopped
        // (or no device) once it lasts a while; then let the picture settle instead of freezing.
        if (total != _lastTotal) { _lastTotal = total; _lastNewAudio = now; }
        else if (now - _lastNewAudio > TimeSpan.FromMilliseconds(150)) Array.Clear(_buffer);

        if (Mode is VisualizerMode.Spectrum or VisualizerMode.Mirror)
        {
            _analyzer.Update(_buffer, dt);
            var levels = _analyzer.Levels;
            if (_shown.Length != levels.Length) _shown = new float[levels.Length];
            // A touch of smoothing on the way up too: steadier bars without feeling late (~35 ms).
            float k = (float)Math.Clamp(dt * 28, 0, 1);
            for (int i = 0; i < levels.Length; i++) _shown[i] += (levels[i] - _shown[i]) * k;
        }

        InvalidateVisual();
        TopLevel.GetTopLevel(this)?.RequestAnimationFrame(OnFrame);
    }

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);
        double w = Bounds.Width, h = Bounds.Height;
        if (w <= 0 || h <= 0 || !_running) return;
        var accent = (BarBrush as ISolidColorBrush)?.Color ?? Color.Parse("#E0567A");
        var light = Lighten(accent, 0.35);

        switch (Mode)
        {
            case VisualizerMode.Spectrum when _analyzer is { } a: DrawSpectrum(ctx, w, h, a.Peaks, accent, light); break;
            case VisualizerMode.Mirror: DrawMirror(ctx, w, h, accent, light); break;
            case VisualizerMode.Wave: DrawWave(ctx, w, h, accent, light); break;
        }
    }

    private void DrawSpectrum(DrawingContext ctx, double w, double h, ReadOnlySpan<float> peaks, Color accent, Color light)
    {
        int n = _shown.Length;
        if (n == 0) return;
        double floor = h * 0.8;            // bars stand on this line; the reflection goes below
        double room = floor - 6;
        double slot = w / n, gap = Math.Max(2, slot * 0.28), bw = Math.Max(1.5, slot - gap);
        double radius = Math.Min(3, bw / 2);
        var bar = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, floor, RelativeUnit.Absolute),
            EndPoint = new RelativePoint(0, floor - room, RelativeUnit.Absolute),
            GradientStops = { new GradientStop(WithAlpha(accent, 0.85), 0), new GradientStop(light, 1) },
        };
        var reflection = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, floor, RelativeUnit.Absolute),
            EndPoint = new RelativePoint(0, h, RelativeUnit.Absolute),
            GradientStops = { new GradientStop(WithAlpha(accent, 0.28), 0), new GradientStop(WithAlpha(accent, 0), 1) },
        };
        var peak = new ImmutableSolidColorBrush(WithAlpha(light, 0.9));
        for (int i = 0; i < n; i++)
        {
            double level = _shown[i];
            double x = i * slot + gap / 2;
            if (level > 0.008)
            {
                double bh = level * room;
                ctx.DrawRectangle(bar, null, new RoundedRect(new Rect(x, floor - bh, bw, bh), radius, radius, 0, 0));
                double rh = Math.Min(bh * 0.35, h - floor - 2);
                if (rh > 1) ctx.DrawRectangle(reflection, null, new RoundedRect(new Rect(x, floor + 2, bw, rh), 0, 0, radius, radius));
            }
            if (i < peaks.Length && peaks[i] > 0.03 && peaks[i] > level + 0.02)
                ctx.DrawRectangle(peak, null, new RoundedRect(new Rect(x, floor - peaks[i] * room - 3, bw, 2), 1));
        }
    }

    private void DrawMirror(DrawingContext ctx, double w, double h, Color accent, Color light)
    {
        int n = _shown.Length;
        if (n == 0) return;
        double mid = h / 2;
        // Bass in the centre, highs towards both edges: 2n bars across.
        double slot = w / (2 * n), gap = Math.Max(2, slot * 0.3), bw = Math.Max(1.5, slot - gap);
        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Absolute),
            EndPoint = new RelativePoint(0, h, RelativeUnit.Absolute),
            GradientStops =
            {
                new GradientStop(WithAlpha(light, 0.15), 0), new GradientStop(light, 0.38),
                new GradientStop(accent, 0.5), new GradientStop(light, 0.62), new GradientStop(WithAlpha(light, 0.15), 1),
            },
        };
        for (int i = 0; i < n; i++)
        {
            double level = _shown[i];
            if (level <= 0.008) continue;
            double half = Math.Max(bw / 2, level * (mid - 4));
            double r = bw / 2;
            double xr = w / 2 + i * slot + gap / 2;
            double xl = w / 2 - (i + 1) * slot + gap / 2;
            ctx.DrawRectangle(brush, null, new RoundedRect(new Rect(xr, mid - half, bw, half * 2), r));
            ctx.DrawRectangle(brush, null, new RoundedRect(new Rect(xl, mid - half, bw, half * 2), r));
        }
    }

    private void DrawWave(DrawingContext ctx, double w, double h, Color accent, Color light)
    {
        const int frames = 1536; // ~32 ms on screen
        int total = _buffer.Length / 2;
        // Trigger like an oscilloscope: start where the wave rises through zero, so it stands still.
        int start = total - frames;
        for (int f = total - frames - 1; f > total - frames - 1200 && f > 1; f--)
        {
            float prev = _buffer[(f - 1) * 2] + _buffer[(f - 1) * 2 + 1], cur = _buffer[f * 2] + _buffer[f * 2 + 1];
            if (prev < 0 && cur >= 0) { start = f; break; }
        }

        // Quiet passages are scaled up (at most 4x), settling smoothly so the trace does not pump.
        float loudest = 0;
        for (int f = start; f < start + frames; f++) loudest = Math.Max(loudest, Math.Abs(0.5f * (_buffer[f * 2] + _buffer[f * 2 + 1])));
        if (loudest < 0.002) return; // silence: draw nothing
        double target = Math.Clamp(0.8 / loudest, 1, 4);
        _waveGain += (target - _waveGain) * (target < _waveGain ? 0.35 : 0.04);

        double mid = h / 2, amp = (h / 2 - 6) * _waveGain;
        int points = (int)Math.Clamp(w / 2, 64, frames);
        var line = new StreamGeometry();
        var fill = new StreamGeometry();
        using (var g = line.Open())
        using (var fg = fill.Open())
        {
            fg.BeginFigure(new Point(0, mid), true);
            for (int p = 0; p < points; p++)
            {
                int f = start + (int)((long)p * frames / points);
                float s = 0.5f * (_buffer[f * 2] + _buffer[f * 2 + 1]);
                // Fade the ends so the trace floats rather than being cut off.
                double edge = Math.Min(1, Math.Min(p, points - 1 - p) / (points * 0.08));
                var pt = new Point(p * w / (points - 1), mid - Math.Clamp(s * amp * edge, -(mid - 4), mid - 4));
                if (p == 0) g.BeginFigure(pt, false);
                else g.LineTo(pt);
                fg.LineTo(pt);
            }
            fg.LineTo(new Point(w, mid));
            fg.EndFigure(true);
            g.EndFigure(false);
        }
        var area = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Absolute),
            EndPoint = new RelativePoint(0, h, RelativeUnit.Absolute),
            GradientStops = { new GradientStop(WithAlpha(accent, 0.22), 0), new GradientStop(WithAlpha(accent, 0.04), 0.5), new GradientStop(WithAlpha(accent, 0.22), 1) },
        };
        ctx.DrawGeometry(area, null, fill);
        ctx.DrawGeometry(null, new Pen(new ImmutableSolidColorBrush(WithAlpha(accent, 0.25)), 7, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), line);
        ctx.DrawGeometry(null, new Pen(new ImmutableSolidColorBrush(light), 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), line);
    }

    private static Color WithAlpha(Color c, double a) => Color.FromArgb((byte)Math.Round(255 * a), c.R, c.G, c.B);

    private static Color Lighten(Color c, double t) => Color.FromRgb(
        (byte)Math.Round(c.R + (255 - c.R) * t), (byte)Math.Round(c.G + (255 - c.G) * t), (byte)Math.Round(c.B + (255 - c.B) * t));
}
