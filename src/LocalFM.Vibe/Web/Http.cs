using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace LocalFM.Vibe.Web;

/// <summary>One parsed HTTP/1.1 request head (we only take requests without a body).</summary>
public sealed class HttpRequest
{
    public required string Method { get; init; }
    /// <summary>The decoded path, without the query.</summary>
    public required string Path { get; init; }
    public required Dictionary<string, string> Query { get; init; }
    public required Dictionary<string, string> Headers { get; init; }
    public bool KeepAlive { get; init; }

    public string? Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;

    public const int MaxHead = 16 * 1024;

    /// <summary>Parses a request head (everything before the blank line). Null when it is not valid HTTP/1.x.</summary>
    public static HttpRequest? Parse(ReadOnlySpan<byte> head)
    {
        string text;
        try { text = Encoding.ASCII.GetString(head); } catch { return null; }
        var lines = text.Split("\r\n");
        var first = lines[0].Split(' ');
        if (first.Length != 3 || !first[2].StartsWith("HTTP/1.", StringComparison.Ordinal)) return null;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            if (line.Length == 0) continue;
            int colon = line.IndexOf(':');
            if (colon <= 0) return null;
            headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        string target = first[1];
        int q = target.IndexOf('?');
        string rawPath = q < 0 ? target : target[..q];
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        if (q >= 0)
            foreach (var pair in target[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = pair.IndexOf('=');
                string k = Uri.UnescapeDataString((eq < 0 ? pair : pair[..eq]).Replace('+', ' '));
                string v = eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
                query[k] = v;
            }
        if (!rawPath.StartsWith('/')) return null;
        string conn = headers.TryGetValue("Connection", out var c) ? c : "";
        bool keepAlive = first[2] == "HTTP/1.1"
            ? !conn.Contains("close", StringComparison.OrdinalIgnoreCase)
            : conn.Contains("keep-alive", StringComparison.OrdinalIgnoreCase);
        return new HttpRequest
        {
            Method = first[0],
            Path = Uri.UnescapeDataString(rawPath),
            Query = query,
            Headers = headers,
            KeepAlive = keepAlive,
        };
    }
}

public static class Http
{
    /// <summary>A single byte range from a Range header against a resource of <paramref name="length"/> bytes:
    /// null = no (usable) Range header, whole body; (-1, -1) = unsatisfiable (416).</summary>
    public static (long Start, long End)? Range(string? header, long length)
    {
        if (header is null || !header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) return null;
        string spec = header[6..].Trim();
        if (spec.Contains(',')) return null; // multiple ranges: send it all
        int dash = spec.IndexOf('-');
        if (dash < 0) return null;
        string a = spec[..dash].Trim(), b = spec[(dash + 1)..].Trim();
        if (a.Length == 0)
        {
            // Suffix: the last N bytes.
            if (!long.TryParse(b, out long n) || n <= 0) return null;
            if (length == 0) return (-1, -1);
            return (Math.Max(0, length - n), length - 1);
        }
        if (!long.TryParse(a, out long start) || start < 0) return null;
        long end = length - 1;
        if (b.Length > 0 && (!long.TryParse(b, out end) || end < start)) return null;
        if (start >= length) return (-1, -1);
        return (start, Math.Min(end, length - 1));
    }

    public static string WebSocketAccept(string key) =>
        Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key.Trim() + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));

    public static string Reason(int status) => status switch
    {
        101 => "Switching Protocols",
        200 => "OK",
        204 => "No Content",
        206 => "Partial Content",
        304 => "Not Modified",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        416 => "Range Not Satisfiable",
        500 => "Internal Server Error",
        503 => "Service Unavailable",
        _ => "Status",
    };

    public static string ContentType(string pathOrExt) => System.IO.Path.GetExtension(pathOrExt).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".js" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".json" => "application/json",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".ico" => "image/x-icon",
        ".mp3" => "audio/mpeg",
        ".flac" => "audio/flac",
        ".m4a" or ".m4b" or ".mp4" or ".aac" or ".alac" => "audio/mp4",
        ".ogg" or ".oga" or ".opus" or ".spx" => "audio/ogg",
        ".webm" => "audio/webm",
        ".mka" => "audio/x-matroska",
        ".wav" or ".wave" => "audio/wav",
        _ => "application/octet-stream",
    };

    /// <summary>Addresses a library share answers: this machine, home networks (private IPv4, IPv6 ULA,
    /// link-local) and carrier-grade NAT space (VPNs such as Tailscale). Never the open internet.</summary>
    public static bool IsLocalNetwork(IPAddress a)
    {
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (IPAddress.IsLoopback(a)) return true;
        if (a.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = a.GetAddressBytes();
            return b[0] == 10
                || (b[0] == 172 && b[1] is >= 16 and <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 100 && b[1] is >= 64 and <= 127);
        }
        if (a.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = a.GetAddressBytes();
            return a.IsIPv6LinkLocal || (b[0] & 0xFE) == 0xFC;
        }
        return false;
    }
}
