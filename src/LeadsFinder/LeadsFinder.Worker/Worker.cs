using LeadsFinder.Data;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using System.Text.Json;

namespace LeadsFinder.Worker;

public class Worker(
    ILogger<Worker> logger,
    IEnumerable<ISearchStrategy> strategies,
    IDbContextFactory<LeadsDbContext> dbFactory) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        const int maxAttempts = 3;

        while (!stoppingToken.IsCancellationRequested)
        {
            await using var db = await dbFactory.CreateDbContextAsync(stoppingToken);
            var nowUtc = DateTime.UtcNow;

            await db.SearchJobs
                .Where(x => x.Status == "running"
                    && (x.LeaseUntilUtc == null || x.LeaseUntilUtc <= nowUtc)
                    && x.AttemptCount >= maxAttempts)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.Status, "failed")
                    .SetProperty(x => x.LastError, "Lease expired after 3 attempts")
                    .SetProperty(x => x.LeaseUntilUtc, (DateTime?)null)
                    .SetProperty(x => x.FinishedAtUtc, (DateTime?)nowUtc),
                    stoppingToken);

            SearchJobEntity? dueJob;

            await using (var claimTransaction =
                await db.Database.BeginTransactionAsync(stoppingToken))
            {
                var lockedJobs = await db.SearchJobs
                    .FromSqlInterpolated($"""
                        SELECT * FROM search_jobs
                        WHERE "ScheduledAtUtc" <= {nowUtc}
                          AND "AttemptCount" < {maxAttempts}
                          AND (
                              "Status" = 'pending'
                              OR (
                                  "Status" = 'running'
                                  AND ("LeaseUntilUtc" IS NULL OR "LeaseUntilUtc" <= {nowUtc})
                              )
                          )
                        ORDER BY "ScheduledAtUtc", "Id"
                        LIMIT 1
                        FOR UPDATE SKIP LOCKED
                        """)
                    .ToListAsync(stoppingToken);

                dueJob = lockedJobs.FirstOrDefault();

                if (dueJob is not null)
                {
                    dueJob.Status = "running";
                    dueJob.AttemptCount++;
                    dueJob.StartedAtUtc = nowUtc;
                    dueJob.LeaseUntilUtc = nowUtc.Add(SearchBudget.For(dueJob.Source, dueJob.RadiusMeters, dueJob.ShortagePolicy) + TimeSpan.FromMinutes(5));
                    dueJob.LastError = null;
                    await db.SaveChangesAsync(stoppingToken);
                }

                await claimTransaction.CommitAsync(stoppingToken);
            }

            if (dueJob is null)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                continue;
            }

            using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            jobCts.CancelAfter(SearchBudget.For(dueJob.Source, dueJob.RadiusMeters, dueJob.ShortagePolicy));
            var jobToken = jobCts.Token;

            try
            {
                var job = new SearchJob(
                    dueJob.Id,
                    new DateTimeOffset(dueJob.ScheduledAtUtc),
                    dueJob.Source,
                    dueJob.Latitude,
                    dueJob.Longitude,
                    dueJob.RadiusMeters,
                    dueJob.PlaceType,
                    dueJob.RequirePhone,
                    dueJob.RequireEmail,
                    dueJob.RequireAddress,
                    dueJob.TargetCount,
                    dueJob.ShortagePolicy);

                logger.LogInformation("Starting job {JobId}", dueJob.Id);

                await using var finishTransaction =
                    await db.Database.BeginTransactionAsync(jobToken);

                // Keep selection and insertion together across worker instances.
                await db.Database.ExecuteSqlRawAsync(
                    "SELECT pg_advisory_xact_lock(3944852341324)", jobToken);

                var strategy = strategies.SingleOrDefault(x => x.Name == job.Source)
                    ?? throw new InvalidOperationException($"Search source {job.Source} is not configured.");
                {
                    var existingSourceIds = await db.SourceRecords
                        .AsNoTracking()
                        .Where(record => record.Strategy == strategy.Name)
                        .Select(record => record.SourceId)
                        .ToListAsync(jobToken);
                    var seenSourceIds = new HashSet<string>(existingSourceIds, StringComparer.Ordinal);
                    var places = await strategy.SearchAsync(job, seenSourceIds, jobToken);

                    logger.LogInformation(
                        "{Strategy} found {Count} places", strategy.Name, places.Count);

                    foreach (var place in places)
                    {
                        if (string.IsNullOrWhiteSpace(place.SourceId)
                            || string.IsNullOrWhiteSpace(place.Name)
                            || !Uri.TryCreate(place.SourceLink, UriKind.Absolute, out var sourceUri)
                            || (sourceUri.Scheme != Uri.UriSchemeHttp && sourceUri.Scheme != Uri.UriSchemeHttps)
                            || !double.IsFinite(place.Latitude) || place.Latitude is < -90 or > 90
                            || !double.IsFinite(place.Longitude) || place.Longitude is < -180 or > 180)
                        {
                            logger.LogWarning("Skipping invalid observation from {Strategy} for job {JobId}", strategy.Name, job.Id);
                            continue;
                        }

                        logger.LogDebug(
                            "Found {Name} at {Address} via {Strategy}: {Link}",
                            place.Name, place.Address, place.Strategy, place.SourceLink);

                        if (!seenSourceIds.Add(place.SourceId))
                        {
                            logger.LogInformation(
                                "Skipping existing source {Strategy}/{SourceId} for job {JobId}",
                                strategy.Name, place.SourceId, job.Id);
                            continue;
                        }
                        db.SourceRecords.Add(new SourceRecord
                        {
                            SearchJobId = job.Id,
                            Strategy = strategy.Name,
                            SourceId = place.SourceId,
                            SourceLink = place.SourceLink,
                            Name = place.Name.Trim()[..Math.Min(place.Name.Trim().Length, 300)],
                            Address = place.Address.Trim()[..Math.Min(place.Address.Trim().Length, 1000)],
                            Location = new Point(place.Longitude, place.Latitude) { SRID = 4326 },
                            Phone = place.Phone,
                            Email = place.Email,
                            Website = place.Website,
                            RawJson = place.RawJson ?? JsonSerializer.Serialize(place),
                            FetchedAtUtc = DateTime.UtcNow
                        });
                    }
                }

                var finishedAtUtc = DateTime.UtcNow;

                var updatedRows = await db.SearchJobs
                    .Where(x => x.Id == dueJob.Id
                        && x.Status == "running"
                        && x.AttemptCount == dueJob.AttemptCount
                        && x.LeaseUntilUtc > finishedAtUtc)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(x => x.Status, "completed")
                        .SetProperty(x => x.FinishedAtUtc, (DateTime?)finishedAtUtc)
                        .SetProperty(x => x.LeaseUntilUtc, (DateTime?)null),
                        jobToken);

                if (updatedRows != 1)
                {
                    await finishTransaction.RollbackAsync(jobToken);
                    logger.LogWarning(
                        "Job {JobId} lost its lease on attempt {AttemptCount}",
                        dueJob.Id, dueJob.AttemptCount);
                    continue;
                }

                await db.SaveChangesAsync(jobToken);
                await finishTransaction.CommitAsync(jobToken);
                logger.LogInformation(
                    "Saved source observations for job {JobId} to quarantine", dueJob.Id);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Job {JobId} failed", dueJob.Id);

                await using var failureDb =
                    await dbFactory.CreateDbContextAsync(stoppingToken);

                var failedAtUtc = DateTime.UtcNow;
                var errorType = ex switch
                {
                    GeofabrikSourceException or GoogleMapsScraperSourceException => ex.Message,
                    OperationCanceledException => "Search exceeded its area-based time limit.",
                    _ => ex.GetType().Name
                };

                await failureDb.SearchJobs
                    .Where(x => x.Id == dueJob.Id
                        && x.Status == "running"
                        && x.AttemptCount == dueJob.AttemptCount
                        && x.LeaseUntilUtc > failedAtUtc)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(x => x.Status, "failed")
                        .SetProperty(x => x.LastError, errorType)
                        .SetProperty(x => x.LeaseUntilUtc, (DateTime?)null)
                        .SetProperty(x => x.FinishedAtUtc, (DateTime?)failedAtUtc),
                        stoppingToken);
            }
        }
    }
}
