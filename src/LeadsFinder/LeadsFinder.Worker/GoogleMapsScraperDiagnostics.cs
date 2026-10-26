using System.Text.Json;

namespace LeadsFinder.Worker;

public sealed record GoogleMapsScraperDiagnostics(int SuccessfulJobs, int FailedJobs,
    int SuccessfulSearches, int FailedSearches, bool HasErrors, bool BrowserClosed)
{
    public static GoogleMapsScraperDiagnostics Parse(string logs)
    {
        var successful = 0;
        var failed = 0;
        var successfulSearches = 0;
        var failedSearches = 0;
        var hasErrors = false;
        var browserClosed = false;
        foreach (var line in logs.Split('\n'))
        {
            if (!line.TrimStart().StartsWith('{')) continue;
            try
            {
                using var json = JsonDocument.Parse(line);
                var row = json.RootElement;
                hasErrors |= Text(row, "level") == "error";
                var error = Text(row, "error") ?? "";
                browserClosed |= error.Contains("target closed", StringComparison.OrdinalIgnoreCase)
                    || error.Contains("browser has been closed", StringComparison.OrdinalIgnoreCase);
                if (Text(row, "message") != "job finished") continue;
                var search = (Text(row, "job") ?? "").Contains("google.com/maps/search/", StringComparison.OrdinalIgnoreCase);
                if (Text(row, "status") == "success")
                {
                    successful++;
                    if (search) successfulSearches++;
                }
                else if (Text(row, "status") == "failed")
                {
                    failed++;
                    if (search) failedSearches++;
                }
            }
            catch (JsonException) { /* Banners and native browser messages are not structured logs. */ }
        }
        return new(successful, failed, successfulSearches, failedSearches, hasErrors, browserClosed);
    }

    public string? FailureMessage(bool hasRawResults)
    {
        if ((FailedSearches > 0 && SuccessfulSearches == 0) || (!hasRawResults && (HasErrors || FailedJobs > 0)))
            return BrowserClosed
                ? "Google Maps Scraper browser closed unexpectedly. On Windows, rebuild with scripts/Setup-GoogleMapsScraper.ps1."
                : "Google Maps Scraper requests failed and did not produce usable results. Check worker logs and retry.";
        return null;
    }

    private static string? Text(JsonElement row, string key) => row.ValueKind == JsonValueKind.Object
        && row.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
