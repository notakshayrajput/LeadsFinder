namespace LeadsFinder.Api;

public static class JobValidation
{
    public static readonly string[] PlaceTypes =
        ["any_lodging", "hotel", "bed_and_breakfast", "guest_house", "vacation_rental"];

    public static readonly string[] ShortagePolicies = ["return_fewer", "expand_radius"];

    public static Dictionary<string, string[]> Validate(CreateJobRequest request, DateTimeOffset now)
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(request.PlaceType) || !PlaceTypes.Contains(request.PlaceType))
            errors[nameof(request.PlaceType)] = ["Choose a supported lodging type."];

        if (request.Source is not null && request.Source is not ("geofabrik-osm" or "google-maps-scraper"))
            errors[nameof(request.Source)] = ["Choose OpenStreetMap or Google Maps Scraper."];

        if (!double.IsFinite(request.Latitude) || request.Latitude is < -90 or > 90)
            errors[nameof(request.Latitude)] = ["Latitude must be between -90 and 90."];

        if (!double.IsFinite(request.Longitude) || request.Longitude is < -180 or > 180)
            errors[nameof(request.Longitude)] = ["Longitude must be between -180 and 180."];

        if (request.RadiusMeters is < 100 or > 50_000)
            errors[nameof(request.RadiusMeters)] = ["Radius must be between 100 and 50,000 meters."];

        if (request.TargetCount is < 1 or > 500)
            errors[nameof(request.TargetCount)] = ["Requested rows must be between 1 and 500."];

        if (!request.RequirePhone && !request.RequireEmail && !request.RequireAddress)
            errors["requiredFields"] = ["Select at least one desired field."];

        if (string.IsNullOrWhiteSpace(request.ShortagePolicy) || !ShortagePolicies.Contains(request.ShortagePolicy))
            errors[nameof(request.ShortagePolicy)] = ["Choose a supported shortage policy."];

        if (request.ScheduledAtUtc == default
            || request.ScheduledAtUtc < now.AddMinutes(-5)
            || request.ScheduledAtUtc > now.AddYears(1))
            errors[nameof(request.ScheduledAtUtc)] = ["Schedule within the next year (up to five minutes in the past is allowed)."];

        return errors;
    }
}
