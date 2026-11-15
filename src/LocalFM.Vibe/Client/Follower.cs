using LocalFM.App;
using LocalFM.Core;
using LocalFM.Library;
using LocalFM.Library.Enrich;
using LocalFM.Metadata;
using LocalFM.Native;
using LocalFM.Playback;
using LocalFM.Vibe.Protocol;

namespace LocalFM.Vibe.Client;

/// <summary>
/// Plays a host's vibe on this machine: puts our own queue aside (<see cref="PlayerService.EnterFollowMode"/>),
/// mirrors the host's window into the queue (so the whole UI shows it), and keeps our engine at the host's
/// position. Tracks change gaplessly in our own engine; the follower only steps in when we are on the wrong
/// track or drift too far, and then jumps to where the host is "now" (computed on the engine thread).
/// </summary>
public sealed class Follower : IFollowSource, IRangeSource, IAsyncDisposable
{
    private const string Area = "Vibe";
    /// <summary>Drift beyond this is corrected with a seek (a short soft blip).</summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromMilliseconds(70);
    /// <summary>Near a track change, either side may already be on the other track for a moment.</summary>
    private static readonly TimeSpan TransitionGrace = TimeSpan.FromSeconds(1.5);
    /// <summary>After a correction, the next one waits so a slow open or seek is not "corrected" again.</summary>
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(15);

    private VibeClient _client;
    private QueueWindow? _lastWindow;
    private readonly PlayerService _player;
    private readonly RemoteMediaCache _cache;
    private readonly Dictionary<Ulid, Entry> _entries = new();
    private readonly Dictionary<string, Ulid> _idsByKey = new(); // stable fake library ids (recording, release …)
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _poke = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly string _coverDir;
    private readonly HashSet<string> _coversWanted = new();
    private DateTime _lastCorrection = DateTime.MinValue;
    private (Ulid Item, DateTime At)? _starting;

    private sealed record Entry(TrackOffer Offer, QueueItem Item, Func<int, Decoder> Open);

    public Follower(VibeClient client, PlayerService player, string cacheDir)
    {
        _client = client;
        _player = player;
        _cache = new RemoteMediaCache(cacheDir, this);
        _coverDir = Path.Combine(cacheDir, "covers");
        Directory.CreateDirectory(_coverDir);
        client.WindowChanged += Poke;
        client.TimelineChanged += Poke;
        player.EnterFollowMode(this);
        _loop = Task.Run(Loop);
    }

    public string HostName => _client.HostName;

    /// <summary>Continues with a new connection to the same host (after the old one broke). Playback goes on
    /// from what is cached meanwhile; item ids are the host's, so nothing restarts.</summary>
    public void Reattach(VibeClient client)
    {
        var old = _client;
        old.WindowChanged -= Poke;
        old.TimelineChanged -= Poke;
        _client = client;
        client.WindowChanged += Poke;
        client.TimelineChanged += Poke;
        Poke();
    }

    ValueTask<int> IRangeSource.ReadAsync(string mediaId, long offset, Memory<byte> buffer, CancellationToken ct) =>
        _client.ReadAsync(mediaId, offset, buffer, ct);
    /// <summary>How far our speaker is from the host's (positive: we are late), when known.</summary>
    public TimeSpan? LastError { get; private set; }

    private void Poke() => _poke.Release();

    private async Task Loop()
    {
        var ct = _cts.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await _poke.WaitAsync(TimeSpan.FromMilliseconds(250), ct).ConfigureAwait(false);
                while (_poke.CurrentCount > 0) _poke.Wait(0);
                try { Tick(); }
                catch (Exception ex) { Log.Warn(Area, "Following the host failed", ex); }
            }
        }
        catch (OperationCanceledException) { }
    }

    // ------------------------------------------------------------------ queue

    private List<Entry> SyncWindow()
    {
        // A fresh connection has no window yet: keep the last one so what follows stays queued.
        if (_client.Window is { } w) _lastWindow = w;
        var window = _lastWindow?.Items ?? [];
        var list = new List<Entry>();
        lock (_gate)
        {
            foreach (var o in window)
            {
                if (!Ulid.TryParse(o.ItemId, out var id)) continue;
                string? cover = Cover(o);
                if (!_entries.TryGetValue(id, out var e) || e.Offer != o || e.Item.Track.CoverPath != cover)
                {
                    _cache.Add(o.MediaId, o.Size);
                    e = new Entry(o, new QueueItem(id, ToTrack(o)), rate => Decoder.Open(_cache.Add(o.MediaId, o.Size).OpenRead(), rate));
                    _entries[id] = e;
                }
                list.Add(e);
            }
            // Keep the item we still hear in the queue (the host may already be past it), so its "next" holds.
            if (_player.AudibleItem is { } audible && list.All(e => e.Item.Id != audible) && _entries.TryGetValue(audible, out var playing))
                list.Insert(0, playing);
            var keep = list.Select(e => e.Item.Id).ToHashSet();
            foreach (var stale in _entries.Keys.Where(k => !keep.Contains(k)).ToList()) _entries.Remove(stale);
        }
        _cache.SetPriority(list.Select(e => e.Offer.MediaId).Distinct().ToList()); // what we hear first, then what follows
        _player.FollowSetQueue(list.Select(e => e.Item).ToList());
        return list;
    }

    private TrackInfo ToTrack(TrackOffer o) => new(
        TrackId: StableId("track:" + o.MediaId),
        RecordingId: StableId("rec:" + o.MediaId),
        FileId: StableId("file:" + o.MediaId),
        Path: "vibe://" + o.MediaId,
        Title: o.Title,
        Artist: o.Artist,
        Album: o.Album,
        AlbumArtist: o.AlbumArtist,
        ReleaseId: StableId("rel:" + o.Album + "\n" + o.AlbumArtist),
        Date: o.Date,
        Disc: o.Disc,
        Position: o.Position,
        DurationMs: o.DurationMs,
        CoverPath: Cover(o),
        Codec: o.Codec,
        TrackGainDb: o.TrackGainDb,
        TrackPeak: o.TrackPeak,
        AlbumGainDb: o.AlbumGainDb,
        AlbumPeak: o.AlbumPeak);

    /// <summary>The downloaded cover, or null (asking for it in the background; the queue updates when it is in).</summary>
    private string? Cover(TrackOffer o)
    {
        if (o.CoverId is not { } id || o.CoverSize <= 0 || o.CoverSize > 32 << 20) return null;
        string path = Path.Combine(_coverDir, id);
        if (File.Exists(path)) return path;
        if (_coversWanted.Add(id))
            _ = Task.Run(async () =>
            {
                try
                {
                    await _client.DownloadAsync(id, o.CoverSize, path, _cts.Token).ConfigureAwait(false);
                    Poke();
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { Log.Debug(Area, $"Cover {id}: {ex.Message}"); }
                catch (OperationCanceledException) { }
                finally { lock (_gate) _coversWanted.Remove(id); }
            });
        return null;
    }

    /// <summary>Lyrics for a track of the vibe, from the host.</summary>
    public async Task<LyricsView?> LyricsAsync(TrackInfo track, CancellationToken ct)
    {
        string? itemId;
        lock (_gate) itemId = _entries.Values.FirstOrDefault(e => e.Item.Track.TrackId == track.TrackId)?.Item.Id.ToString();
        if (itemId is null) return null;
        var reply = await _client.LyricsAsync(itemId, ct).ConfigureAwait(false);
        if (reply is not { Content: { } content, Format: { } format }) return null;
        var data = new LyricsData(reply.Provider ?? "host", MetadataService.FormatOf(format), content);
        return new LyricsView(data, Lrc.Parse(content, data.Format));
    }

    /// <summary>Who is in the vibe, the host first.</summary>
    public IReadOnlyList<string> People => _client.People;

    private Ulid StableId(string key)
    {
        if (!_idsByKey.TryGetValue(key, out var id)) _idsByKey[key] = id = Ulid.NewUlid();
        return id;
    }

    public TrackHandle Handle(QueueItem item, float gain)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(item.Id, out var e))
                return new TrackHandle(item.Track.Path, item.Id, gain); // gone from the window: fails to open, harmlessly
            return new TrackHandle(item.Track.Path, item.Id, gain, e.Offer.JoinGapless, e.Open);
        }
    }

    // ------------------------------------------------------------------ timing

    /// <summary>Where the host's speaker is now in the host's current item.</summary>
    private TimeSpan HostHeardNow(Timeline t)
    {
        long pos = t.PositionUs;
        if (t.Playing) pos += _client.Clock.ToHost(VibeClock.NowUs) - t.HostTimeUs;
        return TimeSpan.FromMilliseconds(pos / 1000.0); // negative: still hearing the end of the previous track
    }

    /// <summary>The engine position that puts our speaker where the host's is (asked for at the last moment).</summary>
    private Func<TimeSpan> EngineTarget(Timeline t) => () => HostHeardNow(t) + _player.OutputLatency;

    private void Tick()
    {
        var entries = SyncWindow();
        if (_client.Timeline is not { } t) return;
        _player.FollowCrossfade(TimeSpan.FromSeconds(t.CrossfadeSeconds));

        Ulid? hostItem = Ulid.TryParse(t.ItemId, out var hi) ? hi : null;
        var audible = _player.AudibleItem;
        var state = _player.State;

        if (hostItem is null || entries.All(e => e.Item.Id != hostItem))
        {
            if (hostItem is null && state != PlaybackState.Stopped) _player.FollowStop();
            return; // nothing playing, or the window has not caught up with the timeline yet
        }

        if (!t.Playing)
        {
            LastError = null;
            if (state == PlaybackState.Playing && audible == hostItem) _player.FollowPause();
            else if (state != PlaybackState.Stopped && audible != hostItem) _player.FollowStop();
            return;
        }

        if (!_client.Clock.IsSynced) return; // a moment after joining

        var now = DateTime.UtcNow;
        if (audible != hostItem)
        {
            if (_starting is { } s && s.Item == hostItem && now - s.At < OpenTimeout) return; // still opening
            if (audible is { } a && InTransition(entries, a, hostItem.Value, t)) return;
            Log.Debug(Area, $"Starting {hostItem} at the host's position");
            _starting = (hostItem.Value, now);
            _lastCorrection = now;
            _player.FollowPlay(hostItem.Value, EngineTarget(t));
            return;
        }
        _starting = null;

        if (state == PlaybackState.Paused)
        {
            _player.FollowResume();
            _lastCorrection = now; // let the resume land, correct on a later tick
            return;
        }
        if (state != PlaybackState.Playing || !_player.PositionIsLive) return;

        var ours = _player.Position - _player.OutputLatency;
        var error = HostHeardNow(t) - ours;
        LastError = error;
        if (now - _lastCorrection < Cooldown || error.Duration() <= Tolerance) return;
        // Not in the last moments of a track: the gapless change is near, and a new track starts aligned anyway.
        if (_player.Duration is { } d && d - ours < TransitionGrace) return;
        Log.Debug(Area, $"Off by {error.TotalMilliseconds:F0} ms; jumping to the host's position");
        _lastCorrection = now;
        _player.FollowSeek(EngineTarget(t));
    }

    /// <summary>One of us is about to cross into the next track: let the engine do it gaplessly.</summary>
    private bool InTransition(List<Entry> entries, Ulid audible, Ulid hostItem, Timeline t)
    {
        int ia = entries.FindIndex(e => e.Item.Id == audible);
        int ih = entries.FindIndex(e => e.Item.Id == hostItem);
        if (ia < 0 || ih < 0) return false;
        if (ih == ia + 1 && _player.Duration is { } d) // we are behind: our track is ending
            return d - (_player.Position - _player.OutputLatency) < TransitionGrace;
        if (ia == ih + 1) // we are ahead: we just started the host's next track, or the host's track is ending
            return _player.Position - _player.OutputLatency < TransitionGrace
                || (entries[ih].Offer.DurationMs is { } ms && TimeSpan.FromMilliseconds(ms) - HostHeardNow(t) < TransitionGrace);
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        _client.WindowChanged -= Poke;
        _client.TimelineChanged -= Poke;
        _cts.Cancel();
        try { await _loop.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); } catch { }
        _player.LeaveFollowMode();
        _cache.Dispose();
    }
}
