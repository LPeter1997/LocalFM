using LocalFM.App;
using LocalFM.Core;
using LocalFM.Library;
using LocalFM.Library.Enrich;
using LocalFM.Vibe.Client;
using LocalFM.Vibe.Host;
using LocalFM.Vibe.Net;

namespace LocalFM.Vibe;

public enum VibeRole { None, Hosting, Listening }

/// <summary>Who can reach a hosted vibe.</summary>
public enum Reachability
{
    /// <summary>Still asking the router.</summary>
    Checking,
    /// <summary>This network (and VPNs such as Tailscale) only: the router did not forward a port.</summary>
    LocalOnly,
    /// <summary>The router forwards a port: friends anywhere can join.</summary>
    Internet,
    /// <summary>The router forwards a port, but the provider's own NAT sits in front (CGNAT): only this
    /// network and VPNs work.</summary>
    CarrierNat,
}

/// <summary>The app's one vibe at a time: hosting one, listening to one, or neither.</summary>
public sealed class VibeService : IAsyncDisposable
{
    private const string Area = "Vibe";
    private static readonly TimeSpan ReconnectFor = TimeSpan.FromSeconds(30);

    private readonly PlayerService _player;
    private readonly AppPaths _paths;
    private readonly Func<TrackInfo, CancellationToken, Task<LyricsData?>>? _lyrics;
    private readonly SemaphoreSlim _busy = new(1, 1);
    private VibeHost? _host;
    private PortMapper? _mapper;
    private CancellationTokenSource? _hostCts;
    private CancellationTokenSource? _announce;
    private VibeClient? _client;
    private Follower? _follower;
    private (Invite Invite, string Name)? _joined;

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
    /// <summary>Listening, and trying to get the broken connection back (playing on from what is cached).</summary>
    public bool Reconnecting { get; private set; }
    /// <summary>Why the last listening session ended, for the UI to show.</summary>
    public string? EndedReason { get; private set; }
    public Reachability Reachability { get; private set; }
    public PortMapping? Mapping => _mapper?.Mapping;
    public bool VisibleOnNetwork => _announce is not null;

    /// <summary>Raised (on a background thread) when the role, listeners, reachability or session change.</summary>
    public event Action? Changed;

    public static string DefaultName
    {
        get
        {
            string n = Environment.UserName;
            return string.IsNullOrWhiteSpace(n) ? "LocalFM" : char.ToUpperInvariant(n[0]) + n[1..];
        }
    }

    // ------------------------------------------------------------------ hosting

    /// <param name="mapPort">Ask the router to forward a port (friends outside this network).</param>
    public async Task<VibeHost> StartHostingAsync(string? name = null, int port = TlsTransport.DefaultPort, bool mapPort = true)
    {
        VibeHost host;
        await _busy.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
            host = _host = new VibeHost(_player, name ?? DefaultName, port, lyrics: _lyrics);
            host.ListenersChanged += RaiseChanged;
            host.InviteChanged += RaiseChanged;
            _hostCts = new CancellationTokenSource();
            Reachability = mapPort ? Reachability.Checking : Reachability.LocalOnly;
            Log.Info(Area, $"Hosting a vibe on port {host.Port}");
            if (mapPort) _ = MapAsync(host, _hostCts.Token);
        }
        finally { _busy.Release(); }
        RaiseChanged();
        return host;
    }

    private async Task MapAsync(VibeHost host, CancellationToken ct)
    {
        PortMapper? mapper = null;
        try { mapper = await PortMapper.MapAsync(host.Port, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        await _busy.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (_host != host || ct.IsCancellationRequested) { _ = mapper.DisposeAsync().AsTask(); return; }
            _mapper = mapper;
            if (mapper.Mapping is not { } m) Reachability = Reachability.LocalOnly;
            else if (m.BehindCarrierNat) Reachability = Reachability.CarrierNat;
            else
            {
                Reachability = Reachability.Internet;
                host.SetPublicEndpoint(m.EndPoint);
            }
        }
        finally { _busy.Release(); }
        RaiseChanged();
    }

    /// <summary>Announces the hosted vibe to this network (anyone on it can then join without a code).</summary>
    public void SetVisibleOnNetwork(bool visible)
    {
        if (visible == VisibleOnNetwork || (visible && _host is null)) return;
        if (visible)
        {
            var host = _host!;
            _announce = new CancellationTokenSource();
            _ = LanAnnouncer.AnnounceAsync(() => host.Invite, _announce.Token);
        }
        else
        {
            _announce!.Cancel();
            _announce = null;
        }
        RaiseChanged();
    }

    // ------------------------------------------------------------------ listening

    public async Task JoinAsync(Invite invite, string? name = null, CancellationToken ct = default)
    {
        await _busy.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
            EndedReason = null;
            name ??= DefaultName;
            var client = await VibeClient.ConnectAsync(invite, name, ct).ConfigureAwait(false);
            _joined = (invite, name);
            Attach(client);
            _follower = new Follower(client, _player, Path.Combine(_paths.VibeCacheDir, client.ListenerId));
            Log.Info(Area, $"Listening to {client.HostName}'s vibe");
        }
        finally { _busy.Release(); }
        RaiseChanged();
    }

    private void Attach(VibeClient client)
    {
        _client = client;
        client.Ended += (reason, byHost) => _ = OnEndedAsync(client, reason, byHost);
        client.PeopleChanged += RaiseChanged;
    }

    private async Task OnEndedAsync(VibeClient client, string reason, bool byHost)
    {
        if (!byHost && _joined is { } joined)
        {
            if (_client != client) return;
            Reconnecting = true;
            RaiseChanged();
            Log.Info(Area, $"{reason} Reconnecting…");
            var until = DateTime.UtcNow + ReconnectFor;
            while (DateTime.UtcNow < until && _client == client)
            {
                try
                {
                    using var attempt = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    var fresh = await VibeClient.ConnectAsync(joined.Invite, joined.Name, attempt.Token).ConfigureAwait(false);
                    await _busy.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        if (_client != client || _follower is not { } f) { await fresh.DisposeAsync().ConfigureAwait(false); return; }
                        Attach(fresh);
                        f.Reattach(fresh);
                        Reconnecting = false;
                        Log.Info(Area, "Reconnected to the vibe");
                    }
                    finally { _busy.Release(); }
                    await client.DisposeAsync().ConfigureAwait(false);
                    RaiseChanged();
                    return;
                }
                catch (VibeRejectedException ex) { reason = ex.Message; break; }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or TimeoutException)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                }
            }
        }

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

    // ------------------------------------------------------------------ stopping

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
        _announce?.Cancel();
        _announce = null;
        _hostCts?.Cancel();
        _hostCts = null;
        if (_host is { } h)
        {
            _host = null;
            h.ListenersChanged -= RaiseChanged;
            h.InviteChanged -= RaiseChanged;
            await h.DisposeAsync().ConfigureAwait(false);
        }
        if (_mapper is { } m)
        {
            _mapper = null;
            await m.DisposeAsync().ConfigureAwait(false);
        }
        Reachability = Reachability.LocalOnly;
        _joined = null;
        Reconnecting = false;
        if (_follower is { } f)
        {
            _follower = null;
            Log.Info(Area, "Leaving the vibe: handing playback back");
            await f.DisposeAsync().ConfigureAwait(false);
        }
        if (_client is { } c)
        {
            _client = null;
            Log.Info(Area, "Leaving the vibe: closing the connection");
            await c.DisposeAsync().ConfigureAwait(false);
            Log.Info(Area, "Left the vibe");
        }
    }

    private void RaiseChanged() => Changed?.Invoke();

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
