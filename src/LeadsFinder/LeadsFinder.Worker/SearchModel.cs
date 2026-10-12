namespace LeadsFinder.Worker
{

    public sealed record SearchJob(
        Guid Id,
        DateTimeOffset ScheduledAtUtc,
        string Source,
        double Latitude,
        double Longitude,
        int RadiusMeters,
        string PlaceType,
        bool RequirePhone,
        bool RequireEmail,
        bool RequireAddress,
        int TargetCount,
        string ShortagePolicy);

    public sealed record FoundPlace(
        string SourceId,
        string Name,
        string Address,
        double Latitude,
        double Longitude,
        string Strategy,
        string SourceLink,
        string? Phone = null,
        string? Email = null,
        string? Website = null,
        string? RawJson = null);
}
