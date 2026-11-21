using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using LocalFM.UI.ViewModels;

namespace LocalFM.UI.Views;

/// <summary>
/// Snapshot mode (the ui-review skill): puts the app into the state the LOCALFM_SNAPSHOT_* settings describe, then
/// waits for it to settle. The head takes the picture. <paramref name="setting"/> reads a setting by its environment
/// variable name (Android passes intent extras under the same names).
/// </summary>
public static class SnapshotScript
{
    public static async Task RunAsync(MainViewModel vm, Control root, Func<string, string?> setting, Action? openMiniplayer = null)
    {
        if (setting("LOCALFM_SNAPSHOT_PAGE") is { Length: > 0 } page)
        {
            await Task.Delay(800);
            if (page is "album" or "match" && vm.Albums.Albums.FirstOrDefault() is { } a) vm.ShowAlbum(a, openMatch: page == "match");
            else if (page.StartsWith("album:", StringComparison.Ordinal)
                     && vm.Albums.Albums.FirstOrDefault(x => x.Title == page[6..]) is { } named) vm.ShowAlbum(named);
            else if (page.StartsWith("match:", StringComparison.Ordinal)
                     && vm.Albums.Albums.FirstOrDefault(x => x.Title == page[6..]) is { } toMatch) vm.ShowAlbum(toMatch, openMatch: true);
            else if (page.StartsWith("move:", StringComparison.Ordinal)
                     && vm.Albums.Albums.FirstOrDefault(x => x.Title == page[5..]) is { } moveFrom)
                vm.MoveTracks(vm.Host.Queries.AlbumTracks(moveFrom.ReleaseId).Take(2).ToList());
            else if (page == "tidy-albums" && vm.Nav.FirstOrDefault(n => n.Page is TidyViewModel) is { } tidy)
            {
                vm.Navigate(tidy);
                ((TidyViewModel)tidy.Page).Tab = 1;
            }
            else if (page == "move" && vm.Albums.Albums.FirstOrDefault() is { } m
                     && vm.Host.Queries.AlbumTracks(m.ReleaseId).FirstOrDefault() is { } first) vm.MoveTrack(first);
            else if (page == "edit" && vm.Albums.Albums.FirstOrDefault() is { } e) vm.EditAlbum(e.ReleaseId);
            else if (page == "mini") openMiniplayer?.Invoke();
            else if (page == "more") vm.ShowMoreCommand.Execute(null);
            else if (page.StartsWith("artist:", StringComparison.Ordinal)
                     && vm.Host.Queries.Artists(page[7..]).FirstOrDefault() is { } artist)
            {
                vm.ShowArtist(artist);
                if (vm.CurrentPage is AlbumsViewModel { Profile: { } profile }) await profile.LookUpCommand.ExecuteAsync(null);
            }
            else if (vm.Nav.FirstOrDefault(n => n.Name.StartsWith(page, StringComparison.OrdinalIgnoreCase)) is { } nav) vm.Navigate(nav);
        }
        // Import page states: a preview of <path>, optionally imported (into the snapshot home, never ~/Music).
        if (setting("LOCALFM_SNAPSHOT_IMPORT") is { Length: > 0 } importPath && vm.CurrentPage is ImportViewModel import)
        {
            await vm.Host.Import.SetImportFolderAsync(Path.Combine(vm.Host.Paths.Root, "imported"));
            await import.ResolveManyAsync([importPath]);
            if (setting("LOCALFM_SNAPSHOT_IMPORT_RUN") == "1")
            {
                await import.ImportSelectedCommand.ExecuteAsync(null);
                await Task.Delay(2500);
            }
        }
        // Not when playback already carries on (resumed after "Restart now").
        // "1" plays the first album; "all" every track (a queue long enough for background playback checks).
        if (setting("LOCALFM_SNAPSHOT_PLAY") is "1" or "all" && vm.Host.Player.State != LocalFM.Playback.PlaybackState.Playing)
        {
            await Task.Delay(500);
            if (setting("LOCALFM_SNAPSHOT_PLAY") == "all")
            {
                vm.Host.Player.Repeat = LocalFM.App.RepeatMode.All;
                vm.Host.Player.PlayTracks(vm.Host.Queries.Tracks(""));
            }
            else if (vm.Albums.Albums.FirstOrDefault() is { } first) vm.Albums.PlayCommand.Execute(first);
        }
        if (setting("LOCALFM_SNAPSHOT_LYRICS") == "1") vm.Lyrics.IsOpen = true;
        if (setting("LOCALFM_SNAPSHOT_UPDATE") is { Length: > 0 } update)
        {
            if (update is "quit" or "restart")
            {
                // A real start shows the installed version's notes, then the buttons are pressed.
                vm.Updater.ShowWhatsNewIfUpdated();
                await vm.Updater.CheckNowCommand.ExecuteAsync(null);
                Console.WriteLine($"update: {vm.Updater.CurrentVersion}, offered {vm.Updater.Available?.Version ?? "nothing"}, {vm.Host.Player.State}");
                if (vm.Updater.Available is not null)
                {
                    if (update == "quit") await vm.Updater.UpdateOnQuitCommand.ExecuteAsync(null);
                    else await vm.Updater.RestartNowCommand.ExecuteAsync(null);
                }
            }
            else if (update == "whatsnew")
            {
                vm.Updater.WhatsNew = LocalFM.UI.Updates.SnapshotUpdateService.SnapshotNotes;
                vm.Updater.WhatsNewOpen = true;
            }
            else
            {
                await vm.Updater.CheckNowCommand.ExecuteAsync(null);
                if (update == "downloading") { vm.Updater.IsDownloading = true; vm.Updater.Progress = 42; }
            }
        }
        if (setting("LOCALFM_SNAPSHOT_VIBE") is "1" or "leave" or "host")
        {
            vm.Vibe.IsOpen = true;
            if (setting("LOCALFM_SNAPSHOT_VIBE") == "host") await vm.Vibe.StartHostingCommand.ExecuteAsync(null);
            if (setting("LOCALFM_SNAPSHOT_VIBE") == "leave")
            {
                await Task.Delay(4000); // joined and playing
                vm.Vibe.StopCommand.Execute(null);
            }
        }
        // The library share on, a paired device and a PIN showing (Sources page).
        if (setting("LOCALFM_SNAPSHOT_SHARE") == "1")
        {
            await vm.Host.Share.SetEnabledAsync(true);
            if (vm.Host.Share.Devices.List.Count == 0) vm.Host.Share.Devices.Pair("Living room TV");
            vm.Host.Share.NewPin();
        }
        // Joins the vibe with this invite (Android has no command line for --vibe-join).
        if (setting("LOCALFM_SNAPSHOT_VIBE_JOIN") is { Length: > 0 } code)
        {
            vm.Vibe.IsOpen = true;
            vm.Vibe.JoinCode = code;
            await vm.Vibe.JoinCommand.ExecuteAsync(null);
        }
        if (setting("LOCALFM_SNAPSHOT_NOWPLAYING") is { Length: > 0 } np)
        {
            vm.NowPlayingOpen = true;
            if (Enum.TryParse<LocalFM.UI.Controls.VisualizerMode>(np, out var mode)) vm.VisualizerMode = mode;
        }
        int wait = int.TryParse(setting("LOCALFM_SNAPSHOT_WAIT"), out int w) ? w : 2500;
        await Task.Delay(wait); // let pages, covers and lookups finish
        if (double.TryParse(setting("LOCALFM_SNAPSHOT_SCROLL"), out double scroll)
            && root.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault(sv => sv.Extent.Height > sv.Viewport.Height + 1) is { } scroller)
        {
            scroller.Offset = new Vector(0, scroll);
            await Task.Delay(400);
        }
    }
}
