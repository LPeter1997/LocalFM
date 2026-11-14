using System.Net;
using LocalFM.Core;
using LocalFM.Library;

namespace LocalFM.Metadata;

public sealed record HttpResult(int Status, byte[] Body, string? ContentType, bool FromCache)
{
    public bool Ok => Status is >= 200 and < 300;
}

/// <summary>Per-provider politeness: minimum spacing between requests (e.g. MusicBrainz: 1/s).</summary>
public sealed class RateLimit(TimeSpan interval)
{
    private readonly TimeSpan _interval = interval;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime _next = DateTime.MinValue;

    public async Task<IDisposable> WaitAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        var wait = _next - DateTime.UtcNow;
        if (wait > TimeSpan.Zero) await Task.Delay(wait, ct).ConfigureAwait(false);
        return new Releaser(this);
    }

    private sealed class Releaser(RateLimit owner) : IDisposable
    {
        public void Dispose()
        {
            owner._next = DateTime.UtcNow + owner._interval;
            owner._gate.Release();
        }
    }
}

/// <summary>
/// HTTP GET through the knowledge base's http_cache: a request answered once (including "not found")
/// is not repeated until it expires. Free services get one request at a time, spaced by their limit,
/// with backoff when they say they are busy.
/// </summary>
public sealed class CachedHttp(Database db, HttpClient http)
{
    public const string DefaultUserAgent = "LocalFM/0.1 ( local music player )";

    private readonly Dictionary<string, RateLimit> _limits = new();
    private readonly Lock _lock = new();

    /// <summary>Raised for every request that actually goes to the network (for status display).</summary>
    public event Action<string, string>? Requesting;

    /// <summary>Raised when a service says it is busy and we wait before retrying (provider, delay).</summary>
    public event Action<string, TimeSpan>? Retrying;

    public void SetRateLimit(string provider, TimeSpan interval)
    {
        lock (_lock) _limits[provider] = new RateLimit(interval);
    }

    private RateLimit Limit(string provider)
    {
        lock (_lock)
        {
            if (!_limits.TryGetValue(provider, out var l)) _limits[provider] = l = new RateLimit(TimeSpan.FromSeconds(1));
            return l;
        }
    }

    /// <param name="cache">False for large binary payloads (images) that are stored elsewhere.</param>
    public async Task<HttpResult> GetAsync(string provider, string url, TimeSpan ttl, bool cache = true, CancellationToken ct = default)
    {
        if (cache && Cached(provider, url) is { } hit) return hit;

        HttpResult result = new(0, [], null, false);
        for (int attempt = 0; attempt < 4; attempt++)
        {
            using (await Limit(provider).WaitAsync(ct).ConfigureAwait(false))
            {
                Requesting?.Invoke(provider, url);
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
                    var body = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                    result = new((int)resp.StatusCode, body, resp.Content.Headers.ContentType?.MediaType, false);
                }
                catch (HttpRequestException ex)
                {
                    Log.Warn("Http", $"{provider}: {ex.Message}");
                    return new(0, [], null, false); // offline: not cached, try again another time
                }
            }
            if (result.Status is (int)HttpStatusCode.ServiceUnavailable or (int)HttpStatusCode.TooManyRequests && attempt < 3)
            {
                var delay = TimeSpan.FromSeconds(1.5 * (1 << attempt)); // 1.5, 3, 6 s
                Log.Info("Http", $"{provider} busy ({result.Status}), retrying in {delay.TotalSeconds:0.#} s");
                Retrying?.Invoke(provider, delay);
                await Task.Delay(delay, ct).ConfigureAwait(false);
                continue;
            }
            break;
        }

        // Cache answers, including "not found" (for less time), but not server trouble.
        if (cache && (result.Ok || result.Status == 404))
        {
            var expires = DateTimeOffset.UtcNow + (result.Ok ? ttl : TimeSpan.FromDays(3));
            _ = db.Writer.Run((c, t) => new Tx(c, t).Exec("""
                INSERT OR REPLACE INTO http_cache(provider, request_key, status, body, fetched_at, expires_at)
                VALUES ($1,$2,$3,$4,$5,$6)
                """, provider, url, result.Status, result.Body, Sql.Now(), expires.ToUnixTimeMilliseconds()));
        }
        return result;
    }

    private HttpResult? Cached(string provider, string url)
    {
        using var c = db.Open();
        return c.QuerySingle("SELECT status, body, expires_at FROM http_cache WHERE provider = $1 AND request_key = $2",
            r => r.GetInt64(2) > Sql.Now()
                ? new HttpResult(r.GetInt32(0), r.IsDBNull(1) ? [] : (byte[])r.GetValue(1), null, true)
                : null,
            provider, url);
    }

    public static HttpClient CreateClient(string userAgent = DefaultUserAgent)
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        })
        { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }
}
