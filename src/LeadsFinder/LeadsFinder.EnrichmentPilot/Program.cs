using LeadsFinder.Data;
using LeadsFinder.Enrichment;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using System.Text.Json;

var config = Host.CreateApplicationBuilder(args).Configuration;
config.AddUserSecrets(System.Reflection.Assembly.GetExecutingAssembly(), optional: true);
config.AddInMemoryCollection(LocalEnvironment.Read()).AddEnvironmentVariables().AddCommandLine(args);
var key = config["Ollama:ApiKey"] ?? config["OLLAMA_API_KEY"]
    ?? throw new InvalidOperationException("Set Ollama__ApiKey in the repository-root .env file or configure Ollama:ApiKey with user-secrets.");
var model = config["Ollama:Model"] ?? "qwen3:8b";
var ollamaUrl = config["Ollama:BaseUrl"] ?? "http://127.0.0.1:11434";
var limit = 20;
var limitIndex = Array.IndexOf(args, "--limit");
if (limitIndex >= 0 && limitIndex + 1 < args.Length && int.TryParse(args[limitIndex + 1], out var parsed))
    limit = Math.Clamp(parsed, 1, 100);
var offset = 0;
var offsetIndex = Array.IndexOf(args, "--offset");
if (offsetIndex >= 0 && offsetIndex + 1 < args.Length && int.TryParse(args[offsetIndex + 1], out var parsedOffset))
    offset = Math.Max(0, parsedOffset);
var contextIndex = Array.IndexOf(args, "--context");
var locationContext = contextIndex >= 0 && contextIndex + 1 < args.Length
    ? args[contextIndex + 1].Trim() : null;
if (args.Contains("--smoke"))
{
    using var smokeHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
    var smokeInput = new EnrichmentInput(Guid.NewGuid(), "Hotel Amber", "New Delhi",
        "https://example.org/amber", null, null, null);
    var smokeResult = new EnrichmentResult { Input = smokeInput };
    var smokeFields = await new QwenExtractor(smokeHttp, ollamaUrl, model).ExtractAsync(smokeInput,
        [new EvidencePage("https://example.org/amber", "Hotel Amber", "Hotel Amber in New Delhi. Contact phone +91 98100 12345. Email stays@amber.example.org. Address 12 Janpath, New Delhi.", "smoke")],
        smokeResult, CancellationToken.None);
    Console.WriteLine($"Model smoke: fields={smokeFields.Count}, promptTokens={smokeResult.PromptTokens}, responseTokens={smokeResult.ResponseTokens}.");
    return;
}
if (args.Contains("--sample"))
{
    var samplePath = Path.Combine("samples", "hotels-20.json");
    var sample = JsonSerializer.Deserialize<SampleHotel[]>(await File.ReadAllTextAsync(samplePath),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
    sample = sample.Skip(offset).Take(limit).ToArray();
    using var samplePageHttp = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };
    using var sampleSearchHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    using var sampleModelHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
    var sampleReader = new HtmlPageReader(samplePageHttp);
    var sampleService = new EnrichmentService(
        [new KnownWebsiteStrategy(sampleReader), new OllamaWebSearchStrategy(sampleSearchHttp, sampleReader, key)],
        new QwenExtractor(sampleModelHttp, ollamaUrl, model));
    var report = new List<object>();
    Directory.CreateDirectory("artifacts");
    var reportPath = Path.Combine("artifacts", offset == 0 && sample.Length == 20
        ? "enrichment-sample-20.json" : $"enrichment-sample-{sample.Length}-offset-{offset}.json");
    foreach (var hotel in sample)
    {
        var input = new EnrichmentInput(Guid.NewGuid(), hotel.Name, "",
            hotel.Website, null, null, hotel.Website, SearchContext: hotel.Address);
        try
        {
            var result = await sampleService.EnrichAsync(input, CancellationToken.None);
            report.Add(new { hotel.Name, hotel.Address, hotel.Website, result.Complete,
                result.SearchCount, result.PromptTokens, result.ResponseTokens, result.Error,
                Fields = result.Fields.Values });
            Console.WriteLine($"{report.Count}/{sample.Length}: {hotel.Name}: fields={result.Fields.Count}, searches={result.SearchCount}");
        }
        catch (Exception ex)
        {
            report.Add(new { hotel.Name, Error = ex.GetType().Name });
            Console.WriteLine($"{report.Count}/{sample.Length}: {hotel.Name}: {ex.GetType().Name}");
        }
        await File.WriteAllTextAsync(reportPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }
    Console.WriteLine($"Sample report: {Path.GetFullPath(reportPath)}");
    return;
}
var connection = await LocalDatabase.ResolveAndInitializeAsync(config["LEADSFINDER_DB"]);
var retryPartial = args.Contains("--retry-partial");

var options = new DbContextOptionsBuilder<LeadsDbContext>()
    .UseNpgsql(connection, x => x.UseNetTopologySuite()).Options;
await using var db = new LeadsDbContext(options);
if (!await db.Database.CanConnectAsync()) throw new InvalidOperationException("Database unavailable.");
if ((await db.Database.GetPendingMigrationsAsync()).Any())
    throw new InvalidOperationException("Apply pending EF migrations before the pilot.");

using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
using var modelResponse = await probe.GetAsync(ollamaUrl.TrimEnd('/') + "/api/tags");
modelResponse.EnsureSuccessStatusCode();
using var tags = JsonDocument.Parse(await modelResponse.Content.ReadAsStringAsync());
if (!tags.RootElement.GetProperty("models").EnumerateArray()
    .Any(x => x.GetProperty("name").GetString() == model))
    throw new InvalidOperationException($"Ollama model {model} is not available locally. Pull it before the pilot.");

var rows = await db.SourceRecords.AsNoTracking()
    .Where(x => !db.EnrichedRecords.Any(e => e.SourceRecordId == x.Id && (!retryPartial || e.Status == "complete")))
    .OrderByDescending(x => x.Website != null)
    .ThenBy(x => x.FetchedAtUtc).ThenBy(x => x.Id)
    .Take(limit).ToListAsync();
var jobIds = rows.Select(x => x.SearchJobId).Distinct().ToArray();
var jobs = await db.SearchJobs.AsNoTracking().Where(x => jobIds.Contains(x.Id))
    .ToDictionaryAsync(x => x.Id);
Console.WriteLine($"Enriching {rows.Count} source records with {model}.");

using var pageHttp = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };
using var searchHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
using var modelHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
var reader = new HtmlPageReader(pageHttp);
var service = new EnrichmentService(
    [new KnownWebsiteStrategy(reader), new OllamaWebSearchStrategy(searchHttp, reader, key)],
    new QwenExtractor(modelHttp, ollamaUrl, model));

var complete = 0;
var partial = 0;
var searches = 0;
var promptTokens = 0;
var responseTokens = 0;
foreach (var source in rows)
{
    var existing = await db.EnrichedRecords.Include(x => x.Evidence)
        .FirstOrDefaultAsync(x => x.SourceRecordId == source.Id);
    jobs.TryGetValue(source.SearchJobId, out var job);
    var input = new EnrichmentInput(source.Id, source.Name, source.Address, source.SourceLink,
        source.Phone, source.Email, source.Website,
        job?.RequirePhone ?? true, job?.RequireEmail ?? true, job?.RequireAddress ?? true,
        SearchContext: locationContext);
    EnrichmentResult result;
    var priorEvidence = existing?.Evidence.Select(x => new FieldEvidence(x.Field, x.Value,
        x.SourceUrl, x.Excerpt, x.Strategy, x.VerificationStatus));
    try { result = await service.EnrichAsync(input, CancellationToken.None, priorEvidence); }
    catch (Exception ex) { Console.WriteLine($"{source.Id}: failed ({ex.GetType().Name})"); continue; }
    var enriched = existing ?? new EnrichedRecord { SourceRecordId = source.Id };
    if (existing is not null)
    {
        db.EnrichmentEvidence.RemoveRange(existing.Evidence);
        existing.Evidence.Clear();
    }
    enriched.Name = source.Name;
    enriched.Address = result.Fields.GetValueOrDefault("address")?.Value;
    enriched.Phone = result.Fields.GetValueOrDefault("phone")?.Value;
    enriched.Email = result.Fields.GetValueOrDefault("email")?.Value;
    enriched.Website = result.Fields.GetValueOrDefault("website")?.Value;
    enriched.Status = result.Complete ? "complete" : "partial";
    enriched.SearchCount += result.SearchCount;
    enriched.PromptTokens += result.PromptTokens;
    enriched.ResponseTokens += result.ResponseTokens;
    enriched.LastError = result.Error;
    enriched.UpdatedAtUtc = DateTime.UtcNow;
    enriched.Evidence = result.Fields.Values.Select(e => new EnrichmentEvidence
        {
            Field = e.Field, Value = e.Value, SourceUrl = e.Url, Excerpt = e.Excerpt,
            Strategy = e.Strategy, VerificationStatus = e.VerificationStatus
        }).ToList();
    if (existing is null) db.EnrichedRecords.Add(enriched);
    await db.SaveChangesAsync();
    db.ChangeTracker.Clear();
    if (result.Complete) complete++; else partial++;
    searches += result.SearchCount;
    promptTokens += result.PromptTokens;
    responseTokens += result.ResponseTokens;
    Console.WriteLine($"{source.Id}: {enriched.Status}, fields={result.Fields.Count}, searches={result.SearchCount}");
}
Console.WriteLine($"Finished: complete={complete}, partial={partial}, searches={searches}, promptTokens={promptTokens}, responseTokens={responseTokens}.");

internal sealed record SampleHotel(string Name, string Address, string Website);
