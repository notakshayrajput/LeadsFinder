namespace LeadsFinder.Worker;

public static class SearchBudget
{
    public static TimeSpan For(string source, int radiusMeters, string shortagePolicy)
    {
        if (source != "google-maps-scraper")
            return TimeSpan.FromMinutes(10);

        if (shortagePolicy == "expand_radius" && radiusMeters < 50_000)
            return TimeSpan.FromMinutes(90);

        var minutes = radiusMeters switch
        {
            <= 1_000 => 6,
            <= 5_000 => 12,
            <= 15_000 => 25,
            <= 30_000 => 50,
            _ => 90
        };
        return TimeSpan.FromMinutes(minutes);
    }
}
