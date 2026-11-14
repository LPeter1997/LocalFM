using Avalonia.Controls;
using Avalonia.Input;
using LocalFM.Library;
using LocalFM.UI.ViewModels;

namespace LocalFM.UI.Views;

public partial class ArtistsView : UserControl
{
    public ArtistsView()
    {
        InitializeComponent();
        List.Tapped += (_, _) => Open();
        List.KeyDown += (_, e) => { if (e.Key == Key.Enter) Open(); };
    }

    private void Open()
    {
        if (List.SelectedItem is ArtistSummary a && DataContext is ArtistsViewModel vm)
        {
            List.SelectedItem = null;
            vm.OpenCommand.Execute(a);
        }
    }
}
