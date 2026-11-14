using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using LocalFM.App;
using LocalFM.App.Visuals;

namespace LocalFM.UI.Controls;

public enum VisualizerMode { Spectrum, Wave, Off }

/// <summary>
/// Draws the audio as it is heard: spectrum bars with falling peaks, or a waveform. Runs on the compositor's
/// frame clock only while <see cref="Active"/>; it reads the engine's lock-free tap, so it can never slow playback.
/// </summary>
public sealed class Visualizer : Control
{
    public static readonly StyledProperty<PlayerService?> SourceProperty = AvaloniaProperty.Register<Visualizer, PlayerService?>(nameof(Source));
    public static readonly StyledProperty<VisualizerMode> ModeProperty = AvaloniaProperty.Register<Visualizer, VisualizerMode>(nameof(Mode));
    public static readonly StyledProperty<bool> ActiveProperty = AvaloniaProperty.Register<Visualizer, bool>(nameof(Active));
    public static readonly StyledProperty<IBrush?> BarBrushProperty = AvaloniaProperty.Register<Visualizer, IBrush?>(nameof(BarBrush));
    public static readonly StyledProperty<int> BandsProperty = AvaloniaProperty.Register<Visualizer, int>(nameof(Bands), 56);

    private readonly float[] _buffer = new float[SpectrumAnalyzer.FftSize * 2];
    private SpectrumAnalyzer? _analyzer;
    private long _lastTotal = -1;
    private TimeSpan? _lastFrame;
    private TimeSpan _lastNewAudio;
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
        if (Mode == VisualizerMode.Spectrum) _analyzer.Update(_buffer, dt);

        InvalidateVisual();
        TopLevel.GetTopLevel(this)?.RequestAnimationFrame(OnFrame);
    }

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);
        var brush = BarBrush ?? Brushes.HotPink;
        double w = Bounds.Width, h = Bounds.Height;
        if (w <= 0 || h <= 0 || !_running) return;

        if (Mode == VisualizerMode.Spectrum && _analyzer is { } a)
        {
            var levels = a.Levels;
            var peaks = a.Peaks;
            double slot = w / levels.Length, gap = Math.Max(2, slot * 0.22), bw = Math.Max(1, slot - gap);
            var peakBrush = new SolidColorBrush(Colors.White, 0.75);
            for (int i = 0; i < levels.Length; i++)
            {
                double x = i * slot + gap / 2;
                double bh = Math.Max(2, levels[i] * (h - 6));
                ctx.DrawRectangle(brush, null, new RoundedRect(new Rect(x, h - bh, bw, bh), Math.Min(3, bw / 2), Math.Min(3, bw / 2), 0, 0));
                double py = h - Math.Max(2, peaks[i] * (h - 6)) - 4;
                ctx.DrawRectangle(peakBrush, null, new Rect(x, py, bw, 2));
            }
        }
        else if (Mode == VisualizerMode.Wave)
        {
            // The most recent ~40 ms, mono, centred.
            int frames = Math.Min(2048, _buffer.Length / 2);
            int start = _buffer.Length / 2 - frames;
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                int points = (int)Math.Min(w, frames);
                for (int p = 0; p < points; p++)
                {
                    int f = start + p * frames / points;
                    float s = 0.5f * (_buffer[f * 2] + _buffer[f * 2 + 1]);
                    var pt = new Point(p * w / (points - 1), h / 2 - Math.Clamp(s, -1, 1) * (h / 2 - 2));
                    if (p == 0) g.BeginFigure(pt, false);
                    else g.LineTo(pt);
                }
                g.EndFigure(false);
            }
            ctx.DrawGeometry(null, new Pen(brush, 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), geo);
        }
    }
}
