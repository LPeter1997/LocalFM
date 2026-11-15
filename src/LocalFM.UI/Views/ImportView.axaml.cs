using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using LocalFM.UI.ViewModels;

namespace LocalFM.UI.Views;

public partial class ImportView : UserControl
{
    public ImportView()
    {
        InitializeComponent();
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => DropZone.Classes.Set("dragover", false));
        AddHandler(DragDrop.DropEvent, OnDrop);
        // A pasted link is looked up right away; typing still waits for Enter.
        InputBox.PastingFromClipboard += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (DataContext is ImportViewModel vm && vm.Input is { } text && text.Trim().StartsWith("http", StringComparison.OrdinalIgnoreCase))
                vm.ResolveCommand.Execute(null);
        }, DispatcherPriority.Background);
    }

    private static IReadOnlyList<string> LocalPaths(DragEventArgs e) =>
        e.DataTransfer.TryGetFiles() is { } items ? items.Select(i => i.TryGetLocalPath()).OfType<string>().ToList() : [];

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        bool files = e.DataTransfer.Contains(DataFormat.File);
        e.DragEffects = files ? DragDropEffects.Copy : DragDropEffects.None;
        DropZone.Classes.Set("dragover", files);
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        DropZone.Classes.Set("dragover", false);
        var paths = LocalPaths(e);
        if (paths.Count > 0 && DataContext is ImportViewModel vm) _ = vm.ResolveManyAsync(paths);
    }
}
