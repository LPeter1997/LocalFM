using Avalonia.Controls;
using LocalFM.UI.ViewModels;

namespace LocalFM.UI.Views;

public partial class TracksView : UserControl
{
    public TracksView()
    {
        InitializeComponent();
        TrackMenu.Attach(List, () => (DataContext as TracksViewModel)?.Actions,
            t => (DataContext as TracksViewModel)?.PlayFromCommand.Execute(t));
    }
}
