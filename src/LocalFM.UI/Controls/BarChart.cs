using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace LocalFM.UI.Controls;

/// <summary>Bars to draw: values, an axis label per bar (empty to skip) and a hover text per bar.</summary>
public sealed record ChartData(IReadOnlyList<double> Values, IReadOnlyList<string> Labels, IReadOnlyList<string> Tips);

/// <summary>A small, dependency-free bar chart with hover read-outs.</summary>
public sealed class BarChart : Control
{
    public static readonly StyledProperty<ChartData?> DataProperty = AvaloniaProperty.Register<BarChart, ChartData?>(nameof(Data));
    public static readonly StyledProperty<IBrush?> BarBrushProperty = AvaloniaProperty.Register<BarChart, IBrush?>(nameof(BarBrush));
    public static readonly StyledProperty<IBrush?> LabelBrushProperty = AvaloniaProperty.Register<BarChart, IBrush?>(nameof(LabelBrush));

    private int _hover = -1;

    static BarChart() => AffectsRender<BarChart>(DataProperty, BarBrushProperty, LabelBrushProperty);

    public ChartData? Data { get => GetValue(DataProperty); set => SetValue(DataProperty, value); }
    public IBrush? BarBrush { get => GetValue(BarBrushProperty); set => SetValue(BarBrushProperty, value); }
    public IBrush? LabelBrush { get => GetValue(LabelBrushProperty); set => SetValue(LabelBrushProperty, value); }

    private const double LabelHeight = 18, TipHeight = 22;

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        int i = IndexAt(e.GetPosition(this).X);
        if (i != _hover) { _hover = i; InvalidateVisual(); }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hover != -1) { _hover = -1; InvalidateVisual(); }
    }

    private int IndexAt(double x)
    {
        if (Data is not { Values.Count: > 0 } d || Bounds.Width <= 0) return -1;
        int i = (int)(x / (Bounds.Width / d.Values.Count));
        return i >= 0 && i < d.Values.Count ? i : -1;
    }

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);
        ctx.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size)); // hit-testable everywhere
        if (Data is not { Values.Count: > 0 } d) return;
        var bar = BarBrush ?? Brushes.HotPink;
        var label = LabelBrush ?? Brushes.Gray;
        double w = Bounds.Width, h = Bounds.Height - LabelHeight, top = TipHeight;
        double max = Math.Max(d.Values.Max(), 1e-9);
        double slot = w / d.Values.Count;
        double gap = Math.Min(slot * 0.25, 6), barW = Math.Max(1, slot - gap);
        var typeface = new Typeface(FontFamily.Default);

        for (int i = 0; i < d.Values.Count; i++)
        {
            double v = d.Values[i];
            double bh = v <= 0 ? 0 : Math.Max(2, (h - top) * v / max);
            double x = i * slot + gap / 2;
            var rect = new Rect(x, h - bh, barW, bh);
            if (bh > 0)
                ctx.DrawRectangle(bar, null, new RoundedRect(rect, Math.Min(3, barW / 2), Math.Min(3, barW / 2), 0, 0));
            if (i == _hover) ctx.DrawRectangle(new SolidColorBrush(Colors.White, 0.06), null, new Rect(i * slot, top, slot, h - top));
            if (i < d.Labels.Count && d.Labels[i] is { Length: > 0 } text)
            {
                var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 11, label);
                double lx = Math.Clamp(x + barW / 2 - ft.Width / 2, 0, Math.Max(0, w - ft.Width));
                ctx.DrawText(ft, new Point(lx, h + 3));
            }
        }
        ctx.DrawLine(new Pen(label, 0.5), new Point(0, h + 0.5), new Point(w, h + 0.5));

        if (_hover >= 0 && _hover < d.Tips.Count && d.Tips[_hover] is { Length: > 0 } tip)
        {
            var ft = new FormattedText(tip, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 12, Brushes.White);
            double cx = _hover * slot + slot / 2;
            double tx = Math.Clamp(cx - ft.Width / 2 - 8, 0, Math.Max(0, w - ft.Width - 16));
            ctx.DrawRectangle(new SolidColorBrush(Color.Parse("#2C2C34")), null, new RoundedRect(new Rect(tx, 0, ft.Width + 16, TipHeight - 3), 5));
            ctx.DrawText(ft, new Point(tx + 8, (TipHeight - 3 - ft.Height) / 2));
        }
    }
}
