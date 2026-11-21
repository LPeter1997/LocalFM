using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LocalFM.Core;
using LocalFM.Vibe.Library;
using LocalFM.Vibe.Protocol;
using Microsoft.Win32.SafeHandles;

namespace LocalFM.Vibe.Web;

/// <summary>
/// The library share for browsers and TVs: a small HTTP/1.1 server (no ASP.NET: size, and no admin rights for
/// URL reservations on Windows) that serves the web client, a web socket speaking the vibe protocol's JSON
/// messages (<see cref="Hello"/> to pair or sign in, then <see cref="LibraryRequest"/>s), and the audio and
/// pictures those messages name, with byte ranges so players can seek. Answers home networks only; every
/// library route needs a paired device's token.
/// </summary>
public sealed class WebGateway : IAsyncDisposable
{
    public const int DefaultPort = 47812;
    private const string Area = "Share";
    private const int MaxConnections = 64;
    private const int MaxMessage = 64 * 1024;
    private const int MaxInFlight = 8;
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(75);
    private static readonly TimeSpan PinLifetime = TimeSpan.FromMinutes(10);
    private const int PinAttempts = 5;

    private readonly LibraryApi _api;
    private readonly PairedDevices _devices;
    private readonly WavStreamer _wav;
    private readonly TcpListener _tcp;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _slots = new(MaxConnections, MaxConnections);
    private readonly ConcurrentDictionary<WebSocket, string> _sockets = new();
    private readonly Lock _pinGate = new();
    private string? _pin;
    private DateTime _pinExpires;
    private int _pinFailures;

    public WebGateway(LibraryApi api, PairedDevices devices, string hostName, int port = DefaultPort)
    {
        _api = api;
        _devices = devices;
        _wav = new WavStreamer();
        HostName = hostName;
        _tcp = Bind(port) ?? throw new IOException($"Could not open port {port} or the ones after it.");
        Port = ((IPEndPoint)_tcp.LocalEndpoint).Port;
        _devices.Changed += DropRemovedDevices;
        _ = Task.Run(AcceptLoop);
        Log.Info(Area, $"Sharing the library on port {Port}");
    }

    public string HostName { get; }
    public int Port { get; }

    /// <summary>The PIN a new device can pair with right now, if any.</summary>
    public string? Pin
    {
        get { lock (_pinGate) return _pin is not null && DateTime.UtcNow < _pinExpires ? _pin : null; }
    }

    public event Action? PinChanged;

    /// <summary>Starts a pairing window: a fresh six-digit PIN, single use, for a few minutes.</summary>
    public string NewPin()
    {
        string pin;
        lock (_pinGate)
        {
            _pin = pin = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
            _pinExpires = DateTime.UtcNow + PinLifetime;
            _pinFailures = 0;
        }
        PinChanged?.Invoke();
        return pin;
    }

    public void ClosePin()
    {
        lock (_pinGate) _pin = null;
        PinChanged?.Invoke();
    }

    private bool TakePin(string pin)
    {
        bool ok, closed = false;
        lock (_pinGate)
        {
            ok = _pin is not null && DateTime.UtcNow < _pinExpires
                && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(_pin), Encoding.ASCII.GetBytes(pin.Trim()));
            if (ok || ++_pinFailures >= PinAttempts) { closed = _pin is not null; _pin = null; }
        }
        if (closed) PinChanged?.Invoke();
        return ok;
    }

    private static TcpListener? Bind(int port)
    {
        for (int p = port; p < port + 8; p++)
        {
            try
            {
                var l = new TcpListener(IPAddress.IPv6Any, p);
                l.Server.DualMode = true;
                l.Start();
                return l;
            }
            catch (SocketException) { }
            try
            {
                var l = new TcpListener(IPAddress.Any, p);
                l.Start();
                return l;
            }
            catch (SocketException) { }
        }
        return null;
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient tcp;
            try { tcp = await _tcp.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (Exception ex) { Log.Warn(Area, "Accept failed", ex); continue; }
            if (!_slots.Wait(0)) { tcp.Dispose(); continue; }
            _ = Task.Run(async () =>
            {
                try { await ServeAsync(tcp, _cts.Token).ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException or WebSocketException) { }
                catch (Exception ex) { Log.Warn(Area, "Connection failed", ex); }
                finally
                {
                    tcp.Dispose();
                    _slots.Release();
                }
            });
        }
    }

    private async Task ServeAsync(TcpClient tcp, CancellationToken ct)
    {
        if (tcp.Client.RemoteEndPoint is not IPEndPoint remote || !Http.IsLocalNetwork(remote.Address)) return;
        tcp.NoDelay = true;
        var stream = tcp.GetStream();
        var heads = new HeadReader(stream);
        while (!ct.IsCancellationRequested)
        {
            byte[]? head;
            using (var idle = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                idle.CancelAfter(IdleTimeout);
                try { head = await heads.ReadAsync(idle.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
            if (head is null) return;
            var req = HttpRequest.Parse(head);
            if (req is null || req.Header("Transfer-Encoding") is not null || (req.Header("Content-Length") is { } cl && cl != "0"))
            {
                await Respond(stream, 400, keepAlive: false, ct).ConfigureAwait(false);
                return;
            }
            if (req.Path == "/ws")
            {
                if (heads.Buffered > 0) return; // a client must wait for the handshake before sending frames
                await WebSocketAsync(stream, req, ct).ConfigureAwait(false);
                return;
            }
            bool keep = await RouteAsync(stream, req, ct).ConfigureAwait(false) && req.KeepAlive;
            if (!keep) return;
        }
    }

    // ------------------------------------------------------------------ routes

    private async Task<bool> RouteAsync(Stream s, HttpRequest req, CancellationToken ct)
    {
        if (req.Method == "OPTIONS")
        {
            await WriteHeadAsync(s, 204, 0, null, req.KeepAlive, ct,
                ("Access-Control-Allow-Methods", "GET, HEAD, OPTIONS"),
                ("Access-Control-Allow-Headers", "Range"),
                ("Access-Control-Max-Age", "86400")).ConfigureAwait(false);
            return true;
        }
        if (req.Method is not ("GET" or "HEAD"))
        {
            await Respond(s, 405, req.KeepAlive, ct).ConfigureAwait(false);
            return true;
        }
        bool head = req.Method == "HEAD";

        if (req.Path == "/api/info")
        {
            var body = Info();
            await WriteHeadAsync(s, 200, body.Length, "application/json", req.KeepAlive, ct, ("Cache-Control", "no-store")).ConfigureAwait(false);
            if (!head) await s.WriteAsync(body, ct).ConfigureAwait(false);
            return true;
        }

        if (req.Path.StartsWith("/media/", StringComparison.Ordinal) || req.Path.StartsWith("/img/", StringComparison.Ordinal))
        {
            if (_devices.Find(req.Query.GetValueOrDefault("k")) is not { } device)
            {
                await Respond(s, 401, req.KeepAlive, ct).ConfigureAwait(false);
                return true;
            }
            _devices.Seen(device);
            if (req.Path.StartsWith("/img/", StringComparison.Ordinal))
            {
                if (_api.Media.Image(req.Path[5..]) is not { } img) { await Respond(s, 404, req.KeepAlive, ct).ConfigureAwait(false); return true; }
                return await FileAsync(s, req, img, Http.ContentType(img), "private, max-age=86400", ct).ConfigureAwait(false);
            }
            string id = req.Path[7..];
            bool wav = id.EndsWith(".wav", StringComparison.Ordinal);
            if (wav) id = id[..^4];
            if (_api.Media.Track(id) is not { } file) { await Respond(s, 404, req.KeepAlive, ct).ConfigureAwait(false); return true; }
            if (wav) return await _wav.ServeAsync(s, req, file, ct).ConfigureAwait(false);
            return await FileAsync(s, req, file.Path, AudioType(file), "private, no-cache", ct).ConfigureAwait(false);
        }

        string path = req.Path == "/" ? "index.html" : req.Path.TrimStart('/');
        if (ClientFiles.Get(path) is not { } bytes)
        {
            await Respond(s, 404, req.KeepAlive, ct).ConfigureAwait(false);
            return true;
        }
        await WriteHeadAsync(s, 200, bytes.Length, Http.ContentType(path), req.KeepAlive, ct, ("Cache-Control", "no-cache")).ConfigureAwait(false);
        if (!head) await s.WriteAsync(bytes, ct).ConfigureAwait(false);
        return true;
    }

    private byte[] Info()
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("app", "LocalFM");
            w.WriteString("name", HostName);
            w.WriteString("version", AppInfo.Version);
            w.WriteNumber("protocol", Wire.Version);
            w.WriteEndObject();
        }
        return ms.ToArray();
    }

    /// <summary>The audio type to announce: by codec (what the player cares about), else by file name.</summary>
    internal static string AudioType(MediaFile f) => f.Codec switch
    {
        "mp3" => "audio/mpeg",
        "flac" => "audio/flac",
        "aac" or "alac" => "audio/mp4",
        "vorbis" or "opus" when Path.GetExtension(f.Path).ToLowerInvariant() is ".webm" or ".mka" => "audio/webm",
        "vorbis" or "opus" => "audio/ogg",
        _ => Http.ContentType(f.Path),
    };

    /// <summary>Sends a file, or the asked-for range of it. False when the connection should close.</summary>
    private static async Task<bool> FileAsync(Stream s, HttpRequest req, string path, string type, string cache, CancellationToken ct)
    {
        SafeFileHandle h;
        try { h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.Asynchronous); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Respond(s, 404, req.KeepAlive, ct).ConfigureAwait(false);
            return true;
        }
        using (h)
        {
            long length = RandomAccess.GetLength(h);
            var range = Http.Range(req.Header("Range"), length);
            if (range is (-1, -1))
            {
                await WriteHeadAsync(s, 416, 0, null, req.KeepAlive, ct, ("Content-Range", $"bytes */{length}")).ConfigureAwait(false);
                return true;
            }
            long start = range?.Start ?? 0, end = range?.End ?? length - 1;
            long count = end - start + 1;
            var headers = new List<(string, string)> { ("Accept-Ranges", "bytes"), ("Cache-Control", cache) };
            if (range is not null) headers.Add(("Content-Range", $"bytes {start}-{end}/{length}"));
            await WriteHeadAsync(s, range is null ? 200 : 206, count, type, req.KeepAlive, ct, [.. headers]).ConfigureAwait(false);
            if (req.Method == "HEAD") return true;
            var buf = ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                long at = start;
                while (at <= end)
                {
                    int want = (int)Math.Min(buf.Length, end - at + 1);
                    int n = await RandomAccess.ReadAsync(h, buf.AsMemory(0, want), at, ct).ConfigureAwait(false);
                    if (n <= 0) return false; // the file shrank: the promised length can't be kept
                    await s.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                    at += n;
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buf); }
            return true;
        }
    }

    private static Task Respond(Stream s, int status, bool keepAlive, CancellationToken ct) =>
        WriteHeadAsync(s, status, 0, null, keepAlive, ct);

    internal static async Task WriteHeadAsync(Stream s, int status, long length, string? type, bool keepAlive, CancellationToken ct,
        params (string Name, string Value)[] extra)
    {
        var sb = new StringBuilder();
        sb.Append("HTTP/1.1 ").Append(status).Append(' ').Append(Http.Reason(status)).Append("\r\n");
        if (status != 204 && status != 101) sb.Append("Content-Length: ").Append(length).Append("\r\n");
        if (type is not null) sb.Append("Content-Type: ").Append(type).Append("\r\n");
        sb.Append("Access-Control-Allow-Origin: *\r\n");
        sb.Append("Access-Control-Expose-Headers: Content-Range, Content-Length, Accept-Ranges\r\n");
        sb.Append("X-Content-Type-Options: nosniff\r\n");
        if (status != 101) sb.Append(keepAlive ? "Connection: keep-alive\r\n" : "Connection: close\r\n");
        foreach (var (n, v) in extra) sb.Append(n).Append(": ").Append(v).Append("\r\n");
        sb.Append("\r\n");
        await s.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()), ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ web socket

    private async Task WebSocketAsync(Stream s, HttpRequest req, CancellationToken ct)
    {
        if (req.Method != "GET" || !(req.Header("Upgrade") ?? "").Equals("websocket", StringComparison.OrdinalIgnoreCase)
            || req.Header("Sec-WebSocket-Key") is not { Length: > 0 } key)
        {
            await Respond(s, 400, keepAlive: false, ct).ConfigureAwait(false);
            return;
        }
        await WriteHeadAsync(s, 101, 0, null, false, ct,
            ("Upgrade", "websocket"), ("Connection", "Upgrade"), ("Sec-WebSocket-Accept", Http.WebSocketAccept(key))).ConfigureAwait(false);
        using var ws = WebSocket.CreateFromStream(s, new WebSocketCreationOptions { IsServer = true, KeepAliveInterval = TimeSpan.FromSeconds(20) });
        var session = new Session(ws);
        try
        {
            var device = await SignInAsync(session, ct).ConfigureAwait(false);
            if (device is null) return;
            _sockets[ws] = device.Id;
            Log.Info(Area, $"{device.Name} connected");
            using var inFlight = new SemaphoreSlim(MaxInFlight, MaxInFlight);
            while (await session.ReceiveAsync(ct).ConfigureAwait(false) is { } m)
            {
                switch (m)
                {
                    case Ping p:
                        await session.SendAsync(new Pong(p.T0, VibeClock.NowUs), ct).ConfigureAwait(false);
                        break;
                    case LibraryRequest q:
                        await inFlight.WaitAsync(ct).ConfigureAwait(false);
                        _ = Task.Run(async () =>
                        {
                            try { await session.SendAsync(await _api.HandleAsync(q, ct).ConfigureAwait(false), ct).ConfigureAwait(false); }
                            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException or IOException) { }
                            finally { inFlight.Release(); }
                        }, ct);
                        _devices.Seen(device);
                        break;
                }
            }
            Log.Info(Area, $"{device.Name} disconnected");
        }
        finally
        {
            _sockets.TryRemove(ws, out _);
            if (ws.State == WebSocketState.Open)
                try { await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                catch { }
        }
    }

    /// <summary>The first message must be a <see cref="Hello"/> whose secret is a paired device's token, or
    /// <c>pin:</c> + the current PIN (which pairs this device and hands it a token in <see cref="Welcome"/>).</summary>
    private async Task<PairedDevice?> SignInAsync(Session session, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        if (await session.ReceiveAsync(timeout.Token).ConfigureAwait(false) is not Hello hello) return null;
        if (hello.Version != Wire.Version)
        {
            await session.SendAsync(new Rejected(hello.Version > Wire.Version
                ? "This LocalFM is older than the app on this device. Update LocalFM on the computer."
                : "This app is older than LocalFM on the computer. Reload or update it."), ct).ConfigureAwait(false);
            return null;
        }
        PairedDevice? device;
        string token;
        if (hello.Secret.StartsWith("pin:", StringComparison.Ordinal))
        {
            if (!TakePin(hello.Secret[4..]))
            {
                await session.SendAsync(new Rejected("That PIN is wrong or has expired. Get a new one in LocalFM on the computer."), ct).ConfigureAwait(false);
                return null;
            }
            (device, token) = _devices.Pair(hello.Name);
            Log.Info(Area, $"Paired {device.Name}");
        }
        else
        {
            device = _devices.Find(hello.Secret);
            token = hello.Secret;
            if (device is null)
            {
                await session.SendAsync(new Rejected("This device is no longer paired. Pair it again with a PIN."), ct).ConfigureAwait(false);
                return null;
            }
        }
        await session.SendAsync(new Welcome(device.Id, token, HostName, [Capability.Library]), ct).ConfigureAwait(false);
        return device;
    }

    private void DropRemovedDevices()
    {
        var ids = _devices.List.Select(d => d.Id).ToHashSet();
        foreach (var (ws, id) in _sockets)
            if (!ids.Contains(id)) ws.Abort();
    }

    /// <summary>JSON messages over a web socket; sends are serialized.</summary>
    private sealed class Session(WebSocket ws)
    {
        private readonly SemaphoreSlim _send = new(1, 1);
        private readonly byte[] _buf = new byte[MaxMessage];

        public async Task<Message?> ReceiveAsync(CancellationToken ct)
        {
            while (true)
            {
                int n = 0;
                ValueWebSocketReceiveResult r;
                do
                {
                    if (n == _buf.Length) throw new WebSocketException("Message too large.");
                    r = await ws.ReceiveAsync(_buf.AsMemory(n), ct).ConfigureAwait(false);
                    if (r.MessageType == WebSocketMessageType.Close) return null;
                    n += r.Count;
                } while (!r.EndOfMessage);
                if (r.MessageType != WebSocketMessageType.Text) continue;
                try { if (JsonSerializer.Deserialize(_buf.AsSpan(0, n), WireJson.Default.Message) is { } m) return m; }
                catch (JsonException) { }
                catch (NotSupportedException) { } // unknown "t"
            }
        }

        public async Task SendAsync(Message m, CancellationToken ct)
        {
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(m, WireJson.Default.Message);
            await _send.WaitAsync(ct).ConfigureAwait(false);
            try { await ws.SendAsync(json, WebSocketMessageType.Text, true, ct).ConfigureAwait(false); }
            finally { _send.Release(); }
        }
    }

    /// <summary>Reads request heads off a keep-alive connection, keeping any bytes that follow for the next one.</summary>
    private sealed class HeadReader(Stream s)
    {
        private readonly byte[] _buf = new byte[HttpRequest.MaxHead];
        private int _len;

        public int Buffered => _len;

        /// <summary>The next head without its blank line; null when the peer closed (or sent garbage).</summary>
        public async Task<byte[]?> ReadAsync(CancellationToken ct)
        {
            while (true)
            {
                int end = _buf.AsSpan(0, _len).IndexOf("\r\n\r\n"u8);
                if (end >= 0)
                {
                    var head = _buf[..end];
                    int rest = _len - end - 4;
                    Buffer.BlockCopy(_buf, end + 4, _buf, 0, rest);
                    _len = rest;
                    return head;
                }
                if (_len == _buf.Length) return null;
                int n = await s.ReadAsync(_buf.AsMemory(_len), ct).ConfigureAwait(false);
                if (n == 0) return null;
                _len += n;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _devices.Changed -= DropRemovedDevices;
        _cts.Cancel();
        _tcp.Stop();
        foreach (var ws in _sockets.Keys) ws.Abort();
        await Task.CompletedTask;
        _tcp.Dispose();
        Log.Info(Area, "Stopped sharing the library");
    }
}

/// <summary>The web client, embedded in the assembly (Web/client).</summary>
internal static class ClientFiles
{
    private static readonly Lazy<Dictionary<string, byte[]>> Files = new(Load);

    public static byte[]? Get(string path) => Files.Value.TryGetValue(path, out var b) ? b : null;

    private static Dictionary<string, byte[]> Load()
    {
        var asm = typeof(ClientFiles).Assembly;
        var map = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var name in asm.GetManifestResourceNames())
        {
            if (!name.StartsWith("client/", StringComparison.Ordinal)) continue;
            using var s = asm.GetManifestResourceStream(name)!;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            map[name["client/".Length..].Replace('\\', '/')] = ms.ToArray();
        }
        return map;
    }
}
