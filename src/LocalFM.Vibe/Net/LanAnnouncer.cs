using System.Net;
using System.Net.Sockets;
using System.Text;
using LocalFM.Core;

namespace LocalFM.Vibe.Net;

/// <summary>A vibe someone on this network announces.</summary>
public sealed record NearbyVibe(string HostName, Invite Invite, DateTime SeenAt);

/// <summary>
/// "Visible on my network": the host multicasts its invite every couple of seconds so people on the same
/// network can pick it from a list instead of pasting a code. Off unless the host turns it on (anyone on the
/// network can then join).
/// </summary>
public static class LanAnnouncer
{
    private const string Area = "Vibe";
    private const string Magic = "LFMVIBE1 ";
    public static readonly IPEndPoint Group = new(IPAddress.Parse("239.255.77.77"), 47812);

    /// <summary>Announces until <paramref name="ct"/> is cancelled; <paramref name="invite"/> is asked each time.</summary>
    public static async Task AnnounceAsync(Func<Invite> invite, CancellationToken ct)
    {
        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.MulticastLoopback = true; // other instances on this machine see it too
            udp.Ttl = 1;
            while (!ct.IsCancellationRequested)
            {
                var bytes = Encoding.UTF8.GetBytes(Magic + invite().Encode());
                try { await udp.SendAsync(bytes, Group, ct).ConfigureAwait(false); }
                catch (SocketException ex) { Log.Debug(Area, $"Announce: {ex.Message}"); }
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Warn(Area, "Could not announce the vibe on the network", ex); }
    }
}

/// <summary>Collects vibes announced on this network (see <see cref="LanAnnouncer"/>).</summary>
public sealed class LanDiscovery : IDisposable
{
    private static readonly TimeSpan Forget = TimeSpan.FromSeconds(7);
    private readonly CancellationTokenSource _cts = new();
    private readonly Lock _gate = new();
    private readonly Dictionary<string, NearbyVibe> _seen = new();

    public LanDiscovery() => _ = Task.Run(Listen);

    /// <summary>Raised (on a background thread) when a vibe appears or disappears.</summary>
    public event Action? Changed;

    public IReadOnlyList<NearbyVibe> Vibes
    {
        get
        {
            lock (_gate)
            {
                var cutoff = DateTime.UtcNow - Forget;
                return _seen.Values.Where(v => v.SeenAt > cutoff).OrderBy(v => v.HostName).ToList();
            }
        }
    }

    private async Task Listen()
    {
        try
        {
            using var udp = new UdpClient();
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, LanAnnouncer.Group.Port));
            udp.JoinMulticastGroup(LanAnnouncer.Group.Address);
            _ = Task.Run(Expire);
            while (!_cts.IsCancellationRequested)
            {
                var r = await udp.ReceiveAsync(_cts.Token).ConfigureAwait(false);
                string text = Encoding.UTF8.GetString(r.Buffer);
                if (!text.StartsWith("LFMVIBE1 ", StringComparison.Ordinal) || !Invite.TryDecode(text[9..], out var invite)) continue;
                string key = Convert.ToHexString(invite.Fingerprint);
                bool isNew;
                lock (_gate)
                {
                    isNew = !_seen.ContainsKey(key) || _seen[key].SeenAt < DateTime.UtcNow - Forget;
                    _seen[key] = new NearbyVibe(invite.HostName, invite, DateTime.UtcNow);
                }
                if (isNew) Changed?.Invoke();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Info("Vibe", $"Not looking for vibes on the network: {ex.Message}"); }
    }

    private async Task Expire()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), _cts.Token).ConfigureAwait(false);
                bool removed;
                lock (_gate)
                {
                    var cutoff = DateTime.UtcNow - Forget;
                    var gone = _seen.Where(kv => kv.Value.SeenAt <= cutoff).Select(kv => kv.Key).ToList();
                    foreach (var k in gone) _seen.Remove(k);
                    removed = gone.Count > 0;
                }
                if (removed) Changed?.Invoke();
            }
        }
        catch (OperationCanceledException) { }
    }

    public void Dispose() => _cts.Cancel();
}
