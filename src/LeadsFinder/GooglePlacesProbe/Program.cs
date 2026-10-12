using System.Net;
using System.Text;
using System.Text.Json;
using LeadsFinder.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

var handler = new FakeHandler();
using var http = new HttpClient(handler);
var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
{
    ["GooglePlaces:ApiKey"] = "test-key",
    ["GooglePlaces:MaxSearchRequestsPerJob"] = "10"
}).Build();
var strategy = new GooglePlacesSearchStrategy(http, config,
    NullLogger<GooglePlacesSearchStrategy>.Instance);
var job = new SearchJob(Guid.NewGuid(), DateTimeOffset.UtcNow, 28.6, 77.2, 1000,
    "hotel", "google-places", false, false, true, 1, "return_fewer");
var seen = Enumerable.Range(0, 20).Select(i => $"old-{i}")
    .ToHashSet(StringComparer.Ordinal);
var found = await strategy.SearchAsync(job, seen, CancellationToken.None);
if (handler.SearchRequests != 2 || handler.DetailRequests != 1
    || found.Count != 1 || found[0].SourceId != "fresh"
    || found[0].Phone != "12345" || found[0].Website != "https://example.com")
    throw new Exception("Adaptive search or detail mapping failed.");
Console.WriteLine("Google Places adaptive search probe passed.");

sealed class FakeHandler : HttpMessageHandler
{
    public int SearchRequests { get; private set; }
    public int DetailRequests { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!request.Headers.TryGetValues("X-Goog-Api-Key", out var keys)
            || keys.Single() != "test-key")
            throw new Exception("API key header missing.");
        if (request.Method == HttpMethod.Post)
        {
            SearchRequests++;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var radius = body.RootElement.GetProperty("locationRestriction")
                .GetProperty("circle").GetProperty("radius").GetDouble();
            object[] places = radius >= 1000
                ? Enumerable.Range(0, 20).Select(i => Place($"old-{i}")).ToArray()
                : [Place("fresh")];
            return Json(new { places });
        }
        DetailRequests++;
        return Json(new { nationalPhoneNumber = "12345", websiteUri = "https://example.com" });
    }

    private static object Place(string id) => new
    {
        id,
        displayName = new { text = id },
        formattedAddress = "Kaushambi, Ghaziabad",
        location = new { latitude = 28.6, longitude = 77.2 },
        googleMapsUri = "https://maps.google.com/"
    };

    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
    };
}
