namespace LeadsFinder.Enrichment;

public sealed record EnrichmentInput(Guid SourceRecordId, string Name, string Address,
    string SourceUrl, string? Phone, string? Email, string? Website,
    bool RequirePhone = true, bool RequireEmail = true, bool RequireAddress = true,
    string? SearchContext = null);

public sealed record EvidencePage(string Url, string Title, string Text, string Strategy);
public sealed record FieldEvidence(string Field, string Value, string Url, string Excerpt,
    string Strategy, string VerificationStatus = "unverified");

public sealed class EnrichmentResult
{
    public required EnrichmentInput Input { get; init; }
    public Dictionary<string, FieldEvidence> Fields { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int SearchCount { get; set; }
    public int PromptTokens { get; set; }
    public int ResponseTokens { get; set; }
    public string? Error { get; set; }
    public bool Complete => (!Input.RequireAddress || Fields.ContainsKey("address"))
        && (!Input.RequirePhone || Fields.ContainsKey("phone"))
        && (!Input.RequireEmail || Fields.ContainsKey("email"));
}

public interface IEnrichmentStrategy
{
    string Name { get; }
    Task<IReadOnlyList<EvidencePage>> FindAsync(EnrichmentInput input, EnrichmentResult current,
        CancellationToken cancellationToken);
}
