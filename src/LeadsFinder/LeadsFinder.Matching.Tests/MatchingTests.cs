using System.Net;
using System.Text.Json;

namespace LeadsFinder.Matching.Tests;

public sealed class MatchingTests
{
    private static PlaceSnapshot Hotel(string name = "Hotel Krishna Deluxe", string address = "12 Main Road, Delhi 110055",
        double latitude = 28.64, string? phone = null, string? website = null) =>
        new(Guid.NewGuid(), name, address, latitude, 77.21, phone, null, website, "test", Guid.NewGuid().ToString(), "https://example.org/source");

    [Fact]
    public void ReorderedDistinctiveNameWithin40MetresIsLikelyDuplicate()
    {
        var pair = PlaceMatcher.Compare(Hotel(), Hotel("Krishna Deluxe Hotel", latitude: 28.6401));
        Assert.Equal(1, pair.NameSimilarity);
        Assert.InRange(pair.DistanceMeters, 10, 12);
        Assert.Equal("likely_duplicate", pair.Decision);
    }

    [Fact]
    public void TypoInNameNearSameAddressNeedsReview()
    {
        var pair = PlaceMatcher.Compare(Hotel(), Hotel("Hotel Krishna Delux", latitude: 28.6401));
        Assert.Equal("possible_duplicate", pair.Decision);
    }

    [Theory]
    [InlineData("14 Main Road, Delhi 110055")]
    [InlineData("12 Main Road, Delhi 110056")]
    public void ConflictingAddressPreventsStrongRuleMatch(string address)
    {
        var pair = PlaceMatcher.Compare(Hotel(), Hotel(address: address, latitude: 28.6401));
        Assert.True(pair.AddressConflict);
        Assert.Equal("possible_duplicate", pair.Decision);
    }

    [Theory]
    [InlineData("Hotel")]
    [InlineData("The Guest House")]
    [InlineData("Unnamed lodging (OSM 123)")]
    public void GenericAndPlaceholderNamesDoNotMatchOnLocationAlone(string name)
    {
        var pair = PlaceMatcher.Compare(Hotel(name), Hotel(name, latitude: 28.6401));
        Assert.Equal(0, pair.NameSimilarity);
        Assert.Equal("likely_distinct", pair.Decision);
    }

    [Fact]
    public void SharedBookingContactsDoNotMergeUnrelatedBusinesses()
    {
        var pair = PlaceMatcher.Compare(Hotel("Hotel Amber", phone: "+91 99999 12345", website: "https://www.booking.example/a"),
            Hotel("Hotel Tulip", latitude: 28.65, phone: "+919999912345", website: "https://booking.example/b"));
        Assert.True(pair.SamePhone);
        Assert.True(pair.SameWebsite);
        Assert.Equal("likely_distinct", pair.Decision);
    }

    [Fact]
    public void SimilarNamesOutside40MetresAreNotStrongMatches()
    {
        var pair = PlaceMatcher.Compare(Hotel(), Hotel(latitude: 28.6405));
        Assert.InRange(pair.DistanceMeters, 50, 60);
        Assert.Equal("possible_duplicate", pair.Decision);
    }

    [Fact]
    public void MissingAddressRemainsMissingInScore()
    {
        var pair = PlaceMatcher.Compare(Hotel(address: ""), Hotel());
        Assert.Null(pair.AddressSimilarity);
        Assert.Contains(pair.Reasons, x => x.Contains("missing"));
    }

    [Fact]
    public void UnicodeAccentsDoNotPreventNameComparison()
    {
        var pair = PlaceMatcher.Compare(Hotel("Hotel Café Amber"), Hotel("Cafe Amber Hotel"));
        Assert.Equal(1, pair.NameSimilarity);
    }

    [Fact]
    public void IndicVowelMarksRemainDistinct()
    {
        Assert.NotEqual(PlaceMatcher.Normalize("माला"), PlaceMatcher.Normalize("मिला"));
        Assert.NotEqual(1, PlaceMatcher.Compare(Hotel("Hotel माला"), Hotel("Hotel मिला")).NameSimilarity);
    }

    [Fact]
    public void MatchingProviderIdentityIsScopedToProvider()
    {
        var a = Hotel("Hotel Amber");
        var b = Hotel("Hotel Tulip") with { SourceId = a.SourceId, Strategy = "another-provider" };
        Assert.Equal("likely_distinct", PlaceMatcher.Compare(a, b).Decision);
        Assert.Equal("likely_duplicate", PlaceMatcher.Compare(a, b with { Strategy = a.Strategy }).Decision);
    }

    [Fact]
    public void ModelCitationsMustContainExactInputValues()
    {
        var pair = PlaceMatcher.Compare(Hotel(), Hotel());
        var answer = Answer(pair, [new("name", pair.Left.Name, pair.Right.Name), new("phone", "invented", "invented")]);
        var result = BedrockDuplicateJudge.ParseAssessment(answer, pair);
        Assert.Equal("uncertain", result.Decision);
        Assert.Equal("invalid_evidence", result.Error);
    }

    [Fact]
    public void ModelCannotConfirmBasedOnNameAlone()
    {
        var pair = PlaceMatcher.Compare(Hotel(), Hotel());
        var result = BedrockDuplicateJudge.ParseAssessment(Answer(pair, [new("name", pair.Left.Name, pair.Right.Name)]), pair);
        Assert.Equal("uncertain", result.Decision);
    }

    [Fact]
    public void SupportedModelSuggestionIsStillOnlyAnAssessment()
    {
        var pair = PlaceMatcher.Compare(Hotel(), Hotel());
        var result = BedrockDuplicateJudge.ParseAssessment(Answer(pair,
            [new("name", pair.Left.Name, pair.Right.Name), new("address", pair.Left.Address, pair.Right.Address)]), pair);
        Assert.Equal("same", result.Decision);
        Assert.Null(result.Error);
        Assert.Equal("likely_duplicate", (pair with { Ai = result }).Decision);
    }

    [Fact]
    public void ModelOutputForDifferentRowsIsRejected()
    {
        var pair = PlaceMatcher.Compare(Hotel(), Hotel());
        var answer = Answer(pair, []).Replace(pair.Left.Id.ToString(), Guid.NewGuid().ToString());
        Assert.Equal("invalid_evidence", BedrockDuplicateJudge.ParseAssessment(answer, pair).Error);
    }

    [Fact]
    public void ModelUncertaintyDowngradesStrongRuleSuggestion()
    {
        var pair = PlaceMatcher.Compare(Hotel(), Hotel());
        Assert.Equal("possible_duplicate", (pair with { Ai = new("uncertain", "Conflicting evidence", []) }).Decision);
    }

    [Fact]
    public async Task DisabledBedrockMakesNoRequest()
    {
        using var http = new HttpClient(new RejectHandler());
        var result = await new BedrockDuplicateJudge(http, new()).AssessAsync(PlaceMatcher.Compare(Hotel(), Hotel()), default);
        Assert.Equal("not_configured", result.Error);
    }

    [Fact]
    public async Task BedrockErrorsDoNotExposeResponseBody()
    {
        using var http = new HttpClient(new ErrorHandler());
        var options = new BedrockMatchingOptions { Enabled = true, Region = "us-east-1", ModelId = "test", ApiKey = "local-secret" };
        var result = await new BedrockDuplicateJudge(http, options).AssessAsync(PlaceMatcher.Compare(Hotel(), Hotel()), default);
        Assert.Equal("Bedrock HTTP 403", result.Error);
        Assert.DoesNotContain("local-secret", JsonSerializer.Serialize(result));
    }

    private static string Answer(PairAssessment pair, AiEvidence[] evidence) => JsonSerializer.Serialize(new
    {
        leftId = pair.Left.Id, rightId = pair.Right.Id, decision = "same", reason = "Compared supplied fields", evidence
    }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private sealed class RejectHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            throw new InvalidOperationException("No request should be made.");
    }

    private sealed class ErrorHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("local-secret") });
    }
}
