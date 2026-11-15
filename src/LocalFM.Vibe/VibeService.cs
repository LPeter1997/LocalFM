using LocalFM.App;
using LocalFM.Core;
using LocalFM.Library;
using LocalFM.Library.Enrich;
using LocalFM.Vibe.Client;
using LocalFM.Vibe.Host;
using LocalFM.Vibe.Net;

namespace LocalFM.Vibe;

public enum VibeRole { None, Hosting, Listening }

/// <summary>The app's one vibe at a time: hosting one, listening to one, or neither.</summary>
public sealed class VibeService : IAsyncDisposable
{
    private const string Area = "Vibe";
    private readonly PlayerService _player;
    private readonly AppPaths _paths;
    private readonly SemaphoreSlim _busy = new(1, 1);
    private VibeHost? _host;
    private VibeClient? _client;
    private Follower? _follower;

    private readonly Func<TrackInfo, CancellationToken, Task<LyricsData?>>? _lyrics;

    /// <param name="lyrics">How hosting answers listeners' lyrics requests.</param>
    public VibeService(PlayerService player, AppPaths paths, Func<TrackInfo, CancellationToken, Task<LyricsData?>>? lyrics = null)
    {
        _player = player;
        _paths = paths;
        _lyrics = lyrics;
        RemoteMediaCache.Sweep(paths.VibeCacheDir); // leftovers of a session that did not end cleanly
    }

    public VibeRole Role => _host is not null ? VibeRole.Hosting : _follower is not null ? VibeRole.Listening : VibeRole.None;
    public VibeHost? Host => _host;
    public string? HostName => _host?.HostName ?? _client?.HostName;
    public Follower? Follower => _follower;
    /// <summary>Why the last listening session ended, for the UI to show.</summary>
    public string? EndedReason { get; private set; }

    /// <summary>Raised (on a background thread) when the role, listeners or session change.</summary>
    public event Action? Changed;

    public static string DefaultName
    {
        get
        {
            string n = Environment.UserName;
            return string.IsNullOrWhiteSpace(n) ? "LocalFM" : char.ToUpperInvariant(n[0]) + n[1..];
        }
    }

    public async Task<VibeHost> StartHostingAsync(string? name = null, int port = TlsTransport.DefaultPort)
    {
        await _busy.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
            _host = new VibeHost(_player, name ?? DefaultName, port, lyrics: _lyrics);
            _host.ListenersChanged += RaiseChanged;
            Log.Info(Area, $"Hosting a vibe on port {_host.Port}");
        }
        finally { _busy.Release(); }
        RaiseChanged();
        return _host;
    }

    public async Task JoinAsync(Invite invite, string? name = null, CancellationToken ct = default)
    {
        await _busy.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
            EndedReason = null;
            var client = await VibeClient.ConnectAsync(invite, name ?? DefaultName, ct).ConfigureAwait(false);
            _client = client;
            client.Ended += reason => _ = OnEndedAsync(client, reason);
            _follower = new Follower(client, _player, Path.Combine(_paths.VibeCacheDir, client.ListenerId));
            client.PeopleChanged += RaiseChanged;
            Log.Info(Area, $"Listening to {client.HostName}'s vibe");
        }
        finally { _busy.Release(); }
        RaiseChanged();
    }

    private async Task OnEndedAsync(VibeClient client, string reason)
    {
        await _busy.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_client != client) return;
            Log.Info(Area, $"The vibe ended: {reason}");
            EndedReason = reason;
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally { _busy.Release(); }
        RaiseChanged();
    }

    /// <summary>Stops hosting or leaves the vibe.</summary>
    public async Task StopAsync()
    {
        await _busy.WaitAsync().ConfigureAwait(false);
        try { await StopCoreAsync().ConfigureAwait(false); }
        finally { _busy.Release(); }
        RaiseChanged();
    }

    private async Task StopCoreAsync()
    {
        if (_host is { } h)
        {
            _host = null;
            h.ListenersChanged -= RaiseChanged;
            await h.DisposeAsync().ConfigureAwait(false);
        }
        if (_follower is { } f)
        {
            _follower = null;
            await f.DisposeAsync().ConfigureAwait(false);
        }
        if (_client is { } c)
        {
            _client = null;
            await c.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void RaiseChanged() => Changed?.Invoke();

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
