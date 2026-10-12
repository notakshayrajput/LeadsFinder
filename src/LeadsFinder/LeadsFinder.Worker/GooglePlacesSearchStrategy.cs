using System.Net.Http.Json;
using System.Text.Json;

namespace LeadsFinder.Worker;

/// <summary>
/// Uses Nearby Search (New) to discover places, refining only saturated circles.
/// Contact details are requested once per selected, previously unseen place.
/// </summary>
public sealed class GooglePlacesSearchStrategy(
    HttpClient http,
    IConfiguration configuration,
    ILogger<GooglePlacesSearchStrategy> logger) : ISearchStrategy
{
    private const int ResultLimit = 20;
    private const double MinimumCircleRadiusMeters = 100;
    private const double MaximumSearchRadiusMeters = 50_000;
    private const string SearchFields =
        "places.id,places.displayName,places.formattedAddress,places.location," +
        "places.googleMapsUri,places.primaryType,places.types";
    private const string DetailFields = "nationalPhoneNumber,websiteUri";
    private static readonly Uri SearchUri = new("https://places.googleapis.com/v1/places:searchNearby");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string Name => "google-places";

    public async Task<IReadOnlyList<FoundPlace>> SearchAsync(
        SearchJob job,
        IReadOnlySet<string> excludedSourceIds,
        CancellationToken cancellationToken)
    {
        var apiKey = configuration["GooglePlaces:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new GooglePlacesSourceException("Set GooglePlaces__ApiKey in the worker environment.");

        var maxSearchRequests = Math.Clamp(
            configuration.GetValue<int?>("GooglePlaces:MaxSearchRequestsPerJob") ?? 100, 1, 500);
        var maxDetailRequests = Math.Clamp(
            configuration.GetValue<int?>("GooglePlaces:MaxDetailRequestsPerJob") ?? 100, 0, 500);
        var types = PlaceTypes(job.PlaceType);
        var candidates = new Dictionary<string, Candidate>(StringComparer.Ordinal);
        var searchRequests = 0;
        var saturatedAtMinimum = 0;

        async Task SearchRegionAsync(double radiusMeters)
        {
            var circles = new Queue<SearchCircle>();
            circles.Enqueue(new SearchCircle(job.Latitude, job.Longitude, radiusMeters));
            while (circles.Count > 0 && searchRequests < maxSearchRequests
                && candidates.Count < job.TargetCount)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var circle = circles.Dequeue();
                var places = await SearchCircleAsync(circle, types, apiKey, cancellationToken);
                searchRequests++;
                foreach (var place in places)
                {
                    if (string.IsNullOrWhiteSpace(place.Id) || excludedSourceIds.Contains(place.Id)
                        || candidates.ContainsKey(place.Id) || place.Location is null
                        || string.IsNullOrWhiteSpace(place.DisplayName?.Text)
                        || !double.IsFinite(place.Location.Latitude)
                        || !double.IsFinite(place.Location.Longitude))
                        continue;

                    var distance = DistanceMeters(job.Latitude, job.Longitude,
                        place.Location.Latitude, place.Location.Longitude);
                    if (distance <= radiusMeters)
                        candidates.Add(place.Id, new Candidate(place, distance));
                }

                // A full response might hide more places. Only then do smaller
                // overlapping circles pay for themselves. Four children cover the
                // parent circle, including the gaps between their centers.
                if (places.Count == ResultLimit
                    && circle.RadiusMeters / Math.Sqrt(2) >= MinimumCircleRadiusMeters)
                    foreach (var child in Split(circle))
                        circles.Enqueue(child);
                else if (places.Count == ResultLimit)
                    saturatedAtMinimum++;
            }
        }

        await SearchRegionAsync(job.RadiusMeters);
        if (job.ShortagePolicy == "expand_radius" && candidates.Count < job.TargetCount
            && job.RadiusMeters < MaximumSearchRadiusMeters && searchRequests < maxSearchRequests)
            await SearchRegionAsync(MaximumSearchRadiusMeters);

        var selected = candidates.Values
            .OrderBy(x => x.DistanceMeters)
            .ThenBy(x => x.Place.Id, StringComparer.Ordinal)
            .Take(job.TargetCount)
            .ToArray();
        var observations = new List<FoundPlace>(selected.Length);
        for (var index = 0; index < selected.Length; index++)
        {
            var place = selected[index].Place;
            GooglePlace? details = null;
            if (index < maxDetailRequests)
                details = await GetDetailsAsync(place.Id!, apiKey, cancellationToken);

            observations.Add(new FoundPlace(
                place.Id!, place.DisplayName!.Text!, place.FormattedAddress ?? "",
                place.Location!.Latitude, place.Location.Longitude, Name,
                place.GoogleMapsUri ?? $"https://www.google.com/maps/place/?q=place_id:{Uri.EscapeDataString(place.Id!)}",
                details?.NationalPhoneNumber, null, details?.WebsiteUri,
                JsonSerializer.Serialize(new { search = place, details }, JsonOptions)));
        }

        logger.LogInformation(
            "Google Places search {JobId}: {Count} observations, {SearchRequests} nearby requests, " +
            "{DetailRequests} details requests, {SaturatedCircles} saturated 200m circles",
            job.Id, observations.Count, searchRequests,
            Math.Min(selected.Length, maxDetailRequests), saturatedAtMinimum);
        if (searchRequests >= maxSearchRequests || saturatedAtMinimum > 0)
            logger.LogWarning(
                "Google Places search {JobId} may be incomplete: request cap or 200m refinement floor reached",
                job.Id);
        return observations;
    }

    private async Task<IReadOnlyList<GooglePlace>> SearchCircleAsync(
        SearchCircle circle, string[] types, string apiKey, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, SearchUri);
        request.Headers.Add("X-Goog-Api-Key", apiKey);
        request.Headers.Add("X-Goog-FieldMask", SearchFields);
        request.Content = JsonContent.Create(new
        {
            includedTypes = types,
            maxResultCount = ResultLimit,
            rankPreference = "DISTANCE",
            locationRestriction = new
            {
                circle = new
                {
                    center = new { latitude = circle.Latitude, longitude = circle.Longitude },
                    radius = circle.RadiusMeters
                }
            }
        });
        using var response = await http.SendAsync(request, token);
        if (!response.IsSuccessStatusCode)
            throw new GooglePlacesSourceException($"Nearby Search returned HTTP {(int)response.StatusCode}.");
        var body = await response.Content.ReadFromJsonAsync<NearbyResponse>(JsonOptions, token);
        return body?.Places ?? [];
    }

    private async Task<GooglePlace?> GetDetailsAsync(string id, string apiKey, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://places.googleapis.com/v1/places/{Uri.EscapeDataString(id)}");
        request.Headers.Add("X-Goog-Api-Key", apiKey);
        request.Headers.Add("X-Goog-FieldMask", DetailFields);
        using var response = await http.SendAsync(request, token);
        if (!response.IsSuccessStatusCode)
            throw new GooglePlacesSourceException($"Place Details returned HTTP {(int)response.StatusCode}.");
        return await response.Content.ReadFromJsonAsync<GooglePlace>(JsonOptions, token);
    }

    private static IEnumerable<SearchCircle> Split(SearchCircle parent)
    {
        var offset = parent.RadiusMeters / 2;
        var childRadius = parent.RadiusMeters / Math.Sqrt(2);
        var latitudeOffset = offset / 111_320;
        var longitudeOffset = offset /
            (111_320 * Math.Max(0.01, Math.Cos(parent.Latitude * Math.PI / 180)));
        foreach (var latitudeSign in new[] { -1, 1 })
            foreach (var longitudeSign in new[] { -1, 1 })
                yield return new SearchCircle(
                    Math.Clamp(parent.Latitude + latitudeSign * latitudeOffset, -90, 90),
                    Math.Clamp(parent.Longitude + longitudeSign * longitudeOffset, -180, 180),
                    childRadius);
    }

    private static string[] PlaceTypes(string type) => type switch
    {
        "hotel" => ["hotel", "motel", "resort_hotel", "extended_stay_hotel"],
        "bed_and_breakfast" => ["bed_and_breakfast"],
        "guest_house" => ["guest_house"],
        "vacation_rental" => ["cottage", "private_guest_room", "farmstay"],
        _ => ["lodging", "hotel", "motel", "resort_hotel", "extended_stay_hotel",
            "bed_and_breakfast", "guest_house", "hostel", "inn", "cottage",
            "private_guest_room", "farmstay"]
    };

    private static double DistanceMeters(double latitude1, double longitude1,
        double latitude2, double longitude2)
    {
        const double earthRadius = 6_371_000;
        var lat = (latitude2 - latitude1) * Math.PI / 180;
        var lon = (longitude2 - longitude1) * Math.PI / 180;
        var a = Math.Pow(Math.Sin(lat / 2), 2)
            + Math.Cos(latitude1 * Math.PI / 180)
            * Math.Cos(latitude2 * Math.PI / 180)
            * Math.Pow(Math.Sin(lon / 2), 2);
        return 2 * earthRadius * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }

    private sealed record SearchCircle(double Latitude, double Longitude, double RadiusMeters);
    private sealed record Candidate(GooglePlace Place, double DistanceMeters);
    private sealed record NearbyResponse(List<GooglePlace>? Places);
    private sealed record PlaceName(string? Text);
    private sealed record PlaceLocation(double Latitude, double Longitude);
    private sealed record GooglePlace(
        string? Id, PlaceName? DisplayName, string? FormattedAddress,
        PlaceLocation? Location, string? GoogleMapsUri, string? PrimaryType,
        string[]? Types, string? NationalPhoneNumber, string? WebsiteUri);
}

public sealed class GooglePlacesSourceException(string message) : Exception(message);
