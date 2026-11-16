using Avalonia.Controls;
using LocalFM.UI.ViewModels;

namespace LocalFM.UI.Views;

public partial class PlaylistsView : UserControl
{
    public PlaylistsView()
    {
        InitializeComponent();
        TrackMenu.Attach(List, () => (DataContext as PlaylistsViewModel)?.Actions,
            t => (DataContext as PlaylistsViewModel)?.PlayFromCommand.Execute(t),
            t => (DataContext as PlaylistsViewModel)?.RemoveTrackCommand.Execute(t), "Remove from playlist");
    }
}
