using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using LocalFM.Metadata;

namespace LocalFM.UI.Controls;

/// <summary>
/// A lyric line that fills in word by word as it is sung: the sung part is drawn again in
/// <see cref="SungBrush"/>, clipped to the characters sung so far, sweeping smoothly through the current word.
/// Styled like any TextBlock; animates on the frame clock only while <see cref="Live"/>.
/// </summary>
public sealed class KaraokeText : TextBlock
{
    public static readonly StyledProperty<IReadOnlyList<WordTiming>?> WordsProperty =
        AvaloniaProperty.Register<KaraokeText, IReadOnlyList<WordTiming>?>(nameof(Words));
    public static readonly StyledProperty<Func<TimeSpan>?> ClockProperty =
        AvaloniaProperty.Register<KaraokeText, Func<TimeSpan>?>(nameof(Clock));
    public static readonly StyledProperty<bool> LiveProperty = AvaloniaProperty.Register<KaraokeText, bool>(nameof(Live));
    public static readonly StyledProperty<IBrush?> SungBrushProperty = AvaloniaProperty.Register<KaraokeText, IBrush?>(nameof(SungBrush));

    private double _sung;
    private bool _running, _attached;
    private TextLayout? _sungLayout, _sungFor;

    static KaraokeText() => AffectsRender<KaraokeText>(SungBrushProperty);

    protected override Type StyleKeyOverride => typeof(TextBlock);

    public IReadOnlyList<WordTiming>? Words { get => GetValue(WordsProperty); set => SetValue(WordsProperty, value); }
    public Func<TimeSpan>? Clock { get => GetValue(ClockProperty); set => SetValue(ClockProperty, value); }
    public bool Live { get => GetValue(LiveProperty); set => SetValue(LiveProperty, value); }
    public IBrush? SungBrush { get => GetValue(SungBrushProperty); set => SetValue(SungBrushProperty, value); }

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
        if (change.Property == SungBrushProperty) _sungLayout = null;
    }

    private void UpdateRunning()
    {
        bool want = _attached && Live && Words is { Count: > 0 } && Clock is not null;
        if (want == _running) return;
        _running = want;
        if (want) OnFrame(TimeSpan.Zero);
        else if (_sung != 0) { _sung = 0; InvalidateVisual(); }
    }

    private void OnFrame(TimeSpan _)
    {
        if (!_running || Words is not { } words || Clock is not { } clock) return;
        double sung = Karaoke.SungChars(words, clock());
        if (Math.Abs(sung - _sung) > 0.01) { _sung = sung; InvalidateVisual(); }
        TopLevel.GetTopLevel(this)?.RequestAnimationFrame(OnFrame);
    }

    protected override void RenderTextLayout(DrawingContext context, Point origin)
    {
        base.RenderTextLayout(context, origin);
        if (!_running || _sung <= 0 || SungBrush is not { } brush || string.IsNullOrEmpty(Text)) return;
        var layout = TextLayout;
        if (!ReferenceEquals(layout, _sungFor) || _sungLayout is null)
        {
            _sungLayout?.Dispose();
            _sungLayout = new TextLayout(Text, new Typeface(FontFamily, FontStyle, FontWeight, FontStretch), FontSize, brush,
                TextAlignment, TextWrapping, maxWidth: layout.MaxWidth, maxHeight: layout.MaxHeight,
                lineHeight: LineHeight, letterSpacing: LetterSpacing);
            _sungFor = layout;
        }

        int length = Text.Length;
        int whole = Math.Min((int)Math.Floor(_sung), length);
        var rects = whole > 0 ? layout.HitTestTextRange(0, whole).ToList() : [];
        double part = _sung - whole;
        if (whole < length && part > 0 && layout.HitTestTextRange(whole, 1).FirstOrDefault() is { Width: > 0 } r)
            rects.Add(r.WithWidth(r.Width * part));

        foreach (var rect in rects)
        {
            // A little room above and below so accents and descenders are not cut.
            using (context.PushClip(rect.Translate(origin).Inflate(new Thickness(0, 4))))
                _sungLayout.Draw(context, origin);
        }
    }
}
