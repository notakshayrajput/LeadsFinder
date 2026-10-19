using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace LeadsFinder.Enrichment;

public sealed class KnownWebsiteStrategy(HtmlPageReader pages) : IEnrichmentStrategy
{
    public string Name => "known-website";

    public async Task<IReadOnlyList<EvidencePage>> FindAsync(EnrichmentInput input, EnrichmentResult current,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(input.Website) || !Uri.TryCreate(input.Website, UriKind.Absolute, out var uri))
            return [];
        var root = new UriBuilder(uri) { Scheme = "https", Port = -1, Path = "/", Query = "", Fragment = "" }.Uri;
        var output = new List<EvidencePage>();
        foreach (var url in new[] { uri.ToString(), root.ToString(), new Uri(root, "contact").ToString(), new Uri(root, "contact-us").ToString() }.Distinct())
        {
            try
            {
                var page = await pages.ReadAsync(url, Name, cancellationToken);
                if (page is not null) output.Add(page);
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            if (output.Count >= 2) break;
        }
        return output;
    }
}

public sealed class OllamaWebSearchStrategy(HttpClient http, HtmlPageReader pages, string apiKey) : IEnrichmentStrategy
{
    public string Name => "ollama-web-search";
    private bool rateLimited;

    public async Task<IReadOnlyList<EvidencePage>> FindAsync(EnrichmentInput input, EnrichmentResult current,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return [];
        if (rateLimited)
        {
            current.Error = "ollama-web-search: HTTP 429";
            return [];
        }
        var output = new List<EvidencePage>();
        var context = input.SearchContext ?? input.Address;
        var locality = context.Length > 120 ? context[..120] : context;
        var queries = new[]
        {
            $"\"{input.Name}\" {locality} official contact phone email address",
            $"\"{input.Name}\" {locality} hotel website contact"
        };
        foreach (var query in queries)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://ollama.com/api/web_search");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Content = JsonContent.Create(new { query, max_results = 3 });
            current.SearchCount++;
            using var response = await http.SendAsync(request, cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                rateLimited = true;
                current.Error = "ollama-web-search: HTTP 429";
                break;
            }
            response.EnsureSuccessStatusCode();
            using var payload = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            if (!payload.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) continue;
            foreach (var item in results.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("url", out var urlElement) || urlElement.ValueKind != JsonValueKind.String)
                    continue;
                var url = urlElement.GetString();
                if (string.IsNullOrWhiteSpace(url) || output.Any(x => x.Url == url)) continue;
                try
                {
                    var page = await pages.ReadAsync(url, Name, cancellationToken);
                    if (page is not null) output.Add(page);
                    else
                    {
                        var snippet = item.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
                        var title = item.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                        if (snippet.Length > 30) output.Add(new EvidencePage(url, title, snippet[..Math.Min(1000, snippet.Length)], Name));
                    }
                }
                catch (HttpRequestException) { }
                catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            }
            if (output.Count >= 2) break;
        }
        return output.Take(3).ToArray();
    }
}
