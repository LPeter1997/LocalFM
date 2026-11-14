using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace LocalFM.UI.Controls;

/// <summary>How an <see cref="Icon"/> maps its glyph into its box.</summary>
public enum IconStretch
{
    /// <summary>The glyph's own outline, scaled to fit the box and centred in it.</summary>
    Uniform,
    /// <summary>The 24×24 design grid, drawn at its own scale and centred (keeps glyphs drawn on one grid the same size).</summary>
    None,
}

/// <summary>
/// A line or filled glyph from the app's icon set. Unlike a stretched <see cref="Avalonia.Controls.Shapes.Path"/>, which
/// pins a non-square glyph to the top-left of its box (a pause sign sat 3 px left of its circle), this centres it.
/// Strokes keep their thickness in pixels whatever the scale.
/// </summary>
public sealed class Icon : Control
{
    public static readonly StyledProperty<Geometry?> DataProperty = AvaloniaProperty.Register<Icon, Geometry?>(nameof(Data));
    public static readonly StyledProperty<IBrush?> StrokeProperty = AvaloniaProperty.Register<Icon, IBrush?>(nameof(Stroke));
    public static readonly StyledProperty<IBrush?> FillProperty = AvaloniaProperty.Register<Icon, IBrush?>(nameof(Fill));
    public static readonly StyledProperty<double> StrokeThicknessProperty = AvaloniaProperty.Register<Icon, double>(nameof(StrokeThickness), 1.8);
    public static readonly StyledProperty<IconStretch> StretchProperty = AvaloniaProperty.Register<Icon, IconStretch>(nameof(Stretch));

    static Icon()
    {
        AffectsRender<Icon>(DataProperty, StrokeProperty, FillProperty, StrokeThicknessProperty, StretchProperty);
        AffectsMeasure<Icon>(DataProperty);
    }

    public Geometry? Data { get => GetValue(DataProperty); set => SetValue(DataProperty, value); }
    public IBrush? Stroke { get => GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public IBrush? Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public double StrokeThickness { get => GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }
    public IconStretch Stretch { get => GetValue(StretchProperty); set => SetValue(StretchProperty, value); }

    private Geometry? _placed;
    private Size _placedFor;

    protected override Size MeasureOverride(Size availableSize) => new(18, 18); // styles and callers set the real size

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DataProperty || change.Property == StretchProperty) _placed = null;
    }

    public override void Render(DrawingContext context)
    {
        if (Data is null || Bounds.Width <= 0 || Bounds.Height <= 0) return;
        var size = Bounds.Size;
        if (_placed is null || _placedFor != size)
        {
            _placed = Data.Clone();
            _placed.Transform = new MatrixTransform(Placement(Data.Bounds, size));
            _placedFor = size;
        }
        var pen = Stroke is { } s && StrokeThickness > 0
            ? new Pen(s, StrokeThickness, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round)
            : null;
        context.DrawGeometry(Fill, pen, _placed);
    }

    private Matrix Placement(Rect glyph, Size box)
    {
        if (Stretch == IconStretch.None) glyph = new Rect(0, 0, 24, 24);
        // A flat glyph (a minus sign) has no height to scale by; fit it by its width alone.
        double sx = glyph.Width > 0 ? box.Width / glyph.Width : double.PositiveInfinity;
        double sy = glyph.Height > 0 ? box.Height / glyph.Height : double.PositiveInfinity;
        double scale = Stretch == IconStretch.None ? 1 : Math.Min(sx, sy);
        if (double.IsInfinity(scale)) scale = 1;
        double x = (box.Width - glyph.Width * scale) / 2 - glyph.X * scale;
        double y = (box.Height - glyph.Height * scale) / 2 - glyph.Y * scale;
        return new Matrix(scale, 0, 0, scale, x, y);
    }
}
