using Avalonia.Controls;
using LocalFM.UI.ViewModels;

namespace LocalFM.UI.Views;

public partial class AlbumDetailView : UserControl
{
    public AlbumDetailView()
    {
        InitializeComponent();
        TrackMenu.Attach(List, () => (DataContext as AlbumDetailViewModel)?.Actions,
            t => (DataContext as AlbumDetailViewModel)?.PlayFromCommand.Execute(t));
    }
}
