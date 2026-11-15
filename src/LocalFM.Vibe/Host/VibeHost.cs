using System.Net;
using System.Security.Cryptography;
using LocalFM.App;
using LocalFM.Core;
using LocalFM.Library;
using LocalFM.Library.Enrich;
using LocalFM.Metadata;
using LocalFM.Playback;
using LocalFM.Vibe.Net;
using LocalFM.Vibe.Protocol;
using Microsoft.Win32.SafeHandles;

namespace LocalFM.Vibe.Host;

public sealed record ListenerInfo(string Id, string Name);

/// <summary>
/// Shares this player with listeners. It only watches <see cref="PlayerService"/> (events and snapshots) and
/// reads files on the thread pool, so hosting never touches the playback path. Listeners can read only the
/// files in the current window (by opaque ids; paths never leave this machine).
/// </summary>
public sealed class VibeHost : IAsyncDisposable
{
    public const int WindowSize = 3;
    public const int MaxListeners = 8;
    private const string Area = "Vibe";
    private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(1);
    /// <summary>Lets a seek or state change land in the engine before we read the position.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(40);
    private const int RecentMedia = 16; // audio and covers of the window and what was in it just now

    private readonly PlayerService _player;
    private readonly VibeIdentity _identity;
    private readonly IVibeListener _listener;
    private readonly byte[] _secret = RandomNumberGenerator.GetBytes(16);
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _changed = new(0);
    private readonly Lock _gate = new();
    private readonly List<Peer> _peers = new();
    private readonly Dictionary<string, string> _mediaByPath = new();
    private readonly Dictionary<string, (string Path, long Size)> _media = new();
    private readonly List<string> _servable = new(); // media ids listeners may read, newest last
    private QueueWindow? _window;
    private string? _windowKey;
    private readonly Task _acceptLoop, _publishLoop;

    private readonly Func<TrackInfo, CancellationToken, Task<LyricsData?>>? _lyrics;

    /// <param name="lyrics">Looks up lyrics for listeners (null: listeners get none).</param>
    public VibeHost(PlayerService player, string hostName, int port = TlsTransport.DefaultPort, IReadOnlyList<IPAddress>? addresses = null,
        Func<TrackInfo, CancellationToken, Task<LyricsData?>>? lyrics = null)
    {
        _player = player;
        _lyrics = lyrics;
        HostName = hostName;
        _identity = VibeIdentity.Create();
        _listener = TlsTransport.Listen(_identity, port);
        _local = (addresses ?? Invite.LocalAddresses()).Select(a => new IPEndPoint(a, _listener.Port)).ToList();
        Invite = new Invite(_local, _identity.Fingerprint, _secret, hostName);
        player.Changed += Poke;
        ListenersChanged += SendPresence;
        player.Seeked += OnSeeked;
        _acceptLoop = Task.Run(AcceptLoop);
        _publishLoop = Task.Run(PublishLoop);
    }

    public string HostName { get; }
    private readonly List<IPEndPoint> _local;

    /// <summary>The current invite; it grows a public endpoint once the router maps a port.</summary>
    public Invite Invite { get; private set; }
    public event Action? InviteChanged;

    /// <summary>Adds (or, with null, removes) the router-mapped public endpoint to the invite.</summary>
    public void SetPublicEndpoint(IPEndPoint? endpoint)
    {
        Invite = Invite with { Endpoints = endpoint is null ? _local : [.. _local, endpoint] };
        InviteChanged?.Invoke();
    }
    public int Port => _listener.Port;

    public IReadOnlyList<ListenerInfo> Listeners
    {
        get { lock (_gate) return _peers.Where(p => p.Welcomed).Select(p => new ListenerInfo(p.Id, p.Name)).ToList(); }
    }

    /// <summary>Raised (on a background thread) when someone joins or leaves.</summary>
    public event Action? ListenersChanged;

    public async Task KickAsync(string listenerId)
    {
        Peer? p;
        lock (_gate) p = _peers.FirstOrDefault(x => x.Id == listenerId);
        if (p is null) return;
        try { await p.Control.SendAsync(new Bye("The host removed you from the vibe.")).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch { }
        await RemoveAsync(p).ConfigureAwait(false);
    }

    /// <summary>Tests: breaks every connection without a goodbye, as a network failure would.</summary>
    internal async Task DropConnectionsAsync()
    {
        List<Peer> peers;
        lock (_gate) peers = [.. _peers];
        foreach (var p in peers) await RemoveAsync(p).ConfigureAwait(false);
    }

    private void Poke() => _changed.Release();
    private void OnSeeked(TimeSpan _) => Poke();

    // ------------------------------------------------------------------ connections

    private async Task AcceptLoop()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            Stream s;
            try { s = await _listener.AcceptAsync(ct).ConfigureAwait(false); }
            catch (Exception) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { Log.Warn(Area, "Accept failed", ex); continue; }
            _ = Task.Run(() => Greet(s, ct));
        }
    }

    private async Task Greet(Stream stream, CancellationToken ct)
    {
        var wire = new Wire(stream);
        Peer? added = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            if (await wire.ReadMessageAsync(timeout.Token).ConfigureAwait(false) is not Hello hello)
                throw new InvalidDataException("No hello.");
            if (hello.Version != Wire.Version)
            {
                await wire.SendAsync(new Rejected(hello.Version < Wire.Version
                    ? "The host has a newer LocalFM. Update to join this vibe."
                    : "The host has an older LocalFM. Ask them to update."), ct).ConfigureAwait(false);
                throw new InvalidDataException($"Version {hello.Version}.");
            }

            if (hello.Role == ConnectionRole.Data)
            {
                Peer? owner;
                lock (_gate) owner = _peers.FirstOrDefault(p => p.Data is null && SecretEquals(p.DataToken, hello.Secret));
                if (owner is null) throw new InvalidDataException("Unknown data token.");
                owner.Data = wire;
                _ = Task.Run(() => ServeData(owner, wire));
                return;
            }

            if (!SecretEquals(Convert.ToBase64String(_secret), hello.Secret))
            {
                await wire.SendAsync(new Rejected("This invite is not valid any more."), ct).ConfigureAwait(false);
                throw new InvalidDataException("Wrong secret.");
            }
            var peer = new Peer(Ulid.NewUlid().ToString(), Clean(hello.Name), wire, Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)));
            lock (_gate)
            {
                if (_peers.Count >= MaxListeners) peer = null!;
                else _peers.Add(peer);
            }
            if (peer is null)
            {
                await wire.SendAsync(new Rejected("This vibe is full."), ct).ConfigureAwait(false);
                throw new InvalidDataException("Full.");
            }
            added = peer;
            await wire.SendAsync(new Welcome(peer.Id, peer.DataToken, HostName), ct).ConfigureAwait(false);
            if (Volatile.Read(ref _window) is { } w) await wire.SendAsync(w, ct).ConfigureAwait(false);
            await wire.SendAsync(CurrentTimeline(), ct).ConfigureAwait(false);
            peer.Welcomed = true;
            Log.Info(Area, $"{peer.Name} joined the vibe");
            ListenersChanged?.Invoke();
            await ServeControl(peer).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested) Log.Debug(Area, $"Connection closed: {ex.Message}");
            if (added is not null) await RemoveAsync(added).ConfigureAwait(false);
            else await wire.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static string Clean(string name)
    {
        name = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return name.Length == 0 ? "Listener" : name.Length > 40 ? name[..40] : name;
    }

    private static bool SecretEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(a), System.Text.Encoding.UTF8.GetBytes(b));

    private async Task ServeControl(Peer peer)
    {
        try
        {
            while (await peer.Control.ReadMessageAsync(peer.Cts.Token).ConfigureAwait(false) is { } m)
            {
                switch (m)
                {
                    case Ping ping:
                        await peer.Control.SendAsync(new Pong(ping.T0, VibeClock.NowUs), peer.Cts.Token).ConfigureAwait(false);
                        break;
                    case LyricsRequest req:
                        _ = Task.Run(() => AnswerLyrics(peer, req));
                        break;
                    case Bye:
                        return;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Log.Debug(Area, $"{peer.Name}: {ex.Message}"); }
        catch (OperationCanceledException) { }
        finally { await RemoveAsync(peer).ConfigureAwait(false); }
    }

    private async Task AnswerLyrics(Peer peer, LyricsRequest req)
    {
        LyricsData? data = null;
        try
        {
            var item = _player.Snapshot(out _).FirstOrDefault(i => i.Id.ToString() == req.ItemId);
            if (item is not null && _lyrics is not null)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(peer.Cts.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                data = await _lyrics(item.Track, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Log.Debug(Area, $"Lyrics for a listener: {ex.Message}"); }
        catch (OperationCanceledException) { }
        try
        {
            await peer.Control.SendAsync(data is null ? new LyricsReply(req.Request, null, null, null)
                : new LyricsReply(req.Request, data.Provider, MetadataService.FormatName(data.Format), data.Content), peer.Cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) { Log.Debug(Area, $"{peer.Name}: {ex.Message}"); }
    }

    private void SendPresence()
    {
        List<Peer> peers;
        lock (_gate) peers = _peers.Where(p => p.Welcomed).ToList();
        var msg = new Presence([HostName, .. peers.Select(p => p.Name)]);
        foreach (var p in peers) _ = SendQuietly(p, msg);
    }

    private static async Task SendQuietly(Peer p, Message m)
    {
        try { await p.Control.SendAsync(m, p.Cts.Token).ConfigureAwait(false); }
        catch (Exception ex) { Log.Debug(Area, $"{p.Name}: {ex.Message}"); }
    }

    private async Task ServeData(Peer peer, Wire wire)
    {
        var handles = new Dictionary<string, SafeFileHandle>();
        var buf = new byte[Wire.MaxRange];
        try
        {
            while (await wire.ReadAsync(peer.Cts.Token).ConfigureAwait(false) is { } f)
            {
                if (f.Type != Wire.FrameType.RangeRequest) throw new InvalidDataException("Expected a range request.");
                var (req, mediaId, offset, length) = f.AsRangeRequest();
                int status = 1, n = 0;
                string? path = null;
                lock (_gate) if (_servable.Contains(mediaId)) path = _media[mediaId].Path;
                if (path is not null && offset >= 0 && length is > 0 and <= Wire.MaxRange)
                {
                    try
                    {
                        if (!handles.TryGetValue(path, out var h))
                        {
                            if (handles.Count >= 4) { foreach (var old in handles.Values) old.Dispose(); handles.Clear(); }
                            handles[path] = h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        }
                        n = await RandomAccess.ReadAsync(h, buf.AsMemory(0, length), offset, peer.Cts.Token).ConfigureAwait(false);
                        status = 0;
                    }
                    catch (IOException ex) { Log.Debug(Area, $"Reading {path}: {ex.Message}"); }
                    catch (UnauthorizedAccessException ex) { Log.Debug(Area, $"Reading {path}: {ex.Message}"); }
                }
                await wire.SendRangeReplyAsync(req, status, buf.AsMemory(0, n), peer.Cts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Log.Debug(Area, $"{peer.Name} data: {ex.Message}"); }
        catch (OperationCanceledException) { }
        finally
        {
            foreach (var h in handles.Values) h.Dispose();
            await RemoveAsync(peer).ConfigureAwait(false);
        }
    }

    private async Task RemoveAsync(Peer peer)
    {
        bool removed;
        lock (_gate) removed = _peers.Remove(peer);
        if (!removed) return;
        peer.Cts.Cancel();
        await peer.Control.DisposeAsync().ConfigureAwait(false);
        if (peer.Data is { } d) await d.DisposeAsync().ConfigureAwait(false);
        if (peer.Welcomed)
        {
            Log.Info(Area, $"{peer.Name} left the vibe");
            ListenersChanged?.Invoke();
        }
    }

    // ------------------------------------------------------------------ publishing

    private async Task PublishLoop()
    {
        var ct = _cts.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                bool poked = await _changed.WaitAsync(Heartbeat, ct).ConfigureAwait(false);
                if (poked)
                {
                    await Task.Delay(Settle, ct).ConfigureAwait(false);
                    while (_changed.CurrentCount > 0) _changed.Wait(0);
                }
                // Right after a start or seek the position holds still until the device takes the new audio;
                // a timeline from then would put listeners ahead.
                for (int i = 0; i < 20 && !_player.PositionIsLive; i++) await Task.Delay(10, ct).ConfigureAwait(false);
                try { await PublishAsync(ct).ConfigureAwait(false); }
                catch (Exception ex) when (!ct.IsCancellationRequested) { Log.Warn(Area, "Publishing failed", ex); }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task PublishAsync(CancellationToken ct)
    {
        var window = BuildWindow();
        string key = string.Join("|", window.Items.Select(i => i.ItemId + i.MediaId + i.JoinGapless + i.Title + i.Artist));
        bool windowChanged = key != _windowKey;
        if (windowChanged)
        {
            _windowKey = key;
            Volatile.Write(ref _window, window);
        }
        var timeline = CurrentTimeline();
        List<Peer> peers;
        lock (_gate) peers = _peers.Where(p => p.Welcomed).ToList();
        foreach (var p in peers)
        {
            try
            {
                if (windowChanged) await p.Control.SendAsync(window, ct).ConfigureAwait(false);
                await p.Control.SendAsync(timeline, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Log.Debug(Area, $"{p.Name}: {ex.Message}");
                _ = RemoveAsync(p);
            }
        }
    }

    private Timeline CurrentTimeline()
    {
        var current = _player.Current;
        // Negative for the first moments of a track: the speaker still plays the end of the previous one.
        var heard = _player.Position - _player.OutputLatency;
        long now = VibeClock.NowUs;
        return new Timeline(current?.Id.ToString(), (long)(heard.TotalMilliseconds * 1000), now,
            _player.State == PlaybackState.Playing, _player.Crossfade.TotalSeconds);
    }

    /// <summary>The current item and what plays after it, in order (as the engine will play them).</summary>
    internal QueueWindow BuildWindow()
    {
        var items = _player.Snapshot(out int idx);
        var list = new List<QueueItem>();
        if (idx >= 0 && idx < items.Count)
        {
            list.Add(items[idx]);
            bool wrap = _player.Repeat == RepeatMode.All;
            for (int k = 1; list.Count < WindowSize && k < items.Count; k++)
            {
                int j = idx + k;
                if (j >= items.Count) { if (!wrap) break; j %= items.Count; }
                list.Add(items[j]);
            }
        }
        var offers = new List<TrackOffer>();
        for (int i = 0; i < list.Count; i++)
        {
            var t = list[i].Track;
            if (Media(t.Path) is not { } m) continue;
            var cover = t.CoverPath is { } cp ? Media(cp) : null;
            offers.Add(new TrackOffer
            {
                ItemId = list[i].Id.ToString(),
                MediaId = m.Id,
                Size = m.Size,
                Codec = t.Codec,
                Title = t.Title,
                Artist = t.Artist,
                Album = t.Album,
                AlbumArtist = t.AlbumArtist,
                Date = t.Date,
                Disc = t.Disc,
                Position = t.Position,
                DurationMs = t.DurationMs,
                TrackGainDb = t.TrackGainDb,
                TrackPeak = t.TrackPeak,
                AlbumGainDb = t.AlbumGainDb,
                AlbumPeak = t.AlbumPeak,
                CoverId = cover?.Id,
                CoverSize = cover?.Size ?? 0,
                JoinGapless = i > 0 && _player.GaplessAlbums && PlayerService.Continues(list[i - 1].Track, t),
            });
        }
        lock (_gate)
        {
            foreach (var id in offers.SelectMany(o => new[] { o.CoverId, o.MediaId }).OfType<string>())
            {
                _servable.Remove(id);
                _servable.Add(id);
            }
            while (_servable.Count > RecentMedia) _servable.RemoveAt(0);
        }
        return new QueueWindow(offers);
    }

    private (string Id, long Size)? Media(string path)
    {
        lock (_gate)
        {
            if (_mediaByPath.TryGetValue(path, out var id)) return (id, _media[id].Size);
        }
        long size;
        try { size = new FileInfo(path).Length; }
        catch (Exception ex) { Log.Debug(Area, $"Cannot share {path}: {ex.Message}"); return null; }
        lock (_gate)
        {
            if (_mediaByPath.TryGetValue(path, out var id)) return (id, _media[id].Size);
            id = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
            _mediaByPath[path] = id;
            _media[id] = (path, size);
            return (id, size);
        }
    }

    // ------------------------------------------------------------------ lifetime

    public async ValueTask DisposeAsync()
    {
        if (_cts.IsCancellationRequested) return;
        _player.Changed -= Poke;
        ListenersChanged -= SendPresence;
        _player.Seeked -= OnSeeked;
        List<Peer> peers;
        lock (_gate) peers = [.. _peers];
        await Task.WhenAll(peers.Select(async p =>
        {
            try { await p.Control.SendAsync(new Bye("The host ended the vibe.")).WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
            catch { }
        })).ConfigureAwait(false);
        _cts.Cancel();
        await _listener.DisposeAsync().ConfigureAwait(false);
        foreach (var p in peers) await RemoveAsync(p).ConfigureAwait(false);
        try { await Task.WhenAll(_acceptLoop, _publishLoop).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
        _identity.Dispose();
    }

    private sealed class Peer(string id, string name, Wire control, string dataToken)
    {
        public string Id { get; } = id;
        public string Name { get; } = name;
        public Wire Control { get; } = control;
        public string DataToken { get; } = dataToken;
        public Wire? Data { get; set; }
        public volatile bool Welcomed;
        public CancellationTokenSource Cts { get; } = new();
    }
}
