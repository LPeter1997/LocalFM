using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using LocalFM.Library;

namespace LocalFM.UI.ViewModels;

/// <summary>Per-track context actions shared by every list (bound from row context menus).</summary>
public sealed partial class TrackActions(MainViewModel main)
{
    public ObservableCollection<PlaylistSummary> Playlists => main.PlaylistList;

    [RelayCommand] private void PlayNext(TrackInfo t) => main.Host.Player.PlayNext([t]);
    [RelayCommand] private void Enqueue(TrackInfo t) => main.Host.Player.Enqueue([t]);
    [RelayCommand] private void ShowAlbum(TrackInfo t) => main.ShowAlbumOf(t);

    public async Task AddToPlaylist(TrackInfo t, PlaylistSummary p)
    {
        await main.Host.Playlists.AppendAsync(p.Id, [t.RecordingId]);
        await main.RefreshPlaylistsAsync();
        main.Notify($"Added “{t.Title}” to {p.Name}");
    }
}
