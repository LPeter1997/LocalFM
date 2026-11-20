using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using LocalFM.App;
using LocalFM.UI.ViewModels;

namespace LocalFM.UI.Views;

/// <summary>A small always-on-top player. Expanding (or closing) it brings the full window back.</summary>
public partial class MiniPlayerWindow : Window
{
    public MiniPlayerWindow()
    {
        InitializeComponent();
        ExpandButton.Click += (_, _) => Expand?.Invoke();
        // Drag anywhere that is not a button to move the window; double-click goes back to the full window.
        WindowChrome.MakeBorderless(this);
        WindowChrome.Outline(this, Root);
        WindowChrome.TitleBar(this, Root, doubleClick: () => Expand?.Invoke());
        // The progress line seeks: press/drag previews the spot, release seeks there.
        // Handling the press also keeps the window-drag from starting.
        SeekArea.PointerPressed += (_, e) =>
        {
            if (DataContext is not NowPlayingViewModel vm || !vm.HasOwnTrack) return;
            if (!e.GetCurrentPoint(SeekArea).Properties.IsLeftButtonPressed) return;
            vm.IsSeeking = true;
            e.Pointer.Capture(SeekArea);
            vm.PreviewSeek(SeekSeconds(vm, e));
            e.Handled = true;
        };
        SeekArea.PointerMoved += (_, e) =>
        {
            if (DataContext is NowPlayingViewModel { IsSeeking: true } vm && e.Pointer.Captured == SeekArea)
                vm.PreviewSeek(SeekSeconds(vm, e));
        };
        SeekArea.PointerReleased += (_, e) =>
        {
            if (DataContext is NowPlayingViewModel { IsSeeking: true } vm) vm.PreviewSeek(SeekSeconds(vm, e));
            CommitSeek();
            e.Pointer.Capture(null);
            e.Handled = true;
        };
        SeekArea.PointerCaptureLost += (_, _) => CommitSeek();
        // Scroll anywhere on the miniplayer to change the volume.
        PointerWheelChanged += (_, e) =>
        {
            if (DataContext is NowPlayingViewModel vm) { vm.NudgeVolume(e.Delta.Y * 5); e.Handled = true; }
        };
        KeyDown += (_, e) =>
        {
            if (DataContext is not NowPlayingViewModel vm) return;
            switch (e.Key)
            {
                case Key.Space or Key.MediaPlayPause: vm.PlayPauseCommand.Execute($"miniplayer key {e.Key}"); break;
                case Key.MediaNextTrack: vm.NextCommand.Execute("miniplayer media key"); break;
                case Key.MediaPreviousTrack: vm.PreviousCommand.Execute("miniplayer media key"); break;
                default: return;
            }
            e.Handled = true;
        };
    }

    /// <summary>Feeds the background visualizer; it follows the mode chosen in the now-playing view and runs only while shown.</summary>
    public void AttachVisualizer(PlayerService player, MainViewModel main)
    {
        Backdrop.Source = player;
        Backdrop.Mode = main.VisualizerMode;
        main.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.VisualizerMode)) Backdrop.Mode = main.VisualizerMode;
        };
        Backdrop.Active = IsVisible;
        PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty) Backdrop.Active = IsVisible; };
    }

    private double SeekSeconds(NowPlayingViewModel vm, PointerEventArgs e)
    {
        var w = SeekArea.Bounds.Width;
        return w <= 0 ? 0 : Math.Clamp(e.GetPosition(SeekArea).X / w, 0, 1) * vm.Duration;
    }

    private void CommitSeek()
    {
        if (DataContext is not NowPlayingViewModel { IsSeeking: true } vm) return;
        vm.IsSeeking = false;
        vm.CommitSeek(vm.Position, "miniplayer seek bar");
    }

    /// <summary>Raised when the person wants the full window back.</summary>
    public event Action? Expand;

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        // Closing the miniplayer by hand means "show me the app", not "quit".
        if (!e.IsProgrammatic) Expand?.Invoke();
    }
}
