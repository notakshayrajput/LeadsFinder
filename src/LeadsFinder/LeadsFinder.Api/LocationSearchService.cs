using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace LeadsFinder.Api;

public sealed record LocationSearchResult(string Id, string DisplayName, double Latitude, double Longitude);

/// <summary>
/// Proxies explicit user searches to Nominatim. Requests are cached and serialized
/// to respect the public service's one-request-per-second application limit.
/// </summary>
public sealed class LocationSearchService : IDisposable
{
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Uri _baseUri;
    private DateTimeOffset _lastRequestUtc = DateTimeOffset.MinValue;

    public LocationSearchService(IConfiguration configuration)
    {
        _baseUri = new Uri(configuration["Geocoding:BaseUrl"]
            ?? "https://nominatim.openstreetmap.org/");
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("LeadsFinder", "0.1"));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en");
    }

    public async Task<IReadOnlyList<LocationSearchResult>> SearchAsync(string query, CancellationToken token)
    {
        var normalized = query.Trim();
        if (_cache.TryGetValue(normalized, out var cached)
            && cached.ExpiresAtUtc > DateTimeOffset.UtcNow)
            return cached.Results;

        await _gate.WaitAsync(token);
        try
        {
            if (_cache.TryGetValue(normalized, out cached)
                && cached.ExpiresAtUtc > DateTimeOffset.UtcNow)
                return cached.Results;

            var wait = _lastRequestUtc.AddSeconds(1) - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, token);

            var uri = new Uri(_baseUri,
                $"search?format=jsonv2&limit=5&q={Uri.EscapeDataString(normalized)}");
            _lastRequestUtc = DateTimeOffset.UtcNow;
            using var response = await _http.GetAsync(uri, token);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: token);
            if (json.RootElement.ValueKind != JsonValueKind.Array)
                throw new JsonException("Place search returned an unexpected response.");
            var results = new List<LocationSearchResult>();
            foreach (var row in json.RootElement.EnumerateArray())
            {
                if (!row.TryGetProperty("lat", out var latValue)
                    || !row.TryGetProperty("lon", out var lonValue)
                    || !double.TryParse(latValue.GetString(), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out var latitude)
                    || !double.TryParse(lonValue.GetString(), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out var longitude)
                    || !double.IsFinite(latitude) || latitude is < -90 or > 90
                    || !double.IsFinite(longitude) || longitude is < -180 or > 180)
                    continue;

                var name = row.GetProperty("display_name").GetString();
                if (string.IsNullOrWhiteSpace(name)) continue;
                var id = row.TryGetProperty("place_id", out var placeId)
                    ? placeId.ToString() : $"{latitude:F6},{longitude:F6}";
                results.Add(new LocationSearchResult(id, name, latitude, longitude));
            }

            var output = results.ToArray();
            _cache[normalized] = new CacheEntry(output, DateTimeOffset.UtcNow.AddHours(24));
            return output;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _gate.Dispose();
        _http.Dispose();
    }

    private sealed record CacheEntry(IReadOnlyList<LocationSearchResult> Results, DateTimeOffset ExpiresAtUtc);
}
