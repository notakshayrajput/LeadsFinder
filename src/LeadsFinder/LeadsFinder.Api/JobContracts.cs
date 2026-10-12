using LeadsFinder.Data;

namespace LeadsFinder.Api;

public sealed record CreateJobRequest(
    string? PlaceType,
    double Latitude,
    double Longitude,
    int RadiusMeters,
    bool RequirePhone,
    bool RequireEmail,
    bool RequireAddress,
    int TargetCount,
    string? ShortagePolicy,
    DateTimeOffset ScheduledAtUtc,
    string? Source = null);

public sealed record JobResponse(
    Guid Id,
    string Status,
    string Source,
    string PlaceType,
    double Latitude,
    double Longitude,
    int RadiusMeters,
    bool RequirePhone,
    bool RequireEmail,
    bool RequireAddress,
    int TargetCount,
    string ShortagePolicy,
    DateTime CreatedAtUtc,
    DateTime ScheduledAtUtc,
    DateTime? StartedAtUtc,
    DateTime? FinishedAtUtc,
    int AttemptCount,
    string? LastError,
    int QuarantinedCount)
{
    public static JobResponse From(SearchJobEntity job, int count) => new(
        job.Id, job.Status, job.Source, job.PlaceType, job.Latitude, job.Longitude,
        job.RadiusMeters, job.RequirePhone, job.RequireEmail, job.RequireAddress,
        job.TargetCount, job.ShortagePolicy, job.CreatedAtUtc, job.ScheduledAtUtc,
        job.StartedAtUtc, job.FinishedAtUtc, job.AttemptCount, job.LastError, count);
}

public sealed record SourceRecordResponse(
    Guid Id,
    Guid SearchJobId,
    string Strategy,
    string SourceId,
    string SourceLink,
    string Name,
    string Address,
    double Latitude,
    double Longitude,
    string? Phone,
    string? Email,
    string? Website,
    DateTime FetchedAtUtc,
    string RawJson);

public sealed record PageResponse<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);
