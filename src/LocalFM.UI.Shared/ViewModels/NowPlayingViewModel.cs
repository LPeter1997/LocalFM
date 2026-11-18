using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalFM.App;
using LocalFM.Playback;

namespace LocalFM.UI.ViewModels;

/// <summary>The bottom bar: current track, transport, seek and volume.</summary>
public sealed partial class NowPlayingViewModel : ObservableObject
{
    private readonly PlayerService _player;
    private readonly DispatcherTimer _timer;

    public NowPlayingViewModel(PlayerService player)
    {
        _player = player;
        _volume = player.Volume * 100;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) => Tick());
        _timer.Start();
        player.Changed += () => Dispatcher.UIThread.Post(Refresh);
        Refresh();
    }

    [ObservableProperty] private string _title = "Nothing playing";
    [ObservableProperty] private string? _artist;
    [ObservableProperty] private string? _album;
    [ObservableProperty] private string? _coverPath;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasOwnTrack))] private bool _hasTrack;
    /// <summary>Listening to someone's vibe: they control playback, and the track is not in our library.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasOwnTrack))] private bool _isFollowing;
    /// <summary>A track of our own library is playing (album/artist links, metadata lookup).</summary>
    public bool HasOwnTrack => HasTrack && !IsFollowing;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private double _position;
    [ObservableProperty] private double _duration = 1;
    [ObservableProperty] private string _positionText = "0:00";
    [ObservableProperty] private string _durationText = "0:00";
    [ObservableProperty] private double _volume;
    [ObservableProperty] private bool _shuffle;
    [ObservableProperty] private RepeatMode _repeat;

    /// <summary>True while the user drags the seek slider; ticks then leave the position alone.</summary>
    public bool IsSeeking { get; set; }

    public bool RepeatOn => Repeat != RepeatMode.Off;
    public bool RepeatOne => Repeat == RepeatMode.One;

    private double _beforeMute = 70;

    public bool Muted => Volume <= 0;

    partial void OnVolumeChanged(double value)
    {
        _player.Volume = (float)(value / 100);
        OnPropertyChanged(nameof(Muted));
    }

    /// <summary>Scroll wheel / keys: change the volume by <paramref name="delta"/> percent.</summary>
    public void NudgeVolume(double delta) => Volume = Math.Clamp(Volume + delta, 0, 100);

    [RelayCommand]
    private void ToggleMute()
    {
        if (Volume > 0) { _beforeMute = Volume; Volume = 0; }
        else Volume = _beforeMute > 0 ? _beforeMute : 50;
    }

    partial void OnRepeatChanged(RepeatMode value)
    {
        OnPropertyChanged(nameof(RepeatOn));
        OnPropertyChanged(nameof(RepeatOne));
    }

    public void CommitSeek(double seconds, string source = "seek bar")
    {
        _player.Seek(TimeSpan.FromSeconds(seconds), source);
        if (_player.IsFollowing) return; // the host's to do
        Position = seconds;
        PositionText = Converters.Time(TimeSpan.FromSeconds(seconds));
    }

    // The parameter says who asked, for the session log: buttons pass none, keys and the tray name themselves.
    [RelayCommand] private void PlayPause(string? source) => _player.PlayPause(source ?? "app");
    [RelayCommand] private void Next(string? source) => _player.Next(source ?? "app");
    [RelayCommand] private void Previous(string? source) => _player.Previous(source ?? "app");

    [RelayCommand]
    private void ToggleShuffle()
    {
        _player.SetShuffle(!Shuffle);
        Shuffle = _player.Queue.Shuffled;
    }

    [RelayCommand]
    private void CycleRepeat()
    {
        _player.Repeat = _player.Repeat switch
        {
            RepeatMode.Off => RepeatMode.All,
            RepeatMode.All => RepeatMode.One,
            _ => RepeatMode.Off,
        };
        Repeat = _player.Repeat;
    }

    public void SeekBy(double seconds, string source = "app")
    {
        if (!HasTrack) return;
        CommitSeek(Math.Clamp(Position + seconds, 0, Duration), source);
    }

    private void Refresh()
    {
        var cur = _player.Current?.Track;
        HasTrack = cur is not null;
        IsFollowing = _player.IsFollowing;
        Title = cur?.Title ?? "Nothing playing";
        Artist = cur?.Artist;
        Album = cur is null ? null : cur.Date is { Length: >= 4 } d ? $"{cur.Album} · {d[..4]}" : cur.Album;
        CoverPath = cur?.CoverPath;
        Repeat = _player.Repeat;
        Shuffle = _player.Queue.Shuffled;
        Tick();
    }

    private void Tick()
    {
        IsPlaying = _player.State == PlaybackState.Playing;
        var dur = _player.Duration ?? TimeSpan.Zero;
        Duration = Math.Max(1, dur.TotalSeconds);
        DurationText = Converters.Time(dur);
        if (IsSeeking) return;
        var pos = _player.Position;
        Position = Math.Min(pos.TotalSeconds, Duration);
        PositionText = Converters.Time(pos);
    }
}
