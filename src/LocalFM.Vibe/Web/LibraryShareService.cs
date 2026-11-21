using System.Net;
using System.Net.Sockets;
using LocalFM.Core;
using LocalFM.Library;
using LocalFM.Vibe.Library;
using LocalFM.Vibe.Net;

namespace LocalFM.Vibe.Web;

/// <summary>
/// "Share my library on this network": runs the <see cref="WebGateway"/> so TVs and browsers at home can browse
/// the library and play from it on their own (they don't follow this computer's playback). Independent of vibes.
/// The on/off choice is remembered and restored at start.
/// </summary>
public sealed class LibraryShareService : IAsyncDisposable
{
    public const string EnabledKey = "remote.enabled";

    private readonly LibraryApi _api;
    private readonly History _kv;
    private readonly Lock _gate = new();
    private WebGateway? _gateway;

    public LibraryShareService(LibraryApi api, History kv)
    {
        _api = api;
        _kv = kv;
        Devices = new PairedDevices(kv);
        Devices.Changed += () => Changed?.Invoke();
    }

    public PairedDevices Devices { get; }

    /// <summary>Anything shown about the share changed (running, PIN, devices). Raised on any thread.</summary>
    public event Action? Changed;

    public bool Remembered => _kv.Get(EnabledKey) == "1";
    public bool Running { get { lock (_gate) return _gateway is not null; } }
    public string? Error { get; private set; }
    public int? Port { get { lock (_gate) return _gateway?.Port; } }
    public string? Pin { get { lock (_gate) return _gateway?.Pin; } }

    /// <summary>Addresses to type into the TV: this machine on its home networks.</summary>
    public IReadOnlyList<string> Urls
    {
        get
        {
            if (Port is not { } port) return [];
            return Invite.LocalAddresses()
                .Where(Http.IsLocalNetwork)
                .Select(a => a.AddressFamily == AddressFamily.InterNetworkV6 ? $"http://[{a}]:{port}" : $"http://{a}:{port}")
                .ToList();
        }
    }

    /// <summary>Starts or stops sharing, and remembers the choice. Never throws: a failure is in <see cref="Error"/>.</summary>
    public async Task SetEnabledAsync(bool on)
    {
        await _kv.SetAsync(EnabledKey, on ? "1" : "0").ConfigureAwait(false);
        if (on) Start(); else await StopAsync().ConfigureAwait(false);
    }

    /// <summary>Starts sharing if it was on last time.</summary>
    public void Restore()
    {
        if (Remembered) Start();
    }

    private void Start()
    {
        lock (_gate)
        {
            if (_gateway is not null) return;
            try
            {
                _gateway = new WebGateway(_api, Devices, HostName());
                _gateway.PinChanged += OnPinChanged;
                Error = null;
            }
            catch (Exception ex) when (ex is IOException or SocketException)
            {
                Log.Warn("Share", "Could not share the library", ex);
                Error = "Could not open a network port for sharing.";
            }
        }
        Changed?.Invoke();
    }

    private void OnPinChanged() => Changed?.Invoke();

    public string? NewPin()
    {
        string? pin;
        lock (_gate) pin = _gateway?.NewPin();
        return pin;
    }

    public void ClosePin()
    {
        lock (_gate) _gateway?.ClosePin();
    }

    public async Task StopAsync()
    {
        WebGateway? g;
        lock (_gate) { g = _gateway; _gateway = null; }
        if (g is null) return;
        g.PinChanged -= OnPinChanged;
        await g.DisposeAsync().ConfigureAwait(false);
        Changed?.Invoke();
    }

    private static string HostName()
    {
        try { return Dns.GetHostName(); } catch { return Environment.MachineName; }
    }

    public ValueTask DisposeAsync() => new(StopAsync());
}
