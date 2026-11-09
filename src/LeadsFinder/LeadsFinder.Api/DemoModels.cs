namespace LeadsFinder.Api;

public sealed record DemoRegisterRequest(string? Name, string? Email, string? Password);
public sealed record DemoLoginRequest(string? Email, string? Password);
public record DemoUserResponse(Guid Id, string Name, string Email, string Role, bool Approved, bool Blocked,
    DateTime CreatedAtUtc, DateTime? LastLoginAtUtc, DateTime? TrialEndsAtUtc, DateTime? DeletionAtUtc,
    int QuarantineLimit, int CleanLimit, int QuarantineCount, int CleanCount, string Status);
public sealed record DemoAdminUserResponse : DemoUserResponse
{
    public int TotalJobs { get; init; }
    public DemoAdminUserResponse(DemoUserResponse user, int totalJobs)
        : base(user.Id, user.Name, user.Email, user.Role, user.Approved, user.Blocked, user.CreatedAtUtc,
            user.LastLoginAtUtc, user.TrialEndsAtUtc, user.DeletionAtUtc, user.QuarantineLimit, user.CleanLimit,
            user.QuarantineCount, user.CleanCount, user.Status) => TotalJobs = totalJobs;
}
public sealed record DemoUpdateUserRequest(bool? Approved, bool? Blocked, int? QuarantineLimit, int? CleanLimit);
public sealed record DemoAdminOverviewResponse(int TotalUsers, int ApprovedUsers, int BlockedUsers, int LockedUsers,
    int TotalJobs, int GlobalRecords, int CatalogSize, int GeneratedRows, DemoGenerationBatchResponse[] GenerationBatches);
public sealed record DemoCreateJobRequest(double Latitude, double Longitude, int RadiusMeters, int TargetCount, string? PlaceType);
public sealed record DemoCreateRecordRequest(string? Name, string? Address, double Latitude, double Longitude,
    string? Phone, string? Email, string? Website, Guid? JobId);
public sealed record DemoPage<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);
public sealed record DemoLeadResponse(Guid Id, string Name, string? Address, double Latitude, double Longitude,
    string? Phone, string? Email, string? Website, string Locality, string PlaceType, string Status,
    Guid JobId, string JobName, DateTime FoundAtUtc, DateTime? EnrichedAtUtc, string[] MissingFields)
{
    public DemoSalesResponse Sales { get; init; } = new("new", false, false, false, false, "", null);
    public Dictionary<string, DemoFieldQuality> FieldQuality { get; init; } = [];
    public bool IsSample { get; init; }
}
public sealed record DemoJobResponse(Guid Id, string Name, string Status, string PlaceType, double Latitude,
    double Longitude, int RadiusMeters, int TargetCount, int FoundCount, int QuarantineCount, int CleanCount,
    int DuplicatesRemoved, int ProcessedCount, DateTime CreatedAtUtc, DateTime? FinishedAtUtc, string? LastError);
public sealed record DemoDashboardResponse(int TotalRecords, int EnrichedRecords, int QuarantineRecords,
    int SalesLeads, int TotalJobs, int ActiveJobs, int CatalogSize, DemoJobResponse[] RecentJobs)
{
    public int UncontactedLeads { get; init; }
    public int PossibleSales { get; init; }
    public int ContactedLeads { get; init; }
    public int DoNotContactLeads { get; init; }
}

public sealed record DemoSalesResponse(string Stage, bool ContactedOnEmail, bool ContactedOnPhone,
    bool ContactedOnWhatsapp, bool DoNotContact, string Notes, DateTime? UpdatedAtUtc);
public sealed record DemoUpdateSalesRequest(string? Stage, bool? ContactedOnEmail, bool? ContactedOnPhone,
    bool? ContactedOnWhatsapp, bool? DoNotContact, string? Notes);
public sealed record DemoFieldQuality(string? Confidence, int InaccurateReports, bool ReportedByMe, string CommunitySignal);
public sealed record DemoReviewFieldRequest(string? Field, string? Value, string? Confidence, bool? Inaccurate, string? Reason);

public sealed class DemoSalesProfile
{
    public Guid UserId { get; set; }
    public string BusinessKey { get; set; } = "";
    public string Stage { get; set; } = "new";
    public bool ContactedOnEmail { get; set; }
    public bool ContactedOnPhone { get; set; }
    public bool ContactedOnWhatsapp { get; set; }
    public bool DoNotContact { get; set; }
    public string Notes { get; set; } = "";
    public DateTime? UpdatedAtUtc { get; set; }
}

// Feedback is tied to the exact value, so a correction does not inherit old votes.
public sealed class DemoFieldReview
{
    public Guid UserId { get; set; }
    public string BusinessKey { get; set; } = "";
    public string Field { get; set; } = "";
    public string Value { get; set; } = "";
    public string? Confidence { get; set; }
    public bool Inaccurate { get; set; }
    public string? Reason { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public sealed class DemoRequestException(string message, int statusCode = 400, string? code = null) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string? Code { get; } = code;
}

// Only fetched businesses and user history are persisted; the million-row source is virtual.
public sealed class DemoDatabase
{
    public int Version { get; set; } = 1;
    public List<DemoUser> Users { get; set; } = [];
    public Dictionary<string, DemoBusiness> Businesses { get; set; } = [];
    public List<DemoJob> Jobs { get; set; } = [];
    public List<DemoUserRecord> Records { get; set; } = [];
    public List<DemoGenerationBatch> GenerationBatches { get; set; } = [];
    public List<DemoSalesProfile> SalesProfiles { get; set; } = [];
    public List<DemoFieldReview> FieldReviews { get; set; } = [];
}
public sealed class DemoUser
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string PasswordSalt { get; set; } = "";
    public string Role { get; set; } = "user";
    public bool Approved { get; set; }
    public bool Blocked { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? LastLoginAtUtc { get; set; }
    public int QuarantineLimit { get; set; } = 1000;
    public int CleanLimit { get; set; } = 1000;
}
public sealed class DemoBusiness
{
    public string Key { get; set; } = "";
    public int? CatalogId { get; set; }
    public string Name { get; set; } = "";
    public string? Address { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Website { get; set; }
    public string Locality { get; set; } = "Delhi NCR";
    public string PlaceType { get; set; } = "hotel";
    public bool IsSample { get; set; }
}
public sealed class DemoUserRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid JobId { get; set; }
    public string BusinessKey { get; set; } = "";
    public string ObservationId { get; set; } = "";
    public string Status { get; set; } = "quarantine";
    public DateTime FoundAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? EnrichedAtUtc { get; set; }
}
public sealed class DemoJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string Name { get; set; } = "";
    public string Status { get; set; } = "fetching";
    public string PlaceType { get; set; } = "any_lodging";
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public int RadiusMeters { get; set; }
    public int TargetCount { get; set; }
    public int FoundCount { get; set; }
    public int DuplicatesRemoved { get; set; }
    public int ProcessedCount { get; set; }
    public long SearchCursor { get; set; }
    public long GeneratedSearchCursor { get; set; }
    public string? PausedStatus { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedAtUtc { get; set; }
    public string? LastError { get; set; }
}
