using LeadsFinder.Api;
using LeadsFinder.Data;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

var connectionString = await LocalDatabase.ResolveAndInitializeAsync(builder.Configuration["LEADSFINDER_DB"]);

builder.Services.AddDbContext<LeadsDbContext>(options =>
    options.UseNpgsql(connectionString, npgsql => npgsql.UseNetTopologySuite()));
builder.Services.AddSingleton<LocationSearchService>();

var app = builder.Build();
var api = app.MapGroup("/api");

api.MapGet("/config", () => Results.Ok(new
{
    placeTypes = JobValidation.PlaceTypes,
    shortagePolicies = JobValidation.ShortagePolicies,
    sourceMode = "geofabrik-osm",
    sources = new[] { "geofabrik-osm", "google-maps-scraper" }
}));

api.MapGet("/locations", async (string? q, LocationSearchService search,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(q) || q.Trim().Length is < 2 or > 120)
        return Results.BadRequest(new { error = "Enter a place name between 2 and 120 characters." });

    try
    {
        return Results.Ok(await search.SearchAsync(q, cancellationToken));
    }
    catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException)
    {
        return Results.Problem("Place search is temporarily unavailable.", statusCode: 503);
    }
});

api.MapPost("/jobs", async (CreateJobRequest request, LeadsDbContext db, CancellationToken cancellationToken) =>
{
    var errors = JobValidation.Validate(request, DateTimeOffset.UtcNow);
    if (errors.Count > 0)
        return Results.ValidationProblem(errors);

    var job = new SearchJobEntity
    {
        CreatedAtUtc = DateTime.UtcNow,
        ScheduledAtUtc = request.ScheduledAtUtc.UtcDateTime,
        Latitude = request.Latitude,
        Longitude = request.Longitude,
        RadiusMeters = request.RadiusMeters,
        PlaceType = request.PlaceType!,
        Source = request.Source ?? "geofabrik-osm",
        RequirePhone = request.RequirePhone,
        RequireEmail = request.RequireEmail,
        RequireAddress = request.RequireAddress,
        TargetCount = request.TargetCount,
        ShortagePolicy = request.ShortagePolicy!
    };

    db.SearchJobs.Add(job);
    await db.SaveChangesAsync(cancellationToken);
    return Results.Created($"/api/jobs/{job.Id}", JobResponse.From(job, 0));
});

api.MapGet("/jobs", async (LeadsDbContext db, int? page, int? pageSize, CancellationToken cancellationToken) =>
{
    var currentPage = Math.Clamp(page ?? 1, 1, 10_000);
    var size = Math.Clamp(pageSize ?? 20, 1, 100);
    var total = await db.SearchJobs.CountAsync(cancellationToken);
    var jobs = await db.SearchJobs.AsNoTracking()
        .OrderByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.Id)
        .Skip((currentPage - 1) * size).Take(size)
        .ToListAsync(cancellationToken);
    var jobIds = jobs.Select(x => x.Id).ToArray();
    var counts = await db.SourceRecords.AsNoTracking()
        .Where(x => jobIds.Contains(x.SearchJobId))
        .GroupBy(x => x.SearchJobId)
        .Select(x => new { JobId = x.Key, Count = x.Count() })
        .ToDictionaryAsync(x => x.JobId, x => x.Count, cancellationToken);
    var items = jobs.Select(x => JobResponse.From(x, counts.GetValueOrDefault(x.Id))).ToArray();
    return Results.Ok(new PageResponse<JobResponse>(items, total, currentPage, size));
});

api.MapGet("/jobs/{id:guid}", async (Guid id, LeadsDbContext db, CancellationToken cancellationToken) =>
{
    var job = await db.SearchJobs.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
    if (job is null)
        return Results.NotFound();

    var count = await db.SourceRecords.CountAsync(x => x.SearchJobId == id, cancellationToken);
    return Results.Ok(JobResponse.From(job, count));
});

api.MapGet("/jobs/{id:guid}/records", async (Guid id, LeadsDbContext db, int? page, int? pageSize, CancellationToken cancellationToken) =>
{
    if (!await db.SearchJobs.AnyAsync(x => x.Id == id, cancellationToken))
        return Results.NotFound();

    var currentPage = Math.Clamp(page ?? 1, 1, 10_000);
    var size = Math.Clamp(pageSize ?? 20, 1, 100);
    var query = db.SourceRecords.AsNoTracking().Where(x => x.SearchJobId == id);
    var total = await query.CountAsync(cancellationToken);
    var records = await query.OrderBy(x => x.FetchedAtUtc).ThenBy(x => x.Id)
        .Skip((currentPage - 1) * size).Take(size)
        .ToListAsync(cancellationToken);
    var items = records.Select(x => new SourceRecordResponse(
        x.Id, x.SearchJobId, x.Strategy, x.SourceId, x.SourceLink,
        x.Name, x.Address, x.Location.Y, x.Location.X,
        x.Phone, x.Email, x.Website, x.FetchedAtUtc, x.RawJson)).ToArray();
    return Results.Ok(new PageResponse<SourceRecordResponse>(items, total, currentPage, size));
});

api.MapGet("/enriched-records", async (LeadsDbContext db, int? page, int? pageSize, CancellationToken cancellationToken) =>
{
    var currentPage = Math.Clamp(page ?? 1, 1, 10_000);
    var size = Math.Clamp(pageSize ?? 20, 1, 100);
    var total = await db.EnrichedRecords.CountAsync(cancellationToken);
    var records = await db.EnrichedRecords.AsNoTracking()
        .OrderByDescending(x => x.UpdatedAtUtc).ThenBy(x => x.Id)
        .Skip((currentPage - 1) * size).Take(size)
        .Select(x => new { x.Id, x.SourceRecordId, x.Name, x.Address, x.Phone, x.Email,
            x.Website, x.Status, x.SearchCount, x.PromptTokens, x.ResponseTokens, x.UpdatedAtUtc })
        .ToListAsync(cancellationToken);
    return Results.Ok(new PageResponse<object>(records.Cast<object>().ToArray(), total, currentPage, size));
});

api.MapGet("/enriched-records/{id:guid}", async (Guid id, LeadsDbContext db, CancellationToken cancellationToken) =>
{
    var record = await db.EnrichedRecords.AsNoTracking().Include(x => x.Evidence)
        .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
    return record is null ? Results.NotFound() : Results.Ok(new
    {
        record.Id, record.SourceRecordId, record.Name, record.Address, record.Phone, record.Email,
        record.Website, record.Status, record.SearchCount, record.PromptTokens,
        record.ResponseTokens, record.LastError, record.UpdatedAtUtc,
        Evidence = record.Evidence.Select(x => new { x.Field, x.Value, x.SourceUrl,
            x.Excerpt, x.Strategy, x.VerificationStatus, x.CheckedAtUtc })
    });
});

app.Run();
