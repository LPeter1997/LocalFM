using System.Net;

namespace LocalFM.Metadata.Tests;

/// <summary>Answers requests from a URL → (status, file) table and counts calls. No network.</summary>
public sealed class FakeHandler : HttpMessageHandler
{
    private readonly List<(Func<string, bool> Match, Func<HttpResponseMessage> Respond)> _routes = new();
    public List<string> Calls { get; } = new();

    public FakeHandler On(string urlContains, string fixture, HttpStatusCode status = HttpStatusCode.OK)
    {
        _routes.Add((u => u.Contains(urlContains, StringComparison.Ordinal), () => new HttpResponseMessage(status)
        {
            Content = new ByteArrayContent(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture))),
        }));
        return this;
    }

    public FakeHandler OnStatus(string urlContains, params HttpStatusCode[] statuses)
    {
        int i = 0;
        _routes.Add((u => u.Contains(urlContains, StringComparison.Ordinal),
            () => new HttpResponseMessage(statuses[Math.Min(i++, statuses.Length - 1)]) { Content = new StringContent("{}") }));
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        string url = request.RequestUri!.ToString();
        lock (Calls) Calls.Add(url);
        foreach (var (match, respond) in _routes)
            if (match(url)) return Task.FromResult(respond());
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("") });
    }
}
