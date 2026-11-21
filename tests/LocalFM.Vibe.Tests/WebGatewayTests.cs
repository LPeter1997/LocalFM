using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using LocalFM.Library;
using LocalFM.Library.Tests;
using LocalFM.Vibe.Library;
using LocalFM.Vibe.Protocol;
using LocalFM.Vibe.Web;

namespace LocalFM.Vibe.Tests;

public sealed class HttpTests
{
    [Theory]
    [InlineData(null, 100, null, null)]
    [InlineData("bytes=0-9", 100, 0L, 9L)]
    [InlineData("bytes=10-", 100, 10L, 99L)]
    [InlineData("bytes=90-200", 100, 90L, 99L)]
    [InlineData("bytes=-10", 100, 90L, 99L)]
    [InlineData("bytes=-500", 100, 0L, 99L)]
    [InlineData("bytes=100-", 100, -1L, -1L)]
    [InlineData("bytes=5-2", 100, null, null)]
    [InlineData("bytes=0-1,5-6", 100, null, null)]
    [InlineData("items=0-1", 100, null, null)]
    public void Ranges(string? header, long length, long? start, long? end)
    {
        var r = Http.Range(header, length);
        Assert.Equal(start, r?.Start);
        Assert.Equal(end, r?.End);
    }

    [Fact]
    public void WebSocket_accept_key_matches_the_rfc_example() =>
        Assert.Equal("s3pPLMBiTxaQ9kYGzzhZRbK+xOo=", Http.WebSocketAccept("dGhlIHNhbXBsZSBub25jZQ=="));

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("192.168.1.20", true)]
    [InlineData("10.0.0.5", true)]
    [InlineData("172.20.1.1", true)]
    [InlineData("100.101.102.103", true)]
    [InlineData("::ffff:192.168.0.2", true)]
    [InlineData("fd12::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("::1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("172.32.0.1", false)]
    [InlineData("2001:db8::1", false)]
    public void Local_networks_only(string ip, bool local) => Assert.Equal(local, Http.IsLocalNetwork(IPAddress.Parse(ip)));

    [Fact]
    public void Parses_request_heads()
    {
        var r = HttpRequest.Parse("GET /media/AB%20C?k=x%2By&z HTTP/1.1\r\nHost: a\r\nRange: bytes=0-\r\n"u8)!;
        Assert.Equal("GET", r.Method);
        Assert.Equal("/media/AB C", r.Path);
        Assert.Equal("x+y", r.Query["k"]);
        Assert.Equal("", r.Query["z"]);
        Assert.Equal("bytes=0-", r.Header("range"));
        Assert.True(r.KeepAlive);
        Assert.Null(HttpRequest.Parse("garbage"u8));
        Assert.False(HttpRequest.Parse("GET / HTTP/1.1\r\nConnection: close"u8)!.KeepAlive);
    }

    [Fact]
    public void Wav_header_describes_the_data()
    {
        var h = WavStreamer.Header(44100, 1000);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(h, 0, 4));
        Assert.Equal(36 + 4000, BitConverter.ToInt32(h, 4));
        Assert.Equal(44100, BitConverter.ToInt32(h, 24));
        Assert.Equal(4000, BitConverter.ToInt32(h, 40));
    }
}

public sealed class WebGatewayTests : IAsyncLifetime
{
    private TempLibrary _lib = null!;
    private WebGateway _gw = null!;
    private PairedDevices _devices = null!;
    private HttpClient _http = null!;
    private string _base = "";

    public async Task InitializeAsync()
    {
        _lib = new TempLibrary();
        if (!TempLibrary.HaveFfmpeg) return;
        _lib.MakeTrack("a/1.mp3", new() { ["title"] = "One", ["artist"] = "X", ["album"] = "A", ["track"] = "1" }, 2.0, _lib.MakePng());
        _lib.MakeTrack("a/2.flac", new() { ["title"] = "Two", ["artist"] = "X", ["album"] = "A", ["track"] = "2" }, 3.0);
        await _lib.ScanAsync();
        var media = new MediaResolver(_lib.Db, _lib.Images, _lib.Queries);
        var api = new LibraryApi(_lib.Db, _lib.Images, _lib.Queries, new Playlists(_lib.Db), media, (_, _) => Task.FromResult<LocalFM.Metadata.LyricsView?>(null));
        _devices = new PairedDevices(null);
        _gw = new WebGateway(api, _devices, "test-host", port: 0);
        _base = $"http://127.0.0.1:{_gw.Port}";
        _http = new HttpClient { BaseAddress = new Uri(_base) };
    }

    public async Task DisposeAsync()
    {
        _http?.Dispose();
        if (_gw is not null) await _gw.DisposeAsync();
        _lib.Dispose();
    }

    private sealed class Client(ClientWebSocket ws) : IDisposable
    {
        public async Task SendAsync(Message m) =>
            await ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(m, WireJson.Default.Message), WebSocketMessageType.Text, true, default);

        public async Task<Message?> ReceiveAsync()
        {
            var buf = new byte[4 << 20];
            int n = 0;
            ValueWebSocketReceiveResult r;
            do
            {
                r = await ws.ReceiveAsync(buf.AsMemory(n), new CancellationTokenSource(10_000).Token);
                if (r.MessageType == WebSocketMessageType.Close) return null;
                n += r.Count;
            } while (!r.EndOfMessage);
            return JsonSerializer.Deserialize(buf.AsSpan(0, n), WireJson.Default.Message);
        }

        public void Dispose() => ws.Dispose();
    }

    private async Task<Client> ConnectAsync()
    {
        var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{_gw.Port}/ws"), default);
        return new Client(ws);
    }

    private async Task<(Client Client, Welcome Welcome)> PairAsync()
    {
        string pin = _gw.NewPin();
        var c = await ConnectAsync();
        await c.SendAsync(new Hello(Wire.Version, ConnectionRole.Control, "pin:" + pin, "Living room TV"));
        return (c, Assert.IsType<Welcome>(await c.ReceiveAsync()));
    }

    [SkippableFact]
    public async Task Pairs_browses_and_streams()
    {
        Skip.IfNot(TempLibrary.HaveFfmpeg);
        var (c, welcome) = await PairAsync();
        using var _ = c;
        Assert.Equal("test-host", welcome.HostName);
        Assert.Null(_gw.Pin); // single use
        Assert.Equal("Living room TV", Assert.Single(_devices.List).Name);

        await c.SendAsync(new LibraryRequest(1, LibraryOp.Tracks));
        var tracks = Assert.IsType<LibraryReply>(await c.ReceiveAsync()).Tracks!;
        var one = tracks.Single(t => t.Title == "One");
        string token = Uri.EscapeDataString(welcome.DataToken);

        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.GetAsync($"/media/{one.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.GetAsync($"/media/{one.Id}?k=nope")).StatusCode);

        var file = await File.ReadAllBytesAsync(_lib.Queries.ByTrack(LocalFM.Core.Ulid.Parse(one.Id))!.Path);
        var req = new HttpRequestMessage(HttpMethod.Get, $"/media/{one.Id}?k={token}");
        req.Headers.Range = new RangeHeaderValue(10, 19);
        var res = await _http.SendAsync(req);
        Assert.Equal(HttpStatusCode.PartialContent, res.StatusCode);
        Assert.Equal("audio/mpeg", res.Content.Headers.ContentType!.MediaType);
        Assert.Equal(file[10..20], await res.Content.ReadAsByteArrayAsync());
        Assert.Equal(file, await _http.GetByteArrayAsync($"/media/{one.Id}?k={token}"));

        var cover = await _http.GetAsync($"/img/{one.Cover}?k={token}");
        Assert.Equal(HttpStatusCode.OK, cover.StatusCode);

        // A device that is no longer paired is locked out.
        _devices.Remove(welcome.ListenerId);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.GetAsync($"/media/{one.Id}?k={token}")).StatusCode);
    }

    [SkippableFact]
    public async Task Signs_in_with_the_token_and_rejects_bad_secrets()
    {
        Skip.IfNot(TempLibrary.HaveFfmpeg);
        var (first, welcome) = await PairAsync();
        first.Dispose();

        using (var c = await ConnectAsync())
        {
            await c.SendAsync(new Hello(Wire.Version, ConnectionRole.Control, welcome.DataToken, "TV"));
            Assert.Equal(welcome.ListenerId, Assert.IsType<Welcome>(await c.ReceiveAsync()).ListenerId);
            await c.SendAsync(new Ping(42));
            Assert.Equal(42, Assert.IsType<Pong>(await c.ReceiveAsync()).T0);
        }
        using (var c = await ConnectAsync())
        {
            await c.SendAsync(new Hello(Wire.Version, ConnectionRole.Control, "pin:000000", "TV"));
            Assert.IsType<Rejected>(await c.ReceiveAsync());
        }
        using (var c = await ConnectAsync())
        {
            await c.SendAsync(new Hello(Wire.Version, ConnectionRole.Control, "stolen", "TV"));
            Assert.IsType<Rejected>(await c.ReceiveAsync());
        }
        // Wrong PINs close the pairing window.
        _gw.NewPin();
        for (int i = 0; i < 5; i++)
        {
            using var c = await ConnectAsync();
            await c.SendAsync(new Hello(Wire.Version, ConnectionRole.Control, "pin:x" + i, "TV"));
            Assert.IsType<Rejected>(await c.ReceiveAsync());
        }
        Assert.Null(_gw.Pin);
    }

    [SkippableFact]
    public async Task Serves_the_client_and_info()
    {
        Skip.IfNot(TempLibrary.HaveFfmpeg);
        var index = await _http.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, index.StatusCode);
        Assert.Equal("text/html", index.Content.Headers.ContentType!.MediaType);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync("/../../etc/passwd")).StatusCode);
        var info = await _http.GetStringAsync("/api/info");
        Assert.Contains("\"name\":\"test-host\"", info);
        var options = await _http.SendAsync(new HttpRequestMessage(HttpMethod.Options, "/media/x"));
        Assert.Equal(HttpStatusCode.NoContent, options.StatusCode);
        Assert.Equal("*", options.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }

    [SkippableFact]
    public async Task Wav_fallback_is_seekable_and_byte_exact()
    {
        Skip.IfNot(TempLibrary.HaveFfmpeg);
        var (c, welcome) = await PairAsync();
        using var _ = c;
        await c.SendAsync(new LibraryRequest(1, LibraryOp.Tracks));
        var two = Assert.IsType<LibraryReply>(await c.ReceiveAsync()).Tracks!.Single(t => t.Title == "Two");
        string url = $"/media/{two.Id}.wav?k={Uri.EscapeDataString(welcome.DataToken)}";

        var whole = await _http.GetByteArrayAsync(url);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(whole, 0, 4));
        Assert.Equal(44100, BitConverter.ToInt32(whole, 24));
        Assert.Equal(whole.Length - 44, BitConverter.ToInt32(whole, 40));
        Assert.InRange((whole.Length - 44) / 4, 44100 * 3 - 10, 44100 * 3 + 10);

        foreach (var (from, to) in new[] { (0L, 99L), (30L, 60L), (100_002L, 200_001L), ((long)whole.Length - 7, (long)whole.Length - 1) })
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Range = new RangeHeaderValue(from, to);
            var res = await _http.SendAsync(req);
            Assert.Equal(HttpStatusCode.PartialContent, res.StatusCode);
            Assert.Equal(whole[(int)from..(int)(to + 1)], await res.Content.ReadAsByteArrayAsync());
        }
    }
}
