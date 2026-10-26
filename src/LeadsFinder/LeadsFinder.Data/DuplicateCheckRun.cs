namespace LeadsFinder.Data;

// Immutable report per manually requested check. Source observations remain untouched.
public sealed class DuplicateCheckRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SearchJobId { get; set; }
    public SearchJobEntity SearchJob { get; set; } = null!;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string RuleVersion { get; set; } = "";
    public string ReportJson { get; set; } = "{}";
}
