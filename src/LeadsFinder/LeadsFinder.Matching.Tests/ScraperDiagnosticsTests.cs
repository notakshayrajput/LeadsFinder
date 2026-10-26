using LeadsFinder.Worker;

namespace LeadsFinder.Matching.Tests;

public sealed class ScraperDiagnosticsTests
{
    private const string SearchSuccess = """{"level":"info","message":"job finished","status":"success","job":"URL: https://www.google.com/maps/search/hotels/"}""";
    private const string SearchFailure = """{"level":"info","message":"job finished","status":"failed","job":"URL: https://www.google.com/maps/search/hotels/","error":"playwright: target closed: Target page, context or browser has been closed"}""";
    private const string PlaceFailure = """{"level":"error","message":"job finished","status":"failed","job":"URL: https://www.google.com/maps/place/Example","error":"request failed"}""";

    [Fact]
    public void BrowserFailureIsNotMistakenForSuccessfulEmptySearch()
    {
        var diagnostics = GoogleMapsScraperDiagnostics.Parse("banner\n" + SearchFailure);
        Assert.Equal(1, diagnostics.FailedSearches);
        Assert.Contains("browser closed", diagnostics.FailureMessage(false));
    }

    [Fact]
    public void LegitimateEmptySearchRemainsSuccessful()
    {
        var diagnostics = GoogleMapsScraperDiagnostics.Parse(SearchSuccess);
        Assert.Null(diagnostics.FailureMessage(false));
    }

    [Fact]
    public void FailingAllDetailPagesIsAnErrorEvenWhenSearchPageSucceeded()
    {
        var diagnostics = GoogleMapsScraperDiagnostics.Parse(SearchSuccess + "\n" + PlaceFailure);
        Assert.NotNull(diagnostics.FailureMessage(false));
    }

    [Fact]
    public void PartialDetailFailuresKeepRealResults()
    {
        var diagnostics = GoogleMapsScraperDiagnostics.Parse(SearchSuccess + "\n" + PlaceFailure);
        Assert.Null(diagnostics.FailureMessage(true));
        Assert.Equal(1, diagnostics.FailedJobs);
    }

    [Fact]
    public void FailedSearchCannotSucceedBecauseAResultsFileContainsRows()
    {
        Assert.NotNull(GoogleMapsScraperDiagnostics.Parse(SearchFailure).FailureMessage(true));
    }

    [Fact]
    public void MalformedOrNonObjectLogRecordsAreIgnored()
    {
        Assert.Null(GoogleMapsScraperDiagnostics.Parse("{broken\n[]\n{\"error\":{}}\n" + SearchSuccess).FailureMessage(false));
    }
}
