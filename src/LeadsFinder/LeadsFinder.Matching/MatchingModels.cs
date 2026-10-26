namespace LeadsFinder.Matching;

public sealed record PlaceSnapshot(Guid Id, string Name, string Address, double Latitude,
    double Longitude, string? Phone, string? Email, string? Website, string Strategy,
    string SourceId, string SourceUrl);

public sealed record PairAssessment(PlaceSnapshot Left, PlaceSnapshot Right, double DistanceMeters,
    double NameSimilarity, double? AddressSimilarity, bool SamePhone, bool SameWebsite,
    bool AddressConflict, string RuleDecision, string[] Reasons, AiAssessment? Ai = null)
{
    public string Decision => Ai?.Decision switch
    {
        "same" => "likely_duplicate",
        "different" => "likely_distinct",
        "uncertain" when Ai.Error is null => "possible_duplicate",
        _ => RuleDecision
    };
}

public sealed record AiEvidence(string Field, string LeftValue, string RightValue);
public sealed record AiAssessment(string Decision, string Reason, AiEvidence[] Evidence,
    string? ModelId = null, int InputTokens = 0, int OutputTokens = 0, string? Error = null);

public sealed class BedrockMatchingOptions
{
    public bool Enabled { get; set; }
    public string Region { get; set; } = "";
    public string ModelId { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public int MaxCallsPerCheck { get; set; } = 5;
    public int TimeoutSeconds { get; set; } = 30;
    public bool Ready => Enabled && !string.IsNullOrWhiteSpace(Region)
        && !string.IsNullOrWhiteSpace(ModelId) && !string.IsNullOrWhiteSpace(ApiKey);
}
