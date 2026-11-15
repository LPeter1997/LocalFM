using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace LocalFM.Vibe.Net;

/// <summary>
/// What a friend needs to join: where the host can be reached (LAN, VPN and, with a router mapping, public
/// addresses; each with its port), the host's certificate fingerprint (so nobody can stand in for it) and the
/// join secret. Shared as a short text code: <c>lfmvibe1-</c> + base64url.
/// </summary>
public sealed record Invite(IReadOnlyList<IPEndPoint> Endpoints, byte[] Fingerprint, byte[] Secret, string HostName)
{
    public const string Prefix = "lfmvibe1-";

    public string Encode()
    {
        var name = Encoding.UTF8.GetBytes(HostName);
        if (name.Length > 60) name = name[..60];
        using var ms = new MemoryStream();
        Span<byte> port = stackalloc byte[2];
        ms.WriteByte((byte)Fingerprint.Length); ms.Write(Fingerprint);
        ms.WriteByte((byte)Secret.Length); ms.Write(Secret);
        ms.WriteByte((byte)name.Length); ms.Write(name);
        var eps = Endpoints.Take(16).ToList();
        ms.WriteByte((byte)eps.Count);
        foreach (var ep in eps)
        {
            var b = ep.Address.GetAddressBytes();
            ms.WriteByte((byte)b.Length); ms.Write(b);
            BinaryPrimitives.WriteUInt16BigEndian(port, (ushort)ep.Port);
            ms.Write(port);
        }
        return Prefix + Convert.ToBase64String(ms.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static bool TryDecode(string? text, out Invite invite)
    {
        invite = null!;
        if (text is null) return false;
        text = string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
        if (!text.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        try
        {
            string b64 = text[Prefix.Length..].Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            var r = new Reader(Convert.FromBase64String(b64));
            var fp = r.Bytes(r.U8());
            var secret = r.Bytes(r.U8());
            var name = Encoding.UTF8.GetString(r.Bytes(r.U8()));
            int n = r.U8();
            var eps = new List<IPEndPoint>();
            for (int i = 0; i < n; i++)
            {
                int len = r.U8();
                if (len is not (4 or 16)) return false;
                var addr = new IPAddress(r.Bytes(len));
                int port = r.U16();
                if (port == 0) return false;
                eps.Add(new IPEndPoint(addr, port));
            }
            if (fp.Length != 32 || secret.Length < 16 || eps.Count == 0) return false;
            invite = new Invite(eps, fp, secret, name);
            return true;
        }
        catch (Exception e) when (e is FormatException or IndexOutOfRangeException or ArgumentException) { return false; }
    }

    private sealed class Reader(byte[] data)
    {
        private int _at;
        public int U8() => data[_at++];
        public int U16() { int v = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(_at, 2)); _at += 2; return v; }
        public byte[] Bytes(int n)
        {
            if (_at + n > data.Length) throw new IndexOutOfRangeException();
            var b = data[_at..(_at + n)];
            _at += n;
            return b;
        }
    }

    /// <summary>This machine's addresses others might reach: LAN, VPNs such as Tailscale, public IPv6.
    /// Private IPv4 first (most likely to work), link-local left out.</summary>
    public static List<IPAddress> LocalAddresses()
    {
        var list = new List<IPAddress>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var u in nic.GetIPProperties().UnicastAddresses)
                {
                    var a = u.Address;
                    if (IPAddress.IsLoopback(a)) continue;
                    if (a.AddressFamily == AddressFamily.InterNetwork && a.GetAddressBytes() is [169, 254, ..]) continue;
                    if (a.AddressFamily == AddressFamily.InterNetworkV6 && (a.IsIPv6LinkLocal || a.IsIPv6SiteLocal || a.IsIPv6Multicast)) continue;
                    if (!list.Contains(a)) list.Add(a);
                }
            }
        }
        catch (NetworkInformationException) { }
        return list.OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).ToList();
    }
}
