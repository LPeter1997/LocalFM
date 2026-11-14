using Avalonia;
using Avalonia.Controls;

namespace LocalFM.UI.Views;

/// <summary>Minimize, maximize/restore and close buttons for a window without system decorations.</summary>
public partial class CaptionButtons : UserControl
{
    private Window? _window;

    public CaptionButtons()
    {
        InitializeComponent();
        MinimizeButton.Click += (_, _) => { if (_window is { } w) w.WindowState = WindowState.Minimized; };
        MaximizeButton.Click += (_, _) => { if (_window is { } w) WindowChrome.ToggleMaximized(w); };
        CloseButton.Click += (_, _) => _window?.Close();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _window = TopLevel.GetTopLevel(this) as Window;
        if (_window is null) return;
        _window.PropertyChanged += OnWindowPropertyChanged;
        Update();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_window is not null) _window.PropertyChanged -= OnWindowPropertyChanged;
        _window = null;
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty || e.Property == Window.CanResizeProperty) Update();
    }

    private void Update()
    {
        if (_window is null) return;
        bool maximized = _window.WindowState == WindowState.Maximized;
        MaximizeButton.IsVisible = _window.CanResize;
        MaximizeIcon.IsVisible = !maximized;
        RestoreIcon.IsVisible = maximized;
        ToolTip.SetTip(MaximizeButton, maximized ? "Restore" : "Maximize");
    }
}
