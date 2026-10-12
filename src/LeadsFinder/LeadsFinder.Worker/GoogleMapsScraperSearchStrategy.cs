using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LeadsFinder.Worker;

/// <summary>Runs the cloned google-maps-scraper CLI in an isolated process per search.</summary>
public sealed class GoogleMapsScraperSearchStrategy(
    IConfiguration configuration,
    IHostEnvironment environment,
    ILogger<GoogleMapsScraperSearchStrategy> logger) : ISearchStrategy
{
    private static readonly string[] LodgingWords =
        ["hotel", "motel", "lodg", "resort", "hostel", "guest", "bed and breakfast",
         "bnb", "b&b", "vacation", "holiday", "rental", "apartment", "cottage",
         "homestay", "home stay", "inn", "chalet", "farmstay", "accommodation"];

    public string Name => "google-maps-scraper";

    public async Task<IReadOnlyList<FoundPlace>> SearchAsync(
        SearchJob job, IReadOnlySet<string> excludedSourceIds, CancellationToken cancellationToken)
    {
        var executable = configuration["GoogleMapsScraper:ExecutablePath"];
        if (string.IsNullOrWhiteSpace(executable))
            executable = Path.GetFullPath(Path.Combine(environment.ContentRootPath,
                "..", "google-maps-scraper", "bin",
                OperatingSystem.IsWindows() ? "google-maps-scraper.exe" : "google-maps-scraper"));
        if (!File.Exists(executable))
            throw new GoogleMapsScraperSourceException(
                $"Google Maps Scraper executable is missing at {executable}. Build the cloned CLI first.");

        var candidates = new Dictionary<string, FoundPlace>(StringComparer.Ordinal);
        await RunRadiusAsync(job, job.RadiusMeters, executable, candidates,
            excludedSourceIds, cancellationToken);

        if (job.ShortagePolicy == "expand_radius" && job.RadiusMeters < 50_000
            && candidates.Count < job.TargetCount)
        {
            await RunRadiusAsync(job, 50_000, executable, candidates,
                excludedSourceIds, cancellationToken);
        }

        return candidates.Values
            .OrderBy(place => DistanceMeters(job.Latitude, job.Longitude,
                place.Latitude, place.Longitude))
            .Take(job.TargetCount)
            .ToArray();
    }

    private async Task RunRadiusAsync(SearchJob job, int radiusMeters, string executable,
        Dictionary<string, FoundPlace> candidates, IReadOnlySet<string> excludedSourceIds,
        CancellationToken token)
    {
        var directory = Path.Combine(Path.GetTempPath(), "LeadsFinder-google-maps",
            job.Id.ToString("N"), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var input = Path.Combine(directory, "queries.txt");
            var results = Path.Combine(directory, "results.jsonl");
            var args = new List<string>
            {
                "-input", input, "-results", results, "-json", "-depth", "1",
                "-c", "4", "-browser-pool-size", "2", "-pages-per-browser", "2",
                "-lang", "en", "-exit-on-inactivity", "3m"
            };
            if (job.RequireEmail)
                args.Add("-email");

            var latSpan = radiusMeters / 111_320.0;
            var lonSpan = radiusMeters / (111_320.0 * Math.Max(0.01,
                Math.Cos(job.Latitude * Math.PI / 180)));
            var useGrid = (radiusMeters > 1_000 || job.TargetCount > 20)
                && job.Latitude - latSpan > -90 && job.Latitude + latSpan < 90
                && job.Longitude - lonSpan > -180 && job.Longitude + lonSpan < 180;
            var queries = Queries(job.PlaceType);
            // A direct Maps URL targets the chosen point. In grid mode the CLI
            // constructs one URL per cell, so its inputs must be plain queries.
            await File.WriteAllLinesAsync(input, useGrid ? queries : queries.Select(query =>
                $"https://www.google.com/maps/search/{Uri.EscapeDataString(query)}/@{job.Latitude.ToString("F6", CultureInfo.InvariantCulture)},{job.Longitude.ToString("F6", CultureInfo.InvariantCulture)},16z"), token);
            if (useGrid)
            {
                // Roughly eight cells across the selected diameter, capped to avoid
                // an unbounded number of browser jobs for a 50 km search.
                var cellKm = Math.Clamp(radiusMeters / 4_000.0, 0.5, 10);
                var zoom = cellKm <= 1 ? 16 : cellKm <= 3 ? 14 : cellKm <= 7 ? 12 : 11;
                args.AddRange(["-grid-bbox", string.Join(",", new[]
                    {
                        job.Latitude - latSpan, job.Longitude - lonSpan,
                        job.Latitude + latSpan, job.Longitude + lonSpan
                    }.Select(value => value.ToString("F6", CultureInfo.InvariantCulture))),
                    "-grid-cell", cellKm.ToString("F2", CultureInfo.InvariantCulture),
                    "-zoom", zoom.ToString(CultureInfo.InvariantCulture)]);
            }
            else
            {
                args.AddRange(["-geo", FormattableString.Invariant($"{job.Latitude:F6},{job.Longitude:F6}"),
                    "-zoom", "16", "-radius", radiusMeters.ToString(CultureInfo.InvariantCulture)]);
            }

            var start = new ProcessStartInfo(executable)
            {
                WorkingDirectory = directory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.Environment["DISABLE_TELEMETRY"] = "1";
            start.Environment["PLAYWRIGHT_BROWSERS_PATH"] = configuration["GoogleMapsScraper:BrowserPath"]
                ?? Path.GetFullPath(Path.Combine(environment.ContentRootPath,
                    "..", "google-maps-scraper", ".playwright"));
            foreach (var arg in args)
                start.ArgumentList.Add(arg);

            using var process = new Process { StartInfo = start };
            try
            {
                process.Start();
                var stdout = process.StandardOutput.ReadToEndAsync(token);
                var stderr = process.StandardError.ReadToEndAsync(token);
                await process.WaitForExitAsync(token);
                var output = await stdout;
                var errors = await stderr;
                if (process.ExitCode != 0)
                    throw new GoogleMapsScraperSourceException(
                        $"Google Maps Scraper exited with code {process.ExitCode}: {Tail(errors, 500)}");
                logger.LogInformation("Google Maps Scraper job {JobId}: radius {Radius} m, {Mode}, {Output}",
                    job.Id, radiusMeters, useGrid ? "grid" : "single URL", Tail(output, 200));
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None);
                }
                throw;
            }
            catch (System.ComponentModel.Win32Exception error)
            {
                throw new GoogleMapsScraperSourceException(
                    "Could not start the Google Maps Scraper executable.", error);
            }

            if (!File.Exists(results))
                throw new GoogleMapsScraperSourceException("Google Maps Scraper produced no results file.");

            foreach (var line in await File.ReadAllLinesAsync(results, token))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var json = JsonDocument.Parse(line);
                var row = json.RootElement;
                var id = Identity(row);
                var link = String(row, "link");
                var name = String(row, "title");
                var category = String(row, "category");
                if (id is null || string.IsNullOrWhiteSpace(name) || excludedSourceIds.Contains(id)
                    || candidates.ContainsKey(id) || !IsLodging(category, job.PlaceType)
                    || !Uri.TryCreate(link, UriKind.Absolute, out var uri)
                    || uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp
                    || !Number(row, "latitude", out var latitude)
                    || !(Number(row, "longitude", out var longitude)
                        || Number(row, "longtitude", out longitude))
                    || !double.IsFinite(latitude) || latitude is < -90 or > 90
                    || !double.IsFinite(longitude) || longitude is < -180 or > 180
                    || DistanceMeters(job.Latitude, job.Longitude, latitude, longitude) > radiusMeters)
                    continue;

                var email = row.TryGetProperty("emails", out var emails)
                    && emails.ValueKind == JsonValueKind.Array
                    ? emails.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
                        .Select(x => x.GetString()).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))
                    : null;
                candidates.Add(id, new FoundPlace(id, name, String(row, "address") ?? "",
                    latitude, longitude, Name, link!, String(row, "phone"), email,
                    String(row, "web_site"), line));
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string[] Queries(string type) => type switch
    {
        "hotel" => ["hotels"],
        "bed_and_breakfast" => ["bed and breakfasts"],
        "guest_house" => ["guest houses"],
        "vacation_rental" => ["vacation rentals"],
        _ => ["hotels", "guest houses", "vacation rentals"]
    };

    private static bool IsLodging(string? category, string type)
    {
        if (string.IsNullOrWhiteSpace(category)) return true;
        var text = category.ToLowerInvariant();
        if (!LodgingWords.Any(text.Contains)) return false;
        return type switch
        {
            "hotel" => text.Contains("hotel") || text.Contains("motel") || text.Contains("resort"),
            "bed_and_breakfast" => text.Contains("bed and breakfast") || text.Contains("bnb") || text.Contains("b&b"),
            "guest_house" => text.Contains("guest") || text.Contains("inn"),
            "vacation_rental" => text.Contains("rental") || text.Contains("apartment")
                || text.Contains("cottage") || text.Contains("chalet") || text.Contains("homestay"),
            _ => true
        };
    }

    private static string? Identity(JsonElement row)
    {
        foreach (var (field, prefix) in new[] { ("place_id", "place:"),
                     ("cid", "cid:"), ("data_id", "data:") })
        {
            var value = String(row, field);
            if (!string.IsNullOrWhiteSpace(value)) return prefix + value;
        }
        var link = String(row, "link");
        return string.IsNullOrWhiteSpace(link) ? null
            : "url:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(link)))[..32];
    }

    private static string? String(JsonElement row, string field) =>
        row.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static bool Number(JsonElement row, string field, out double number)
    {
        number = 0;
        return row.TryGetProperty(field, out var value)
            && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out number);
    }

    private static double DistanceMeters(double lat1, double lon1, double lat2, double lon2)
    {
        const double earthRadius = 6_371_000;
        var lat = (lat2 - lat1) * Math.PI / 180;
        var lon = (lon2 - lon1) * Math.PI / 180;
        var a = Math.Pow(Math.Sin(lat / 2), 2) + Math.Cos(lat1 * Math.PI / 180)
            * Math.Cos(lat2 * Math.PI / 180) * Math.Pow(Math.Sin(lon / 2), 2);
        return 2 * earthRadius * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }

    private static string Tail(string text, int max) => text.Length <= max ? text : text[^max..];
}

public sealed class GoogleMapsScraperSourceException : Exception
{
    public GoogleMapsScraperSourceException(string message) : base(message) { }
    public GoogleMapsScraperSourceException(string message, Exception inner) : base(message, inner) { }
}
