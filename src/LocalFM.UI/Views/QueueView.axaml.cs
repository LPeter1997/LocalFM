using Avalonia.Controls;
using Avalonia.Input;
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
    }
}
