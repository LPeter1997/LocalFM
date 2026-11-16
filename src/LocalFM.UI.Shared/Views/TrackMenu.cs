using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using LocalFM.Library;
using LocalFM.UI.ViewModels;

namespace LocalFM.UI.Views;

/// <summary>Right-click (long-press) menu and double-click-to-play (tap on a touchscreen) for any ListBox of
/// <see cref="TrackInfo"/> rows.</summary>
public static class TrackMenu
{
    /// <summary>A finger's tap on a row, not on a button inside it: touch has no double-click, so a tap plays.</summary>
    public static bool IsTouchRowTap(TappedEventArgs e) =>
        e.Pointer.Type == PointerType.Touch
        && (e.Source as Control)?.FindAncestorOfType<Avalonia.Controls.Primitives.ToggleButton>(includeSelf: true) is null
        && (e.Source as Control)?.FindAncestorOfType<Button>(includeSelf: true) is null;

    public static void Attach(ListBox list, Func<TrackActions?> actions, Action<TrackInfo> play, Action<TrackInfo>? remove = null, string removeLabel = "Remove")
    {
        list.DoubleTapped += (_, e) =>
        {
            if (e.Pointer.Type != PointerType.Touch && Row(e.Source) is { } t) play(t);
        };
        list.Tapped += (_, e) =>
        {
            if (IsTouchRowTap(e) && Row(e.Source) is { } t) play(t);
        };
        list.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && list.SelectedItem is TrackInfo t) { play(t); e.Handled = true; }
        };
        list.ContextRequested += (_, e) =>
        {
            if (Row(e.Source) is not { } track || actions() is not { } a) return;
            var target = (e.Source as Control)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) ?? (Control)list;
            var menu = new ContextMenu();
            menu.Items.Add(Item("Play", () => play(track)));
            menu.Items.Add(Item("Play next", () => a.PlayNextCommand.Execute(track)));
            menu.Items.Add(Item("Add to queue", () => a.EnqueueCommand.Execute(track)));
            var playlists = new MenuItem { Header = "Add to playlist" };
            foreach (var p in a.Playlists.ToList())
                playlists.Items.Add(Item(p.Name, () => _ = a.AddToPlaylist(track, p)));
            if (playlists.Items.Count == 0) playlists.Items.Add(new MenuItem { Header = "(create one under Playlists)", IsEnabled = false });
            menu.Items.Add(playlists);
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Go to album", () => a.ShowAlbumCommand.Execute(track)));
            if (a.CanEdit)
            {
                menu.Items.Add(Item("Edit…", () => a.EditCommand.Execute(track)));
                // Right-clicking one of several selected songs moves them all (songs of one album).
                var selected = list.SelectedItems?.OfType<TrackInfo>().ToList() ?? [];
                if (selected.Count > 1 && selected.Contains(track) && selected.All(t => t.ReleaseId == track.ReleaseId))
                    menu.Items.Add(Item($"Move {selected.Count} songs to album…", () => a.MoveMany(selected)));
                else
                    menu.Items.Add(Item("Move to album…", () => a.MoveCommand.Execute(track)));
            }
            if (remove is not null) menu.Items.Add(Item(removeLabel, () => remove(track)));
            menu.Open(target);
            e.Handled = true;
        };
    }

    private static MenuItem Item(string header, Action click)
    {
        var mi = new MenuItem { Header = header };
        mi.Click += (_, _) => click();
        return mi;
    }

    private static TrackInfo? Row(object? source) =>
        (source as Control)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as TrackInfo;
}
