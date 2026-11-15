using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;
using LocalFM.Core;

namespace LocalFM.Vibe.Net;

/// <summary>A per-vibe self-signed certificate. Listeners pin its fingerprint (from the invite), so the
/// connection is encrypted and nobody but the host can answer, with no certificate authority involved.</summary>
public sealed class VibeIdentity : IDisposable
{
    private VibeIdentity(X509Certificate2 cert)
    {
        Certificate = cert;
        Fingerprint = SHA256.HashData(cert.RawData);
    }

    public X509Certificate2 Certificate { get; }
    public byte[] Fingerprint { get; }

    public static VibeIdentity Create()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest("CN=LocalFM vibe", key, HashAlgorithmName.SHA256);
        var now = DateTimeOffset.UtcNow;
        var cert = req.CreateSelfSigned(now.AddDays(-1), now.AddDays(30));
        // SChannel cannot use an ephemeral key for TLS; a round trip through PKCS#12 gives it a usable one.
        if (OperatingSystem.IsWindows())
        {
            var pfx = cert.Export(X509ContentType.Pkcs12);
            cert.Dispose();
            cert = X509CertificateLoader.LoadPkcs12(pfx, null);
        }
        return new VibeIdentity(cert);
    }

    public void Dispose() => Certificate.Dispose();
}

/// <summary>Where a vibe's connections come from; a relay could implement this later.</summary>
public interface IVibeListener : IAsyncDisposable
{
    int Port { get; }
    /// <summary>The next authenticated (TLS) connection.</summary>
    Task<Stream> AcceptAsync(CancellationToken ct);
}

/// <summary>Direct TCP + TLS.</summary>
public static class TlsTransport
{
    public const int DefaultPort = 47811;
    private const string Area = "Vibe";
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Listens on <paramref name="port"/> (the default one when free, so a port forward keeps working;
    /// otherwise any), IPv6 and IPv4.</summary>
    public static IVibeListener Listen(VibeIdentity identity, int port = DefaultPort) => new Listener(identity, port);

    /// <summary>Connects to whichever of the invite's addresses answers first with the right certificate.</summary>
    public static async Task<(Stream Stream, IPEndPoint EndPoint)> ConnectAsync(Invite invite, IEnumerable<IPEndPoint>? prefer, CancellationToken ct)
    {
        var endpoints = (prefer ?? []).Concat(invite.Endpoints).Distinct().ToList();
        using var race = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var attempts = endpoints.Select(ep => Attempt(ep, invite.Fingerprint, race.Token)).ToList();
        var errors = new List<string>();
        while (attempts.Count > 0)
        {
            var done = await Task.WhenAny(attempts).ConfigureAwait(false);
            attempts.Remove(done);
            try
            {
                var won = await done.ConfigureAwait(false);
                race.Cancel();
                foreach (var loser in attempts)
                    _ = loser.ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result.Item1.Dispose(); }, TaskScheduler.Default);
                return won;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested) { errors.Add(ex.Message); }
        }
        ct.ThrowIfCancellationRequested();
        throw new IOException("Could not reach the host" + (errors.Count > 0 ? $" ({errors.Distinct().First()})" : "") + ".");
    }

    private static async Task<(Stream Stream, IPEndPoint EndPoint)> Attempt(IPEndPoint ep, byte[] fingerprint, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(HandshakeTimeout);
        var tcp = new TcpClient(ep.AddressFamily) { NoDelay = true };
        try
        {
            await tcp.ConnectAsync(ep, timeout.Token).ConfigureAwait(false);
            var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "localfm-vibe",
                RemoteCertificateValidationCallback = (_, cert, _, _) =>
                    cert is not null && CryptographicOperations.FixedTimeEquals(SHA256.HashData(cert.GetRawCertData()), fingerprint),
            }, timeout.Token).ConfigureAwait(false);
            return (ssl, ep);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            tcp.Dispose();
            throw new TimeoutException($"{ep} did not answer");
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    private sealed class Listener : IVibeListener
    {
        private readonly VibeIdentity _identity;
        private readonly TcpListener _tcp;
        private readonly Channel<Stream> _ready = Channel.CreateBounded<Stream>(16);
        private readonly CancellationTokenSource _cts = new();

        public Listener(VibeIdentity identity, int port)
        {
            _identity = identity;
            _tcp = Bind(port) ?? Bind(0) ?? throw new IOException("Could not open a port for the vibe.");
            Port = ((IPEndPoint)_tcp.LocalEndpoint).Port;
            _ = Task.Run(AcceptLoop);
        }

        private static TcpListener? Bind(int port)
        {
            try
            {
                var l = new TcpListener(IPAddress.IPv6Any, port);
                l.Server.DualMode = true;
                l.Start();
                return l;
            }
            catch (SocketException) { }
            try
            {
                var l = new TcpListener(IPAddress.Any, port);
                l.Start();
                return l;
            }
            catch (SocketException) { return null; }
        }

        public int Port { get; }

        public async Task<Stream> AcceptAsync(CancellationToken ct) => await _ready.Reader.ReadAsync(ct).ConfigureAwait(false);

        /// <summary>Accepts continuously; each handshake runs on its own, so a slow peer holds up nobody.</summary>
        private async Task AcceptLoop()
        {
            var ct = _cts.Token;
            while (!ct.IsCancellationRequested)
            {
                TcpClient tcp;
                try { tcp = await _tcp.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (SocketException ex) { Log.Debug(Area, $"Accept failed: {ex.Message}"); continue; }
                tcp.NoDelay = true;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        timeout.CancelAfter(HandshakeTimeout);
                        var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
                        await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                        {
                            ServerCertificate = _identity.Certificate,
                            ClientCertificateRequired = false,
                        }, timeout.Token).ConfigureAwait(false);
                        if (!_ready.Writer.TryWrite(ssl)) await ssl.DisposeAsync().ConfigureAwait(false); // too many waiting
                    }
                    catch (Exception ex)
                    {
                        Log.Debug(Area, $"Handshake failed: {ex.Message}");
                        tcp.Dispose();
                    }
                });
            }
        }

        public ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _tcp.Stop();
            _ready.Writer.TryComplete();
            while (_ready.Reader.TryRead(out var s)) s.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
