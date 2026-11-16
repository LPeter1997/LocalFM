using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LocalFM.UI.ViewModels;

namespace LocalFM.UI.Views;

public partial class QueueView : UserControl
{
    public QueueView()
    {
        InitializeComponent();
        List.DoubleTapped += (_, _) =>
        {
            if (List.SelectedItem is QueueRow row && DataContext is QueueViewModel vm) vm.PlayCommand.Execute(row);
        };
        List.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && _lifted is not null) { Drop(commit: false); e.Handled = true; return; }
            if (List.SelectedItem is not QueueRow row || DataContext is not QueueViewModel vm) return;
            if (e.Key == Key.Delete) { vm.RemoveCommand.Execute(row); e.Handled = true; }
            else if (e.Key == Key.Enter) { vm.PlayCommand.Execute(row); e.Handled = true; }
        };
        SetUpDragToReorder();
    }

    // ------------------------------------------------------------------ drag to reorder

    private static readonly TimeSpan SlideTime = TimeSpan.FromMilliseconds(160);

    private QueueRow? _pressed;
    private Point _pressPoint;
    private ListBoxItem? _lifted;
    private int _from, _to, _count;
    private double _pitch, _startContentY, _lastY;
    private ScrollViewer? _scroll;
    private DispatcherTimer? _autoScroll;
    private bool _dropping;

    /// <summary>
    /// Press on a row and drag it: the row follows the pointer and the rows it passes slide out of its way.
    /// The queue itself changes once, on release (Esc cancels), so playback never sees the in-between orders.
    /// </summary>
    private void SetUpDragToReorder()
    {
        List.AddHandler(PointerPressedEvent, (_, e) =>
        {
            _pressed = null;
            if (_dropping || !e.GetCurrentPoint(List).Properties.IsLeftButtonPressed) return;
            if (e.Source is Visual v && v.FindAncestorOfType<Button>(includeSelf: true) is not null) return; // row buttons
            if ((e.Source as Control)?.DataContext is not QueueRow row) return;
            _pressed = row;
            _pressPoint = e.GetPosition(List);
        }, RoutingStrategies.Tunnel, handledEventsToo: true);

        List.AddHandler(PointerMovedEvent, (_, e) =>
        {
            if (_dropping || (_pressed is null && _lifted is null)) return;
            var p = e.GetPosition(List);
            if (_lifted is null)
            {
                if (Math.Abs(p.Y - _pressPoint.Y) < 6 || !Lift(_pressed!)) return;
                e.Pointer.Capture(List);
            }
            _lastY = p.Y;
            Follow();
            e.Handled = true;
        }, RoutingStrategies.Tunnel);

        List.AddHandler(PointerReleasedEvent, (_, e) =>
        {
            _pressed = null;
            if (_lifted is null) return;
            e.Pointer.Capture(null);
            Drop(commit: true);
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        List.AddHandler(PointerCaptureLostEvent, (_, _) => { if (_lifted is not null) Drop(commit: true); }, RoutingStrategies.Tunnel, handledEventsToo: true);

        // Rows scrolled into view mid-drag take their shifted place; recycled rows lose theirs.
        List.ContainerPrepared += (_, e) =>
        {
            if (_lifted is null || e.Container == _lifted) return;
            e.Container.RenderTransform = TranslateY(ShiftOf(e.Index));
            e.Container.Transitions = Slide();
        };
        List.ContainerClearing += (_, e) => { if (e.Container != _lifted) Reset(e.Container); };
    }

    /// <summary>Picks the row up. False when its container isn't there (cannot happen for a row under the pointer).</summary>
    private bool Lift(QueueRow row)
    {
        if (List.ContainerFromIndex(row.Index) is not ListBoxItem item) return false;
        _scroll = List.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        _from = _to = row.Index;
        _count = List.ItemCount;
        _pitch = Pitch(row.Index, item);
        _startContentY = _pressPoint.Y + ScrollY;
        _lifted = item;
        item.ZIndex = 1;
        item.Classes.Add("lifted");
        List.Classes.Add("dragging");
        foreach (var c in List.GetRealizedContainers())
        {
            c.RenderTransform = TranslateY(0);
            if (c != item) c.Transitions = Slide();
        }
        _autoScroll = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Input, (_, _) => AutoScroll());
        _autoScroll.Start();
        return true;
    }

    /// <summary>Moves the lifted row under the pointer and slides the others to make room.</summary>
    private void Follow()
    {
        if (_lifted is null) return;
        double dy = Math.Clamp(_lastY + ScrollY - _startContentY, -_from * _pitch, (_count - 1 - _from) * _pitch);
        _lifted.RenderTransform = TranslateY(dy);
        int to = Math.Clamp(_from + (int)Math.Round(dy / _pitch), 0, _count - 1);
        if (to == _to) return;
        _to = to;
        foreach (var c in List.GetRealizedContainers())
            if (c != _lifted) c.RenderTransform = TranslateY(ShiftOf(List.IndexFromContainer(c)));
    }

    /// <summary>Near the top or bottom edge, scroll the list (faster the closer to the edge) so a row can travel far.</summary>
    private void AutoScroll()
    {
        if (_scroll is null || _lifted is null) return;
        const double zone = 40;
        double h = List.Bounds.Height, step = 0;
        if (_lastY < zone) step = -(zone - Math.Max(_lastY, 0)) / 3;
        else if (_lastY > h - zone) step = (Math.Min(_lastY, h) - (h - zone)) / 3;
        if (step == 0) return;
        double max = Math.Max(0, _scroll.Extent.Height - _scroll.Viewport.Height);
        double y = Math.Clamp(_scroll.Offset.Y + step, 0, max);
        if (Math.Abs(y - _scroll.Offset.Y) < 0.5) return;
        _scroll.Offset = _scroll.Offset.WithY(y);
        Follow();
    }

    /// <summary>Settles the lifted row into its slot, then applies the move (or puts everything back).</summary>
    private void Drop(bool commit)
    {
        if (_lifted is not { } item || _dropping) return;
        _autoScroll?.Stop();
        _autoScroll = null;
        if (!commit && _to != _from)
        {
            _to = _from;
            foreach (var c in List.GetRealizedContainers()) if (c != item) c.RenderTransform = TranslateY(0);
        }
        _dropping = true;
        item.Transitions = Slide();
        item.RenderTransform = TranslateY((_to - _from) * _pitch);
        DispatcherTimer.RunOnce(() =>
        {
            int from = _from, to = _to;
            // Apply the move and rebuild the rows before the next frame, so nothing jumps back for a moment.
            if (from != to && DataContext is QueueViewModel vm) vm.Move(from, to);
            foreach (var c in List.GetRealizedContainers()) Reset(c);
            Reset(item);
            List.Classes.Remove("dragging");
            _lifted = null;
            _dropping = false;
            if (from != to) List.SelectedIndex = to;
        }, SlideTime);
    }

    private int ShiftOf(int index)
    {
        if (_from < _to && index > _from && index <= _to) return -1 * (int)Math.Round(_pitch);
        if (_to < _from && index >= _to && index < _from) return (int)Math.Round(_pitch);
        return 0;
    }

    private double ScrollY => _scroll?.Offset.Y ?? 0;

    /// <summary>Distance from one row to the next, spacing included.</summary>
    private double Pitch(int index, Control item)
    {
        var next = List.ContainerFromIndex(index + 1) ?? List.ContainerFromIndex(index - 1);
        if (next is not null && next.TranslatePoint(default, item) is { } p && Math.Abs(p.Y) > 1) return Math.Abs(p.Y);
        return Math.Max(1, item.Bounds.Height);
    }

    private static void Reset(Control c)
    {
        c.Transitions = null;
        c.RenderTransform = null;
        c.ZIndex = 0;
        c.Classes.Remove("lifted");
    }

    private static Transitions Slide() =>
        [new TransformOperationsTransition { Property = RenderTransformProperty, Duration = SlideTime, Easing = new CubicEaseOut() }];

    private static ITransform TranslateY(double y)
    {
        var b = TransformOperations.CreateBuilder(1);
        b.AppendTranslate(0, y);
        return b.Build();
    }
}
