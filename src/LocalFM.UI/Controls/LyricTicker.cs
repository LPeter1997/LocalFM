using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace LocalFM.UI.Controls;

/// <summary>
/// One lyric line in a fixed width: a <see cref="KaraokeText"/> child that is too long slides sideways so the
/// part being sung stays in view. With word timings it hops word to word (a quick eased jump when each word starts,
/// then still while it is sung) so the text isn't moving under the eye; without them it glides along the line's own
/// start/end.
/// The clipped edges fade out. Animates on the frame clock only while <see cref="Live"/>.
/// </summary>
public sealed class LyricTicker : Decorator
{
    public static readonly StyledProperty<TimeSpan?> StartProperty = AvaloniaProperty.Register<LyricTicker, TimeSpan?>(nameof(Start));
    public static readonly StyledProperty<TimeSpan?> EndProperty = AvaloniaProperty.Register<LyricTicker, TimeSpan?>(nameof(End));
    public static readonly StyledProperty<bool> LiveProperty = AvaloniaProperty.Register<LyricTicker, bool>(nameof(Live));

    /// <summary>Where the sung part sits in the box once the line has to slide (0 = left edge, 1 = right edge).</summary>
    private const double Anchor = 0.4;
    private const double Fade = 28;
    /// <summary>Time constant of the eased hop/glide toward the target, in seconds (~3x this to settle).</summary>
    private const double Ease = 0.055;

    private bool _attached, _running;
    private double _offset;
    private TimeSpan? _lastFrame;
    private string? _text;
    private (bool Left, bool Right) _fades;

    static LyricTicker() => ClipToBoundsProperty.OverrideDefaultValue<LyricTicker>(true);

    public TimeSpan? Start { get => GetValue(StartProperty); set => SetValue(StartProperty, value); }
    public TimeSpan? End { get => GetValue(EndProperty); set => SetValue(EndProperty, value); }
    public bool Live { get => GetValue(LiveProperty); set => SetValue(LiveProperty, value); }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Child is not { } child) return default;
        child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
        return new Size(Math.Min(child.DesiredSize.Width, availableSize.Width), child.DesiredSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Child?.Arrange(new Rect(-_offset, 0, Math.Max(Child.DesiredSize.Width, finalSize.Width), finalSize.Height));
        return finalSize;
    }

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
        if (change.Property == LiveProperty || change.Property == ChildProperty) UpdateRunning();
    }

    private void UpdateRunning()
    {
        bool want = _attached && Live && Child is KaraokeText;
        if (want == _running) return;
        _running = want;
        _lastFrame = null;
        if (want) OnFrame(TimeSpan.Zero);
    }

    private void OnFrame(TimeSpan time)
    {
        if (!_running || Child is not KaraokeText text) return;
        double target = Target(text);
        // Frame-rate independent easing; the first frame (or one after a long stall) uses a nominal 60 fps step.
        double dt = _lastFrame is { } last && time > last ? Math.Min((time - last).TotalSeconds, 0.1) : 1 / 60.0;
        _lastFrame = time;
        // A new line starts at its beginning; within a line, ease toward the target.
        if (text.Text != _text) { _text = text.Text; _offset = target; InvalidateArrange(); }
        else if (Math.Abs(target - _offset) > 0.25)
        {
            _offset += (target - _offset) * (1 - Math.Exp(-dt / Ease));
            InvalidateArrange();
        }
        else if (_offset != target) { _offset = target; InvalidateArrange(); }
        UpdateFades(text);
        TopLevel.GetTopLevel(this)?.RequestAnimationFrame(OnFrame);
    }

    /// <summary>The offset that puts the point being sung at <see cref="Anchor"/>.</summary>
    private double Target(KaraokeText text)
    {
        double width = Bounds.Width, overflow = text.DesiredSize.Width - width;
        if (overflow <= 0.5 || width <= 0 || text.Clock is not { } clock || string.IsNullOrEmpty(text.Text)) return 0;
        var now = clock();
        double x;
        if (text.Words is { Count: > 0 } words)
        {
            int i = -1;
            while (i + 1 < words.Count && words[i + 1].Start <= now) i++;
            if (i < 0) return 0;
            var w = words[i];
            var layout = text.TextLayout;
            int end = Math.Min(w.CharStart + w.CharLength, text.Text.Length);
            double x0 = layout.HitTestTextPosition(Math.Min(w.CharStart, text.Text.Length)).X;
            double x1 = layout.HitTestTextPosition(end).X;
            // The whole word, not the sweep through it: the target only moves when a new word starts.
            x = text.Padding.Left + (x0 + x1) / 2;
        }
        else if (Start is { } s && End is { } e && e > s)
            x = Math.Clamp((now - s) / (e - s), 0, 1) * text.DesiredSize.Width;
        else return 0;
        return Math.Clamp(x - width * Anchor, 0, overflow);
    }

    /// <summary>Fades the edge on each side where text is cut off.</summary>
    private void UpdateFades(KaraokeText text)
    {
        double width = Bounds.Width;
        var fades = (_offset > 0.5, text.DesiredSize.Width - _offset > width + 0.5);
        if (fades == _fades) return;
        _fades = fades;
        if (fades is (false, false) || width <= 0) { OpacityMask = null; return; }
        double f = Math.Min(Fade / width, 0.25);
        OpacityMask = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(fades.Item1 ? Colors.Transparent : Colors.Black, 0),
                new GradientStop(Colors.Black, fades.Item1 ? f : 0),
                new GradientStop(Colors.Black, fades.Item2 ? 1 - f : 1),
                new GradientStop(fades.Item2 ? Colors.Transparent : Colors.Black, 1),
            },
        };
    }
}
