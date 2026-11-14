using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.TextFormatting;
using Avalonia.Utilities;
using LocalFM.Metadata;

namespace LocalFM.UI.Controls;

/// <summary>
/// A lyric line that lights up word by word as it is sung: each word fades from <see cref="UnsungBrush"/>
/// (the colour of lines still to come) to the line's own foreground, the same look as the whole-line highlight.
/// Drawn as one text layout with per-word colours, so there are no overlapping layers.
/// Styled like any TextBlock; animates on the frame clock only while <see cref="Live"/>.
/// </summary>
public sealed class KaraokeText : TextBlock
{
    public static readonly StyledProperty<IReadOnlyList<WordTiming>?> WordsProperty =
        AvaloniaProperty.Register<KaraokeText, IReadOnlyList<WordTiming>?>(nameof(Words));
    public static readonly StyledProperty<Func<TimeSpan>?> ClockProperty =
        AvaloniaProperty.Register<KaraokeText, Func<TimeSpan>?>(nameof(Clock));
    public static readonly StyledProperty<bool> LiveProperty = AvaloniaProperty.Register<KaraokeText, bool>(nameof(Live));
    public static readonly StyledProperty<IBrush?> UnsungBrushProperty = AvaloniaProperty.Register<KaraokeText, IBrush?>(nameof(UnsungBrush));

    /// <summary>How long a word takes to light up once it starts.</summary>
    private static readonly TimeSpan WordFade = TimeSpan.FromMilliseconds(220);
    private const int Steps = 8;

    private bool _running, _attached;
    private int[] _levels = [];
    private Color _bright, _dim;
    private TextLayout? _layout;
    private bool _dirty = true;

    protected override Type StyleKeyOverride => typeof(TextBlock);

    public IReadOnlyList<WordTiming>? Words { get => GetValue(WordsProperty); set => SetValue(WordsProperty, value); }
    public Func<TimeSpan>? Clock { get => GetValue(ClockProperty); set => SetValue(ClockProperty, value); }
    public bool Live { get => GetValue(LiveProperty); set => SetValue(LiveProperty, value); }
    public IBrush? UnsungBrush { get => GetValue(UnsungBrushProperty); set => SetValue(UnsungBrushProperty, value); }

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
        if (change.Property == LiveProperty || change.Property == WordsProperty || change.Property == ClockProperty)
            UpdateRunning();
        if (change.Property == TextProperty || change.Property == FontSizeProperty || change.Property == FontWeightProperty
            || change.Property == UnsungBrushProperty || change.Property == WordsProperty)
            _dirty = true;
    }

    private void UpdateRunning()
    {
        bool want = _attached && Live && Words is { Count: > 0 } && Clock is not null;
        if (want == _running) return;
        _running = want;
        _dirty = true;
        if (want) OnFrame(TimeSpan.Zero);
        else InvalidateVisual();
    }

    private void OnFrame(TimeSpan _)
    {
        if (!_running || Words is not { } words || Clock is not { } clock) return;
        var now = clock();
        if (_levels.Length != words.Count) { _levels = new int[words.Count]; _dirty = true; }
        for (int i = 0; i < words.Count; i++)
        {
            double f = Math.Clamp((now - words[i].Start) / WordFade, 0, 1);
            int level = (int)Math.Round(f * Steps);
            if (level != _levels[i]) { _levels[i] = level; _dirty = true; }
        }
        // The line's own colour fades in when it becomes current: follow it.
        var bright = (Foreground as ISolidColorBrush)?.Color ?? Colors.White;
        var dim = (UnsungBrush as ISolidColorBrush)?.Color ?? bright;
        if (bright != _bright || dim != _dim) { _bright = bright; _dim = dim; _dirty = true; }
        if (_dirty) InvalidateVisual();
        TopLevel.GetTopLevel(this)?.RequestAnimationFrame(OnFrame);
    }

    protected override void RenderTextLayout(DrawingContext context, Point origin)
    {
        if (!_running || Words is not { } words || string.IsNullOrEmpty(Text) || _levels.Length != words.Count)
        {
            base.RenderTextLayout(context, origin);
            return;
        }
        var layout = TextLayout;
        if (_dirty || _layout is null)
        {
            _layout?.Dispose();
            var typeface = new Typeface(FontFamily, FontStyle, FontWeight, FontStretch);
            var spans = new List<ValueSpan<TextRunProperties>>(words.Count);
            for (int i = 0; i < words.Count; i++)
            {
                var w = words[i];
                if (_levels[i] == 0 || w.CharStart + w.CharLength > Text.Length) continue;
                // Light the space after a word with it, so the line reads as one piece once sung.
                int length = w.CharStart + w.CharLength < Text.Length ? w.CharLength + 1 : w.CharLength;
                var brush = new ImmutableSolidColorBrush(Mix(_dim, _bright, _levels[i] / (double)Steps));
                spans.Add(new ValueSpan<TextRunProperties>(w.CharStart, length,
                    new GenericTextRunProperties(typeface, FontSize, foregroundBrush: brush)));
            }
            _layout = new TextLayout(Text, typeface, FontSize, new ImmutableSolidColorBrush(_dim), TextAlignment, TextWrapping,
                maxWidth: layout.MaxWidth, maxHeight: layout.MaxHeight, lineHeight: LineHeight, letterSpacing: LetterSpacing,
                textStyleOverrides: spans);
            _dirty = false;
        }
        _layout.Draw(context, origin);
    }

    private static Color Mix(Color a, Color b, double t) => Color.FromArgb(
        (byte)Math.Round(a.A + (b.A - a.A) * t), (byte)Math.Round(a.R + (b.R - a.R) * t),
        (byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t));
}
