using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using LocalFM.Library;

namespace LocalFM.UI.ViewModels;

/// <summary>Per-track context actions shared by every list (bound from row context menus).</summary>
public sealed partial class TrackActions(MainViewModel main)
{
    public ObservableCollection<PlaylistSummary> Playlists => main.PlaylistList;

    /// <summary>Edit and move are offered (not on a phone).</summary>
    public bool CanEdit => main.Capabilities.Editing;

    [RelayCommand] private void PlayNext(TrackInfo t) => main.Host.Player.PlayNext([t]);
    [RelayCommand] private void Enqueue(TrackInfo t) => main.Host.Player.Enqueue([t]);
    [RelayCommand] private void ShowAlbum(TrackInfo t) => main.ShowAlbumOf(t);
    [RelayCommand] private void Edit(TrackInfo t) => main.EditAlbum(t.ReleaseId, t.TrackId);
    [RelayCommand] private void Move(TrackInfo t) => main.MoveTrack(t);
    public void MoveMany(IReadOnlyList<TrackInfo> tracks) => main.MoveTracks(tracks);

    public async Task AddToPlaylist(TrackInfo t, PlaylistSummary p)
    {
        await main.Host.Playlists.AppendAsync(p.Id, [t.RecordingId]);
        await main.RefreshPlaylistsAsync();
        main.Notify($"Added “{t.Title}” to {p.Name}");
    }
}
