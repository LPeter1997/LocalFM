using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security;
using System.Text;
using System.Xml.Linq;
using LocalFM.Core;

namespace LocalFM.Vibe.Net;

/// <summary>A port the router forwards to us.</summary>
public sealed record PortMapping(IPAddress ExternalAddress, int ExternalPort, string Method)
{
    public IPEndPoint EndPoint => new(ExternalAddress, ExternalPort);

    /// <summary>The router's own "public" address is private or carrier-grade NAT (100.64/10): the provider puts
    /// another NAT in front, so people outside cannot reach us even with the mapping.</summary>
    public bool BehindCarrierNat => IsPrivate(ExternalAddress);

    internal static bool IsPrivate(IPAddress a) => a.GetAddressBytes() switch
    {
        [10, ..] => true,
        [172, >= 16 and <= 31, ..] => true,
        [192, 168, ..] => true,
        [100, >= 64 and <= 127, ..] => true,
        [127, ..] => true,
        [169, 254, ..] => true,
        _ => false,
    };
}

/// <summary>
/// Asks the home router to forward a TCP port to us, so friends outside the network can join without setting
/// anything up: NAT-PMP first (simple, Apple/most open firmware), then UPnP IGD (most consumer routers).
/// The mapping is renewed while held and removed on dispose. Nothing here is required: no mapping simply means
/// LAN/VPN only.
/// </summary>
public sealed class PortMapper : IAsyncDisposable
{
    private const string Area = "Vibe";
    private const int LeaseSeconds = 3600;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    private readonly int _internalPort;
    private readonly CancellationTokenSource _cts = new();
    private Func<Task>? _remove;
    private Task? _renewal;

    private PortMapper(int internalPort) => _internalPort = internalPort;

    public PortMapping? Mapping { get; private set; }

    /// <summary>Tries to map <paramref name="internalPort"/>; the result says how (or holds no mapping).</summary>
    public static async Task<PortMapper> MapAsync(int internalPort, CancellationToken ct = default)
    {
        var m = new PortMapper(internalPort);
        if (Gateway() is not { } gw)
        {
            Log.Info(Area, "No default gateway: the vibe stays on this network");
            return m;
        }
        try
        {
            if (await m.TryNatPmp(gw, ct).ConfigureAwait(false) || await m.TryUpnp(gw, ct).ConfigureAwait(false))
            {
                Log.Info(Area, $"Router forwards {m.Mapping!.EndPoint} to port {internalPort} ({m.Mapping.Method})");
                m._renewal = Task.Run(m.Renew);
            }
            else Log.Info(Area, "The router did not map a port (no NAT-PMP or UPnP)");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Log.Info(Area, $"Port mapping failed: {ex.Message}"); }
        return m;
    }

    private async Task Renew()
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(LeaseSeconds / 2), _cts.Token).ConfigureAwait(false);
                if (Gateway() is not { } gw) continue;
                try
                {
                    bool ok = Mapping?.Method == "NAT-PMP" ? await TryNatPmp(gw, _cts.Token).ConfigureAwait(false)
                        : await TryUpnp(gw, _cts.Token).ConfigureAwait(false);
                    if (!ok) Log.Info(Area, "Could not renew the port mapping");
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { Log.Info(Area, $"Renewing the port mapping: {ex.Message}"); }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>The IPv4 default gateway of the first interface that is up and has one.</summary>
    internal static IPAddress? Gateway()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().GatewayAddresses)
                .Select(g => g.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));
        }
        catch (NetworkInformationException) { return null; }
    }

    /// <summary>Our address on the network that leads to <paramref name="gateway"/>.</summary>
    private static IPAddress LocalAddressToward(IPAddress gateway)
    {
        using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        s.Connect(gateway, 9); // UDP connect sends nothing; it only picks the route
        return ((IPEndPoint)s.LocalEndPoint!).Address;
    }

    // ------------------------------------------------------------------ NAT-PMP (RFC 6886)

    private async Task<bool> TryNatPmp(IPAddress gateway, CancellationToken ct)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Connect(gateway, 5351);
        var ext = await NatPmpRequest(udp, [0, 0], 12, ct).ConfigureAwait(false);
        if (ext is null || ext[1] != 128 || BinaryPrimitives.ReadUInt16BigEndian(ext.AsSpan(2)) != 0) return false;
        var address = new IPAddress(ext.AsSpan(8, 4));

        var req = new byte[12];
        req[1] = 2; // map TCP
        BinaryPrimitives.WriteUInt16BigEndian(req.AsSpan(4), (ushort)_internalPort);
        BinaryPrimitives.WriteUInt16BigEndian(req.AsSpan(6), (ushort)(Mapping?.ExternalPort ?? _internalPort));
        BinaryPrimitives.WriteUInt32BigEndian(req.AsSpan(8), LeaseSeconds);
        var map = await NatPmpRequest(udp, req, 16, ct).ConfigureAwait(false);
        if (map is null || map[1] != 130 || BinaryPrimitives.ReadUInt16BigEndian(map.AsSpan(2)) != 0) return false;
        int external = BinaryPrimitives.ReadUInt16BigEndian(map.AsSpan(10));
        Mapping = new PortMapping(address, external, "NAT-PMP");
        _remove = async () =>
        {
            using var u = new UdpClient(AddressFamily.InterNetwork);
            u.Connect(gateway, 5351);
            var del = new byte[12];
            del[1] = 2;
            BinaryPrimitives.WriteUInt16BigEndian(del.AsSpan(4), (ushort)_internalPort); // lifetime 0 = delete
            await NatPmpRequest(u, del, 16, CancellationToken.None).ConfigureAwait(false);
        };
        return true;
    }

    /// <summary>Sends with the RFC's back-off (250 ms, doubling), a few times; null when the router stays silent.</summary>
    private static async Task<byte[]?> NatPmpRequest(UdpClient udp, byte[] request, int minLength, CancellationToken ct)
    {
        int wait = 250;
        for (int attempt = 0; attempt < 3; attempt++, wait *= 2)
        {
            await udp.SendAsync(request, ct).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(wait);
            try
            {
                while (true)
                {
                    var r = await udp.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                    if (r.Buffer.Length >= minLength && r.Buffer[0] == 0 && r.Buffer[1] == request[1] + 128) return r.Buffer;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            catch (SocketException) { return null; } // port unreachable: no NAT-PMP here
        }
        return null;
    }

    // ------------------------------------------------------------------ UPnP IGD

    private static readonly string[] ServiceTypes =
    [
        "urn:schemas-upnp-org:service:WANIPConnection:2",
        "urn:schemas-upnp-org:service:WANIPConnection:1",
        "urn:schemas-upnp-org:service:WANPPPConnection:1",
    ];

    private async Task<bool> TryUpnp(IPAddress gateway, CancellationToken ct)
    {
        if (await DiscoverAsync(gateway, ct).ConfigureAwait(false) is not { } location) return false;
        var desc = XDocument.Parse(await Http.GetStringAsync(location, ct).ConfigureAwait(false));
        var service = desc.Descendants().Where(e => e.Name.LocalName == "service")
            .Select(e => (Type: Child(e, "serviceType"), Control: Child(e, "controlURL")))
            .Where(s => s.Type is not null && s.Control is not null && ServiceTypes.Contains(s.Type))
            .OrderBy(s => Array.IndexOf(ServiceTypes, s.Type))
            .FirstOrDefault();
        if (service.Type is null) return false;
        var control = new Uri(new Uri(location), service.Control!);

        var local = LocalAddressToward(gateway);
        int external = Mapping?.ExternalPort ?? _internalPort;
        bool mapped = false;
        for (int attempt = 0; attempt < 3 && !mapped; attempt++)
        {
            var (ok, _) = await Soap(control, service.Type, "AddPortMapping", ct,
                ("NewRemoteHost", ""), ("NewExternalPort", external.ToString()), ("NewProtocol", "TCP"),
                ("NewInternalPort", _internalPort.ToString()), ("NewInternalClient", local.ToString()), ("NewEnabled", "1"),
                ("NewPortMappingDescription", "LocalFM vibe"), ("NewLeaseDuration", LeaseSeconds.ToString())).ConfigureAwait(false);
            mapped = ok;
            if (!ok) external = Random.Shared.Next(40000, 60000); // taken by someone else: another port
        }
        if (!mapped) return false;
        var (_, ipXml) = await Soap(control, service.Type, "GetExternalIPAddress", ct).ConfigureAwait(false);
        var ip = ipXml?.Descendants().FirstOrDefault(e => e.Name.LocalName == "NewExternalIPAddress")?.Value;
        if (!IPAddress.TryParse(ip, out var address)) return false;
        Mapping = new PortMapping(address, external, "UPnP");
        string type = service.Type;
        _remove = () => Soap(control, type, "DeletePortMapping", CancellationToken.None,
            ("NewRemoteHost", ""), ("NewExternalPort", external.ToString()), ("NewProtocol", "TCP"));
        return true;
    }

    private static string? Child(XElement e, string name) => e.Elements().FirstOrDefault(c => c.Name.LocalName == name)?.Value.Trim();

    /// <summary>SSDP search for the gateway's IGD description URL (only answers from the gateway itself count).</summary>
    private static async Task<string?> DiscoverAsync(IPAddress gateway, CancellationToken ct)
    {
        using var udp = new UdpClient(new IPEndPoint(LocalAddressToward(gateway), 0));
        var target = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
        foreach (var st in new[] { "urn:schemas-upnp-org:device:InternetGatewayDevice:1", "urn:schemas-upnp-org:device:InternetGatewayDevice:2" })
        {
            var msg = Encoding.ASCII.GetBytes($"M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\nST: {st}\r\n\r\n");
            await udp.SendAsync(msg, target, ct).ConfigureAwait(false);
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            while (true)
            {
                var r = await udp.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                if (!r.RemoteEndPoint.Address.Equals(gateway)) continue;
                foreach (var line in Encoding.ASCII.GetString(r.Buffer).Split("\r\n"))
                    if (line.StartsWith("LOCATION:", StringComparison.OrdinalIgnoreCase))
                    {
                        string url = line[9..].Trim();
                        // Only talk to the gateway (a reply cannot send us elsewhere on the network).
                        if (Uri.TryCreate(url, UriKind.Absolute, out var u) && IPAddress.TryParse(u.Host, out var h) && h.Equals(gateway))
                            return url;
                    }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
    }

    private static async Task<(bool Ok, XDocument? Body)> Soap(Uri control, string serviceType, string action, CancellationToken ct,
        params (string Name, string Value)[] args)
    {
        var body = new StringBuilder();
        body.Append("<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" ")
            .Append("s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body>")
            .Append($"<u:{action} xmlns:u=\"{serviceType}\">");
        foreach (var (n, v) in args) body.Append($"<{n}>{SecurityElement.Escape(v)}</{n}>");
        body.Append($"</u:{action}></s:Body></s:Envelope>");
        using var req = new HttpRequestMessage(HttpMethod.Post, control)
        {
            Content = new StringContent(body.ToString(), Encoding.UTF8, "text/xml"),
        };
        req.Headers.TryAddWithoutValidation("SOAPAction", $"\"{serviceType}#{action}\"");
        using var res = await Http.SendAsync(req, ct).ConfigureAwait(false);
        string text = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        XDocument? doc = null;
        try { doc = XDocument.Parse(text); } catch (System.Xml.XmlException) { }
        return (res.IsSuccessStatusCode, doc);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_renewal is not null) try { await _renewal.ConfigureAwait(false); } catch { }
        if (_remove is { } remove)
        {
            try { await remove().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
            catch (Exception ex) { Log.Debug(Area, $"Removing the port mapping: {ex.Message}"); }
        }
    }
}
