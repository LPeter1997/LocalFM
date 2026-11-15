using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using LocalFM.UI.ViewModels;

namespace LocalFM.UI.Views;

public partial class AlbumDetailView : UserControl
{
    private AlbumDetailViewModel? _vm;

    public AlbumDetailView()
    {
        InitializeComponent();
        TrackMenu.Attach(List, () => (DataContext as AlbumDetailViewModel)?.Actions,
            t => (DataContext as AlbumDetailViewModel)?.PlayFromCommand.Execute(t));

        // Move panel: Enter moves, Esc closes, arrow keys walk the results from the search box, double-click picks.
        MoveSearch.KeyDown += (_, e) => MoveKey(e, fromSearch: true);
        MoveResults.KeyDown += (_, e) => MoveKey(e, fromSearch: false);
        MoveResults.DoubleTapped += (_, _) => _vm?.Move?.ApplyCommand.Execute(null);
        MoveOnline.KeyDown += (_, e) => MoveKey(e, fromSearch: false);
        MoveOnline.DoubleTapped += (_, _) => _vm?.Move?.ApplyCommand.Execute(null);
        NewAlbumBox.KeyDown += (_, e) => { if (e.Key == Key.Escape) { _vm?.Move?.CancelCommand.Execute(null); e.Handled = true; } };
        GuessList.DoubleTapped += (_, _) => _vm?.Match?.ApplyGuessCommand.Execute(null);

        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
            _vm = DataContext as AlbumDetailViewModel;
            if (_vm is not null) _vm.PropertyChanged += OnVmChanged;
            FocusMoveWhenOpen();
        };
    }

    private void OnVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AlbumDetailViewModel.Move)) FocusMoveWhenOpen();
    }

    private void FocusMoveWhenOpen()
    {
        if (_vm?.Move is null) return;
        Dispatcher.UIThread.Post(() => { MoveSearch.Focus(); MoveSearch.SelectAll(); }, DispatcherPriority.Loaded);
    }

    private void MoveKey(KeyEventArgs e, bool fromSearch)
    {
        if (_vm?.Move is not { } move) return;
        switch (e.Key)
        {
            case Key.Enter:
                move.ApplyCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Escape:
                move.CancelCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Down or Key.Up when fromSearch && move.Results.Count > 0:
                int i = move.Selected is { } s ? move.Results.IndexOf(s) : -1;
                i = Math.Clamp(i + (e.Key == Key.Down ? 1 : -1), 0, move.Results.Count - 1);
                move.Selected = move.Results[i];
                MoveResults.ScrollIntoView(i);
                e.Handled = true;
                break;
        }
    }
}
