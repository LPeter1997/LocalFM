using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace LocalFM.UI.Views;

/// <summary>
/// Title-bar behaviour for our borderless windows, the same on every OS: drag the header to move the window,
/// double-click it to maximize or restore, and grab any edge or corner to resize.
/// </summary>
public static class WindowChrome
{
    /// <summary>Turns off the system title bar; the window keeps its native border, shadow and snapping where the OS has them.</summary>
    public static void MakeBorderless(Window w)
    {
        w.ExtendClientAreaToDecorationsHint = true;
        w.WindowDecorations = WindowDecorations.BorderOnly;
        KeepSizeAcrossMonitors(w);
    }

    /// <summary>
    /// Dragging a window onto a monitor with different scaling (e.g. a 125% laptop to a 100% monitor) must keep its
    /// size in DIPs. During a drag, Windows announces the new DPI (WM_GETDPISCALEDSIZE), resizes the window to the
    /// new pixel size (WM_SIZE) and only then switches the DPI (WM_DPICHANGED). Avalonia (12.1) reads that WM_SIZE
    /// with the old scaling and keeps it, so the window shrinks or grows by the scaling ratio while its content is
    /// laid out for the old size and gets cut off. Skipping WM_SIZE between the announcement and the switch leaves the
    /// size in DIPs untouched; the DPI change then fits the window to it.
    /// </summary>
    private static void KeepSizeAcrossMonitors(Window w)
    {
        if (!OperatingSystem.IsWindows()) return;
        bool dpiChanging = false;
        Win32Properties.AddWndProcHookCallback(w, (nint _, uint msg, nint wParam, nint _, ref bool handled) =>
        {
            const uint WM_SIZE = 0x0005, WM_EXITSIZEMOVE = 0x0232, WM_DPICHANGED = 0x02E0, WM_GETDPISCALEDSIZE = 0x02E4;
            switch (msg)
            {
                case WM_GETDPISCALEDSIZE:
                    dpiChanging = Math.Abs(wParam / 96.0 - w.RenderScaling) > 0.001;
                    break;
                case WM_DPICHANGED or WM_EXITSIZEMOVE:
                    dpiChanging = false;
                    break;
                case WM_SIZE when dpiChanging && wParam == 0: // SIZE_RESTORED
                    handled = true;
                    break;
            }
            return 0;
        });
    }

    /// <summary>
    /// On Linux a window without decorations gets no border or shadow from the window manager, so dark windows
    /// melt into dark desktops; <paramref name="frame"/> draws a 1 px outline there instead (none while maximized).
    /// </summary>
    public static void Outline(Window w, Border frame)
    {
        if (!OperatingSystem.IsLinux()) return;
        frame.BorderBrush = (IBrush?)Application.Current?.FindResource("Line");
        void Update() => frame.BorderThickness = new Thickness(w.WindowState == WindowState.Normal ? 1 : 0);
        w.PropertyChanged += (_, e) => { if (e.Property == Window.WindowStateProperty) Update(); };
        Update();
    }

    public static void ToggleMaximized(Window w) =>
        w.WindowState = w.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    /// <summary>
    /// Makes <paramref name="area"/> act as a title bar. Presses on buttons, text boxes and the like never reach it,
    /// because they handle their own. The move starts once the pointer actually moves, so a double-click stays a double-click.
    /// </summary>
    public static void TitleBar(Window w, Control area, Action? doubleClick = null)
    {
        PointerPressedEventArgs? press = null;
        Point start = default;
        area.PointerPressed += (_, e) =>
        {
            press = null;
            if (!e.GetCurrentPoint(area).Properties.IsLeftButtonPressed) return;
            if (e.ClickCount == 2)
            {
                if (doubleClick is not null) doubleClick();
                else if (w.CanResize) ToggleMaximized(w);
                e.Handled = true;
                return;
            }
            press = e;
            start = e.GetPosition(area);
        };
        area.PointerMoved += (_, e) =>
        {
            if (press is null) return;
            var p = e.GetPosition(area);
            if (Math.Abs(p.X - start.X) < 4 && Math.Abs(p.Y - start.Y) < 4) return;
            var args = press;
            press = null;
            w.BeginMoveDrag(args);
        };
        area.PointerReleased += (_, _) => press = null;
    }

    /// <summary>
    /// Adds thin invisible grips along the edges of <paramref name="host"/> (the window's root panel) for resizing.
    /// They hide while the window is maximized or full screen.
    /// </summary>
    public static void ResizeGrips(Window w, Panel host, double edge = 5, double corner = 12)
    {
        var grips = new List<Control>();
        void Add(WindowEdge which, HorizontalAlignment h, VerticalAlignment v, double width, double height, StandardCursorType cursor)
        {
            var grip = new Border
            {
                Background = Brushes.Transparent,
                HorizontalAlignment = h,
                VerticalAlignment = v,
                Width = width,
                Height = height,
                Cursor = new Cursor(cursor),
                ZIndex = 100,
            };
            grip.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed) return;
                w.BeginResizeDrag(which, e);
                e.Handled = true;
            };
            grips.Add(grip);
            host.Children.Add(grip);
        }
        const HorizontalAlignment L = HorizontalAlignment.Left, R = HorizontalAlignment.Right, HS = HorizontalAlignment.Stretch;
        const VerticalAlignment T = VerticalAlignment.Top, B = VerticalAlignment.Bottom, VS = VerticalAlignment.Stretch;
        Add(WindowEdge.North, HS, T, double.NaN, edge, StandardCursorType.TopSide);
        Add(WindowEdge.South, HS, B, double.NaN, edge, StandardCursorType.BottomSide);
        Add(WindowEdge.West, L, VS, edge, double.NaN, StandardCursorType.LeftSide);
        Add(WindowEdge.East, R, VS, edge, double.NaN, StandardCursorType.RightSide);
        Add(WindowEdge.NorthWest, L, T, corner, corner, StandardCursorType.TopLeftCorner);
        Add(WindowEdge.NorthEast, R, T, corner, corner, StandardCursorType.TopRightCorner);
        Add(WindowEdge.SouthWest, L, B, corner, corner, StandardCursorType.BottomLeftCorner);
        Add(WindowEdge.SouthEast, R, B, corner, corner, StandardCursorType.BottomRightCorner);

        void Update()
        {
            bool on = w.CanResize && w.WindowState == WindowState.Normal;
            foreach (var g in grips) g.IsVisible = on;
        }
        w.PropertyChanged += (_, e) =>
        {
            if (e.Property == Window.WindowStateProperty || e.Property == Window.CanResizeProperty) Update();
        };
        Update();
    }
}
