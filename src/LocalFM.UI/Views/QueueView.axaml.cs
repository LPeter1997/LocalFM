using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
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
            if (List.SelectedItem is not QueueRow row || DataContext is not QueueViewModel vm) return;
            if (e.Key == Key.Delete) { vm.RemoveCommand.Execute(row); e.Handled = true; }
            else if (e.Key == Key.Enter) { vm.PlayCommand.Execute(row); e.Handled = true; }
        };
        SetUpDragToReorder();
    }

    // ------------------------------------------------------------------ drag to reorder

    private int? _dragFrom;
    private Point _dragStart;
    private bool _dragging;

    /// <summary>Press on a row and drag it up or down; the queue reorders live under the pointer.</summary>
    private void SetUpDragToReorder()
    {
        List.AddHandler(PointerPressedEvent, (_, e) =>
        {
            _dragFrom = null;
            if (!e.GetCurrentPoint(List).Properties.IsLeftButtonPressed) return;
            if (e.Source is Visual v && v.FindAncestorOfType<Button>(includeSelf: true) is not null) return; // row buttons
            if ((e.Source as Control)?.DataContext is not QueueRow row) return;
            _dragFrom = row.Index;
            _dragStart = e.GetPosition(List);
        }, RoutingStrategies.Tunnel, handledEventsToo: true);

        List.AddHandler(PointerMovedEvent, (_, e) =>
        {
            if (_dragFrom is not { } from || DataContext is not QueueViewModel vm) return;
            var p = e.GetPosition(List);
            if (!_dragging)
            {
                if (Math.Abs(p.Y - _dragStart.Y) < 6) return;
                _dragging = true;
                e.Pointer.Capture(List);
                List.Classes.Add("dragging");
            }
            int to = IndexAt(p.Y);
            if (to >= 0 && to != from)
            {
                vm.Move(from, to);
                _dragFrom = to;
            }
            e.Handled = true;
        }, RoutingStrategies.Tunnel);

        void End()
        {
            _dragFrom = null;
            if (!_dragging) return;
            _dragging = false;
            List.Classes.Remove("dragging");
        }
        List.AddHandler(PointerReleasedEvent, (_, e) => { if (_dragging) e.Pointer.Capture(null); End(); }, RoutingStrategies.Tunnel, handledEventsToo: true);
        List.AddHandler(PointerCaptureLostEvent, (_, _) => End(), RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    /// <summary>The row under a y position in the list (first or last row beyond the ends).</summary>
    private int IndexAt(double y)
    {
        int count = List.ItemCount, last = -1;
        for (int i = 0; i < count; i++)
        {
            if (List.ContainerFromIndex(i) is not Control c || c.TranslatePoint(default, List) is not { } top) continue;
            if (y < top.Y + c.Bounds.Height) return i;
            last = i;
        }
        return last;
    }
}
