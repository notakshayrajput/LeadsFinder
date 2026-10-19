namespace LeadsFinder.Data;

// One enrichment result per quarantined observation. Business deduplication is a separate step.
public sealed class EnrichedRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SourceRecordId { get; set; }
    public SourceRecord SourceRecord { get; set; } = null!;
    public string Name { get; set; } = "";
    public string? Address { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Website { get; set; }
    public string Status { get; set; } = "pending";
    public int SearchCount { get; set; }
    public int PromptTokens { get; set; }
    public int ResponseTokens { get; set; }
    public string? LastError { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public List<EnrichmentEvidence> Evidence { get; set; } = [];
}

public sealed class EnrichmentEvidence
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EnrichedRecordId { get; set; }
    public EnrichedRecord EnrichedRecord { get; set; } = null!;
    public string Field { get; set; } = "";
    public string Value { get; set; } = "";
    public string SourceUrl { get; set; } = "";
    public string Excerpt { get; set; } = "";
    public string Strategy { get; set; } = "";
    public string VerificationStatus { get; set; } = "unverified";
    public DateTime CheckedAtUtc { get; set; } = DateTime.UtcNow;
}
