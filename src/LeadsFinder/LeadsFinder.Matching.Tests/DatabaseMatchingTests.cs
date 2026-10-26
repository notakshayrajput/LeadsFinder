using LeadsFinder.Api;
using LeadsFinder.Data;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;

namespace LeadsFinder.Matching.Tests;

public sealed class DatabaseFactAttribute : FactAttribute
{
    public DatabaseFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LEADSFINDER_TEST_DB")))
            Skip = "Set LEADSFINDER_TEST_DB to run the PostGIS smoke test using temporary tables.";
    }
}

public sealed class DatabaseMatchingTests
{
    [DatabaseFact]
    public async Task SpatialAndNormalizedContactCandidatesAreComparedOnceAndReportsPersist()
    {
        var options = new DbContextOptionsBuilder<LeadsDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("LEADSFINDER_TEST_DB"), x => x.UseNetTopologySuite()).Options;
        await using var db = new LeadsDbContext(options);
        await db.Database.OpenConnectionAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        // Session-local tables shadow the real ones. All writes stay inside these temporary tables.
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TEMP TABLE source_records (LIKE public.source_records INCLUDING ALL) ON COMMIT DROP;
            CREATE TEMP TABLE enriched_records (LIKE public.enriched_records INCLUDING ALL) ON COMMIT DROP;
            CREATE TEMP TABLE duplicate_check_runs (LIKE public.duplicate_check_runs INCLUDING ALL) ON COMMIT DROP;
            """);
        var job = Guid.NewGuid();
        var left = Record(job, "Hotel Amber Grand", 28.64);
        var nearby = Record(job, "Amber Grand Hotel", 28.6401);
        var phone = Record(Guid.NewGuid(), "Hotel Amber Grand", 28.65);
        var website = Record(Guid.NewGuid(), "Hotel Amber Grand", 28.66);
        website.Website = "https://www.amber.example/booking";
        var unrelated = Record(Guid.NewGuid(), "Hotel Tulip", 28.67);
        unrelated.Phone = "+919999912345";
        db.SourceRecords.AddRange(left, nearby, phone, website, unrelated);
        db.EnrichedRecords.AddRange(
            new EnrichedRecord { SourceRecordId = left.Id, Name = left.Name, Phone = "+91 99999 12345", Website = "https://amber.example" },
            new EnrichedRecord { SourceRecordId = phone.Id, Name = phone.Name, Phone = "+919999912345" });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        using var http = new HttpClient();
        var settings = new BedrockMatchingOptions();
        var service = new DuplicateCheckService(db, new BedrockDuplicateJudge(http, settings), settings);
        var result = await service.CheckAsync(job, false, default);
        Assert.Equal(2, result.ScannedCount);
        Assert.Equal(4, result.ComparedPairCount);
        Assert.Equal(3, result.Pairs.Length);
        Assert.Single(result.Pairs, x => x.Decision == "likely_duplicate");
        Assert.Contains(result.Pairs, x => x.SamePhone && x.DistanceMeters > 100);
        Assert.Contains(result.Pairs, x => x.SameWebsite && x.DistanceMeters > 100);
        Assert.Equal(0, result.AiCalls);
        Assert.False(result.Truncated);
        Assert.Equal(5, await db.SourceRecords.CountAsync());
        var saved = await db.DuplicateCheckRuns.SingleAsync();
        Assert.Equal(result.Id, saved.Id);
        Assert.Contains("Amber Grand", saved.ReportJson);
        var restored = System.Text.Json.JsonSerializer.Deserialize<DuplicateReport>(saved.ReportJson, DuplicateCheckService.Json);
        Assert.NotNull(restored);
        Assert.Equal(result.Pairs.Select(x => x.Decision), restored.Pairs.Select(x => x.Decision));
        await transaction.RollbackAsync();
    }

    private static SourceRecord Record(Guid job, string name, double latitude) => new()
    {
        SearchJobId = job, Strategy = "test", SourceId = Guid.NewGuid().ToString(),
        SourceLink = "https://example.org/record", Name = name, Address = "12 Main Road, Delhi 110055",
        Location = new Point(77.21, latitude) { SRID = 4326 }
    };
}
