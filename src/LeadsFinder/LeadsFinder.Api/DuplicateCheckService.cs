using System.Text.Json;
using LeadsFinder.Data;
using LeadsFinder.Matching;
using Microsoft.EntityFrameworkCore;

namespace LeadsFinder.Api;

public sealed record DuplicateCheckRequest(bool UseAi = false);
public sealed record DuplicateReport(Guid Id, Guid SearchJobId, DateTime CreatedAtUtc, string RuleVersion,
    int ScannedCount, int ComparedPairCount, bool Truncated, int AiCalls, int AiSkipped, PairAssessment[] Pairs);

public sealed class DuplicateCheckService(LeadsDbContext db, BedrockDuplicateJudge judge, BedrockMatchingOptions options)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<DuplicateReport> CheckAsync(Guid jobId, bool useAi, CancellationToken token)
    {
        var sources = await db.SourceRecords.AsNoTracking().Where(x => x.SearchJobId == jobId)
            .OrderBy(x => x.Id).Take(501).ToListAsync(token);
        var truncated = sources.Count > 500;
        sources = sources.Take(500).ToList();
        var sourceIds = sources.Select(x => x.Id).ToArray();
        var enriched = await db.EnrichedRecords.AsNoTracking().Where(x => sourceIds.Contains(x.SourceRecordId))
            .ToDictionaryAsync(x => x.SourceRecordId, token);
        var compared = new HashSet<(Guid, Guid)>();
        var pairs = new List<PairAssessment>();
        var scanned = 0;
        foreach (var source in sources)
        {
            if (compared.Count >= 2000) { truncated = true; break; }
            scanned++;
            var left = Snapshot(source, enriched.GetValueOrDefault(source.Id));
            var phone = PlaceMatcher.PhoneKey(left.Phone);
            if (phone.Length < 7) phone = "";
            var host = PlaceMatcher.WebsiteKey(left.Website);
            // Geography distance is in metres. Exact contact candidates also cover imprecise coordinates.
            // Contacts only select candidates: the matcher never treats a shared contact as proof.
            var candidates = await db.SourceRecords.FromSqlInterpolated($"""
                SELECT s.* FROM source_records AS s
                LEFT JOIN enriched_records AS e ON e."SourceRecordId" = s."Id"
                WHERE s."Id" <> {source.Id} AND (
                    ST_DWithin(s."Location", {source.Location}::geography, 100)
                    OR ({phone} <> '' AND regexp_replace(coalesce(nullif(e."Phone", ''), s."Phone", ''), '[^0-9]', '', 'g') = {phone})
                    OR ({host} <> '' AND lower(regexp_replace(
                        split_part(split_part(regexp_replace(coalesce(nullif(e."Website", ''), s."Website", ''),
                        '^https?://', '', 'i'), '/', 1), ':', 1), '^www\.', '', 'i')) = {host})
                )
                ORDER BY ST_Distance(s."Location", {source.Location}::geography), s."Id"
                LIMIT 51
                """).AsNoTracking().ToListAsync(token);
            if (candidates.Count > 50) truncated = true;
            var candidateIds = candidates.Select(x => x.Id).ToArray();
            var candidateEnriched = await db.EnrichedRecords.AsNoTracking()
                .Where(x => candidateIds.Contains(x.SourceRecordId)).ToDictionaryAsync(x => x.SourceRecordId, token);
            foreach (var candidate in candidates.Take(50))
            {
                var key = source.Id.CompareTo(candidate.Id) < 0 ? (source.Id, candidate.Id) : (candidate.Id, source.Id);
                if (compared.Contains(key)) continue;
                if (compared.Count >= 2000) { truncated = true; break; }
                compared.Add(key);
                var pair = PlaceMatcher.Compare(left, Snapshot(candidate, candidateEnriched.GetValueOrDefault(candidate.Id)));
                if (pair.RuleDecision != "likely_distinct") pairs.Add(pair);
            }
        }

        var aiCalls = 0;
        if (useAi)
        {
            var maxCalls = Math.Clamp(options.MaxCallsPerCheck, 1, 10);
            // Prioritize ambiguous pairs, and stop on service errors rather than repeating billable failures.
            foreach (var pair in pairs.OrderBy(x => x.RuleDecision == "possible_duplicate" ? 0 : 1)
                .ThenByDescending(x => x.NameSimilarity).Take(maxCalls).ToArray())
            {
                var ai = await judge.AssessAsync(pair, token);
                aiCalls++;
                pairs[pairs.IndexOf(pair)] = pair with { Ai = ai };
                if (ai.Error is not null) break;
            }
        }

        var report = new DuplicateReport(Guid.NewGuid(), jobId, DateTime.UtcNow, PlaceMatcher.RuleVersion,
            scanned, compared.Count, truncated, aiCalls, useAi ? pairs.Count - aiCalls : 0,
            pairs.OrderBy(x => x.Decision == "likely_duplicate" ? 0 : x.Decision == "possible_duplicate" ? 1 : 2)
                .ThenByDescending(x => x.NameSimilarity).ToArray());
        db.DuplicateCheckRuns.Add(new DuplicateCheckRun
        {
            Id = report.Id, SearchJobId = jobId, CreatedAtUtc = report.CreatedAtUtc,
            RuleVersion = report.RuleVersion, ReportJson = JsonSerializer.Serialize(report, Json)
        });
        await db.SaveChangesAsync(token);
        return report;
    }

    private static PlaceSnapshot Snapshot(SourceRecord source, EnrichedRecord? enriched) => new(source.Id,
        source.Name, Value(enriched?.Address, source.Address) ?? "", source.Location.Y, source.Location.X,
        Value(enriched?.Phone, source.Phone), Value(enriched?.Email, source.Email),
        Value(enriched?.Website, source.Website), source.Strategy, source.SourceId, source.SourceLink);

    private static string? Value(string? enriched, string? raw) => string.IsNullOrWhiteSpace(enriched) ? raw : enriched;
}
