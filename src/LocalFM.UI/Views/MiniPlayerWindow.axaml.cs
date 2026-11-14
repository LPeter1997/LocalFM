using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
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
                case Key.Space or Key.MediaPlayPause: vm.PlayPauseCommand.Execute(null); break;
                case Key.MediaNextTrack: vm.NextCommand.Execute(null); break;
                case Key.MediaPreviousTrack: vm.PreviousCommand.Execute(null); break;
                default: return;
            }
            e.Handled = true;
        };
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
