using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalFM.Vibe;
using LocalFM.Vibe.Client;
using LocalFM.Vibe.Host;
using LocalFM.Vibe.Net;

namespace LocalFM.UI.ViewModels;

/// <summary>The "Vibe together" panel: host a vibe, join one, or see who is in the one we are in.</summary>
public sealed partial class VibeViewModel : ObservableObject
{
    private const string NameKey = "vibe.name";
    private readonly MainViewModel _main;
    private readonly VibeService _vibe;
    private LanDiscovery? _discovery;
    private VibeRole _lastRole;

    public VibeViewModel(MainViewModel main)
    {
        _main = main;
        _vibe = main.Host.Vibe;
        _name = main.Host.History.Get(NameKey) is { Length: > 0 } n ? n : VibeService.DefaultName;
        _vibe.Changed += () => Dispatcher.UIThread.Post(Refresh);
        Refresh();
    }

    [ObservableProperty] private bool _isOpen;
    /// <summary>How others see us (host name or listener name).</summary>
    [ObservableProperty] private string _name;
    [ObservableProperty] private string? _joinCode;
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string? _error;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsIdle), nameof(IsHosting), nameof(IsListening), nameof(InVibe), nameof(ButtonTip))]
    private VibeRole _role;
    public bool IsIdle => Role == VibeRole.None;
    public bool IsHosting => Role == VibeRole.Hosting;
    public bool IsListening => Role == VibeRole.Listening;
    public bool InVibe => Role != VibeRole.None;

    // hosting
    [ObservableProperty] private string? _inviteCode;
    [ObservableProperty] private string? _reachabilityText;
    [ObservableProperty] private bool _visibleOnNetwork;
    public ObservableCollection<ListenerInfo> Listeners { get; } = new();
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ButtonTip))] private string? _listenersTitle;

    // listening
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ButtonTip))] private string? _hostName;
    [ObservableProperty] private string? _listeningStatus;
    public ObservableCollection<string> People { get; } = new();

    // joining
    public ObservableCollection<NearbyVibe> Nearby { get; } = new();
    [ObservableProperty] private bool _hasNearby;

    public string ButtonTip => Role switch
    {
        VibeRole.Hosting => $"Your vibe · {ListenersTitle}",
        VibeRole.Listening => $"Listening with {HostName}",
        _ => "Vibe together",
    };

    partial void OnIsOpenChanged(bool value)
    {
        if (value) _main.Lyrics.IsOpen = false;
        UpdateDiscovery();
    }

    partial void OnNameChanged(string value) => _ = _main.Host.History.SetAsync(NameKey, value.Trim());

    partial void OnVisibleOnNetworkChanged(bool value) => _vibe.SetVisibleOnNetwork(value);

    private string CleanName => string.IsNullOrWhiteSpace(Name) ? VibeService.DefaultName : Name.Trim();

    // ------------------------------------------------------------------ commands

    [RelayCommand]
    private async Task StartHosting()
    {
        Error = null;
        Busy = true;
        try { await Task.Run(() => _vibe.StartHostingAsync(CleanName)); }
        catch (Exception ex) { Error = $"Could not start the vibe: {ex.Message}"; }
        finally { Busy = false; }
    }

    [RelayCommand]
    private Task Join() => JoinInvite(Invite.TryDecode(JoinCode, out var invite) ? invite : null);

    [RelayCommand]
    private Task JoinNearby(NearbyVibe vibe) => JoinInvite(vibe.Invite);

    private async Task JoinInvite(Invite? invite)
    {
        Error = null;
        if (invite is null) { Error = "That is not a vibe invite. It starts with “lfmvibe1-”."; return; }
        Busy = true;
        try
        {
            await Task.Run(() => _vibe.JoinAsync(invite, CleanName));
            JoinCode = null;
        }
        catch (VibeRejectedException ex) { Error = ex.Message; }
        catch (Exception ex) { Error = ex.Message; }
        finally { Busy = false; }
    }

    /// <summary>Ends our vibe, or leaves the one we listen to.</summary>
    [RelayCommand]
    private async Task Stop()
    {
        Busy = true;
        try { await Task.Run(_vibe.StopAsync); }
        finally { Busy = false; }
    }

    [RelayCommand]
    private async Task CopyInvite()
    {
        if (InviteCode is null) return;
        if (_main.CopyText is { } copy)
        {
            await copy(InviteCode);
            _main.Notify("Invite copied. Send it to your friends.");
        }
    }

    public bool CanShare => App.Platform?.CanShare ?? false;

    /// <summary>The phone's share sheet: a message app, mail, nearby share.</summary>
    [RelayCommand]
    private void ShareInvite()
    {
        if (InviteCode is { } code) App.Platform?.Share($"Join my LocalFM vibe: {code}");
    }

    [RelayCommand]
    private Task Kick(ListenerInfo listener) => _vibe.Host is { } h ? Task.Run(() => h.KickAsync(listener.Id)) : Task.CompletedTask;

    // ------------------------------------------------------------------ state

    private void Refresh()
    {
        var role = _vibe.Role;
        if (_lastRole == VibeRole.Listening && role == VibeRole.None && _vibe.EndedReason is { } why)
        {
            Error = why;
            _main.Notify(why);
        }
        _lastRole = role;
        Role = role;

        var host = _vibe.Host;
        InviteCode = host?.Invite.Encode();
        VisibleOnNetwork = _vibe.VisibleOnNetwork;
        ReachabilityText = _vibe.Reachability switch
        {
            Reachability.Checking => "Asking your router to let friends in from outside…",
            Reachability.Internet => "Friends anywhere can join with the invite.",
            Reachability.CarrierNat => "Your internet provider blocks incoming connections, so only friends on this network "
                + "(or on a shared VPN such as Tailscale) can join.",
            _ => "Friends on this network (or on a shared VPN such as Tailscale) can join. Your router did not open a port "
                + "for friends elsewhere; forwarding TCP port " + (host?.Port ?? TlsTransport.DefaultPort) + " to this computer would.",
        };
        Sync(Listeners, host?.Listeners ?? []);
        ListenersTitle = Listeners.Count switch { 0 => "Nobody has joined yet", 1 => "1 listening", var n => $"{n} listening" };

        HostName = _vibe.HostName;
        Sync(People, _vibe.Follower?.People ?? []);
        ListeningStatus = _vibe.Reconnecting ? "Connection lost. Reconnecting… (the music plays on meanwhile)" : null;
        UpdateDiscovery();
    }

    private static void Sync<T>(ObservableCollection<T> target, IReadOnlyList<T> source)
    {
        if (target.SequenceEqual(source)) return;
        target.Clear();
        foreach (var x in source) target.Add(x);
    }

    /// <summary>Looks for vibes on the network only while the panel offers joining one.</summary>
    private void UpdateDiscovery()
    {
        bool want = IsOpen && IsIdle;
        if (want && _discovery is null)
        {
            _discovery = new LanDiscovery();
            _discovery.Changed += () => Dispatcher.UIThread.Post(() =>
            {
                if (_discovery is not { } d) return;
                // Announcements refresh SeenAt every few seconds; only rebuild when the vibes themselves change.
                if (!Nearby.Select(v => v.Invite.Encode()).SequenceEqual(d.Vibes.Select(v => v.Invite.Encode()))) Sync(Nearby, d.Vibes);
                HasNearby = Nearby.Count > 0;
            });
        }
        else if (!want && _discovery is not null)
        {
            _discovery.Dispose();
            _discovery = null;
            Nearby.Clear();
            HasNearby = false;
        }
    }
}
