using System.Text.Json;

namespace LeadsFinder.Enrichment;

public sealed class EnrichmentService(IEnumerable<IEnrichmentStrategy> strategies, QwenExtractor extractor)
{
    public async Task<EnrichmentResult> EnrichAsync(EnrichmentInput input, CancellationToken cancellationToken,
        IEnumerable<FieldEvidence>? priorEvidence = null)
    {
        var result = new EnrichmentResult { Input = input };
        if (priorEvidence is not null)
            foreach (var evidence in priorEvidence)
                result.Fields[evidence.Field] = evidence;
        AddExisting("address", input.Address);
        AddExisting("phone", input.Phone);
        AddExisting("email", input.Email);
        AddExisting("website", input.Website);
        if (result.Complete) return result;

        foreach (var strategy in strategies)
        {
            if (result.Complete) break;
            try
            {
                var pages = await strategy.FindAsync(input, result, cancellationToken);
                if (pages.Count == 0) continue;
                var extracted = await extractor.ExtractAsync(input, pages, result, cancellationToken);
                foreach (var field in extracted)
                    result.Fields.TryAdd(field.Field, field);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                result.Error = ex is HttpRequestException { StatusCode: { } status }
                    ? $"{strategy.Name}: HTTP {(int)status}"
                    : $"{strategy.Name}: {ex.GetType().Name}";
            }
        }
        return result;

        void AddExisting(string field, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                result.Fields[field] = new FieldEvidence(field, value.Trim(), input.SourceUrl,
                    value.Trim()[..Math.Min(value.Trim().Length, 500)], "source-record");
        }
    }
}
