using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using OsmSharp;
using OsmSharp.Streams;
using OsmSharp.Tags;

namespace LeadsFinder.Worker;

/// <summary>
/// Reads a cached Geofabrik Northern Zone extract and searches OSM accommodation
/// objects. The cache is built once per worker process; jobs never fetch OSM tiles.
/// </summary>
public sealed class GeofabrikSearchStrategy(
    IConfiguration configuration,
    IHostEnvironment environment,
    ILogger<GeofabrikSearchStrategy> logger) : ISearchStrategy
{
    private const string ExtractUrl = "https://download.geofabrik.de/asia/india/northern-zone-latest.osm.pbf";
    private const string ExtractPage = "https://download.geofabrik.de/asia/india/northern-zone.html";
    private const int MaximumSearchRadiusMeters = 50_000;
    private const long MaximumDownloadBytes = 1_000_000_000;
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(15),
        ConnectCallback = async (context, token) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(
                context.DnsEndPoint.Host, AddressFamily.InterNetwork, token);
            Exception? lastError = null;
            foreach (var address in addresses)
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), token);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch (Exception error) when (error is SocketException or OperationCanceledException)
                {
                    socket.Dispose();
                    lastError = error;
                }
            }
            throw new HttpRequestException("Could not connect to the Geofabrik download host over IPv4.", lastError);
        }
    }) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly SemaphoreSlim _catalogLock = new(1, 1);
    private IReadOnlyList<CatalogPlace>? _catalog;

    public string Name => "geofabrik-osm";

    public async Task<IReadOnlyList<FoundPlace>> SearchAsync(
        SearchJob job,
        IReadOnlySet<string> excludedSourceIds,
        CancellationToken cancellationToken)
    {
        var catalog = await GetCatalogAsync(cancellationToken);
        var budget = job.TargetCount;
        var candidates = catalog
            .Where(place => MatchesPlaceType(place, job.PlaceType))
            .Where(place => !excludedSourceIds.Contains(place.SourceId))
            .Select(place => new { Place = place, Distance = DistanceMeters(
                job.Latitude, job.Longitude, place.Latitude, place.Longitude) })
            .Where(x => x.Distance <= (job.ShortagePolicy == "expand_radius"
                ? MaximumSearchRadiusMeters : job.RadiusMeters))
            .OrderBy(x => x.Distance)
            .ThenBy(x => x.Place.SourceId, StringComparer.Ordinal)
            .ToList();

        var selected = candidates.Where(x => x.Distance <= job.RadiusMeters).Take(budget).ToList();
        if (job.ShortagePolicy == "expand_radius" && selected.Count < budget)
        {
            selected.AddRange(candidates
                .Where(x => x.Distance > job.RadiusMeters)
                .Take(budget - selected.Count));
        }

        logger.LogInformation(
            "Geofabrik search {JobId}: {Count} observations for target {Target} (budget {Budget}, radius {RadiusMeters}m)",
            job.Id, selected.Count, job.TargetCount, budget, job.RadiusMeters);
        return selected.Select(x => x.Place.Place).ToArray();
    }

    private async Task<IReadOnlyList<CatalogPlace>> GetCatalogAsync(CancellationToken cancellationToken)
    {
        if (_catalog is not null)
            return _catalog;

        await _catalogLock.WaitAsync(cancellationToken);
        try
        {
            if (_catalog is not null)
                return _catalog;

            var configuredPath = configuration["Geofabrik:ExtractPath"]
                ?? Path.Combine("data", "northern-zone-latest.osm.pbf");
            var path = Path.IsPathFullyQualified(configuredPath)
                ? configuredPath
                : Path.GetFullPath(Path.Combine(environment.ContentRootPath, configuredPath));

            await EnsureExtractAsync(path, cancellationToken);
            var timer = Stopwatch.StartNew();
            var catalog = await Task.Run(() => ParseExtract(path, cancellationToken), cancellationToken);
            logger.LogInformation(
                "Loaded {Count} Northern Zone lodging objects from {Path} in {Elapsed}",
                catalog.Count, path, timer.Elapsed);
            _catalog = catalog;
            return catalog;
        }
        finally
        {
            _catalogLock.Release();
        }
    }

    private async Task EnsureExtractAsync(string path, CancellationToken cancellationToken)
    {
        if (File.Exists(path) && new FileInfo(path).Length > 1_000_000)
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + ".download-" + Guid.NewGuid().ToString("N");
        logger.LogInformation("Downloading Geofabrik Northern Zone extract from {Url}", ExtractUrl);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ExtractUrl);
            request.Headers.UserAgent.ParseAdd("LeadsFinder/0.1");
            using var responseTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            responseTimeout.CancelAfter(TimeSpan.FromSeconds(45));
            using var response = await Http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, responseTimeout.Token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > MaximumDownloadBytes)
                throw new InvalidDataException("The Geofabrik extract exceeds the 1 GB download limit.");

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using (var target = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 1024 * 1024, useAsync: true))
            {
                var buffer = new byte[1024 * 1024];
                long downloaded = 0;
                int length;
                while ((length = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    downloaded += length;
                    if (downloaded > MaximumDownloadBytes)
                        throw new InvalidDataException("The Geofabrik extract exceeds the 1 GB download limit.");
                    await target.WriteAsync(buffer.AsMemory(0, length), cancellationToken);
                }
            }

            if (new FileInfo(temporaryPath).Length <= 1_000_000)
                throw new InvalidDataException("The Geofabrik extract download is unexpectedly small.");

            File.Move(temporaryPath, path, overwrite: true);
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GeofabrikSourceException(
                "Geofabrik did not respond within 45 seconds. Check the extract download connection.", error);
        }
        catch (HttpRequestException error)
        {
            throw new GeofabrikSourceException(
                "Could not download the Geofabrik Northern Zone extract.", error);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static IReadOnlyList<CatalogPlace> ParseExtract(string path, CancellationToken cancellationToken)
    {
        var candidates = new List<Candidate>();
        var relationWayIds = new HashSet<long>();

        // Pass 1: retain only accommodation objects and relation member IDs.
        ReadPbf(path, false, false, false, cancellationToken, osm =>
        {
            if (!TryGetTag(osm.Tags, "tourism", out var tourism) || !IsLodging(tourism))
                return;
            if (osm.Id is not long id)
                return;

            var tags = CopyTags(osm.Tags);
            switch (osm)
            {
                case Node node when node.Latitude.HasValue && node.Longitude.HasValue:
                    candidates.Add(new Candidate("node", id, tags, [], [],
                        node.Latitude.Value, node.Longitude.Value));
                    break;
                case Way way:
                    candidates.Add(new Candidate("way", id, tags, way.Nodes ?? [], [], null, null));
                    break;
                case Relation relation:
                    var outerWays = (relation.Members ?? [])
                        .Where(member => member.Type == OsmGeoType.Way
                            && (member.Role is "outer" or "" or null))
                        .Select(member => member.Id).ToArray();
                    if (outerWays.Length == 0)
                        outerWays = (relation.Members ?? [])
                            .Where(member => member.Type == OsmGeoType.Way)
                            .Select(member => member.Id).ToArray();
                    foreach (var wayId in outerWays)
                        relationWayIds.Add(wayId);
                    candidates.Add(new Candidate("relation", id, tags, [], outerWays, null, null));
                    break;
            }
        });

        // Pass 2: resolve the ways used by tagged relations. Tagged ways already
        // supplied their node IDs in pass 1.
        var relationWayNodes = new Dictionary<long, long[]>();
        if (relationWayIds.Count > 0)
        {
            ReadPbf(path, true, false, true, cancellationToken, osm =>
            {
                if (osm is Way way && way.Id is long id && relationWayIds.Contains(id))
                    relationWayNodes[id] = way.Nodes ?? [];
            });
        }

        var neededNodeIds = new HashSet<long>(candidates
            .Where(x => x.OsmType == "way")
            .SelectMany(x => x.NodeIds));
        foreach (var wayNodes in relationWayNodes.Values)
            neededNodeIds.UnionWith(wayNodes);

        // Pass 3: keep only coordinates needed for lodging geometries.
        var nodeCoordinates = new Dictionary<long, (double Latitude, double Longitude)>();
        if (neededNodeIds.Count > 0)
        {
            ReadPbf(path, false, true, true, cancellationToken, osm =>
            {
                if (osm is Node node && node.Id is long id && neededNodeIds.Contains(id)
                    && node.Latitude is double latitude && node.Longitude is double longitude)
                    nodeCoordinates[id] = (latitude, longitude);
            });
        }

        var places = new List<CatalogPlace>(candidates.Count);
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (double Latitude, double Longitude)? location = candidate.OsmType switch
            {
                "node" when candidate.Latitude.HasValue && candidate.Longitude.HasValue
                    => (candidate.Latitude.Value, candidate.Longitude.Value),
                "way" => Center(candidate.NodeIds, nodeCoordinates),
                "relation" => Center(candidate.WayIds
                    .SelectMany(id => relationWayNodes.GetValueOrDefault(id) ?? []), nodeCoordinates),
                _ => null
            };
            if (location is null)
                continue;

            var sourceId = $"{candidate.OsmType}/{candidate.Id}";
            var tourism = candidate.Tags["tourism"];
            var name = FirstTag(candidate.Tags, "name", "name:en", "brand", "operator")
                ?? $"Unnamed {tourism.Replace('_', ' ')} (OSM {sourceId})";
            var address = Address(candidate.Tags);
            var website = FirstTag(candidate.Tags, "contact:website", "website");
            if (website is not null && !website.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !website.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                website = "https://" + website;
            if (website is not null && (!Uri.TryCreate(website, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)))
                website = null;

            var raw = JsonSerializer.Serialize(new
            {
                provider = "OpenStreetMap",
                extract = ExtractPage,
                osmType = candidate.OsmType,
                osmId = candidate.Id,
                tags = candidate.Tags
            });
            var place = new FoundPlace(sourceId, name, address,
                location.Value.Latitude, location.Value.Longitude,
                "geofabrik-osm", $"https://www.openstreetmap.org/{sourceId}",
                FirstTag(candidate.Tags, "contact:phone", "phone"),
                FirstTag(candidate.Tags, "contact:email", "email"), website, raw);
            places.Add(new CatalogPlace(place, tourism,
                FirstTag(candidate.Tags, "guest_house")));
        }

        return places;
    }

    private static void ReadPbf(string path, bool ignoreNodes, bool ignoreWays,
        bool ignoreRelations, CancellationToken cancellationToken, Action<OsmGeo> read)
    {
        using var file = File.OpenRead(path);
        var source = new PBFOsmStreamSource(file);
        var count = 0;
        foreach (var osm in source)
        {
            if (++count % 10_000 == 0)
                cancellationToken.ThrowIfCancellationRequested();
            if ((ignoreNodes && osm is Node)
                || (ignoreWays && osm is Way)
                || (ignoreRelations && osm is Relation))
                continue;
            read(osm);
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static bool IsLodging(string value) => value is
        "hotel" or "motel" or "guest_house" or "hostel" or "apartment"
        or "chalet" or "alpine_hut" or "wilderness_hut" or "bed_and_breakfast";

    private static bool MatchesPlaceType(CatalogPlace place, string requestedType) => requestedType switch
    {
        "hotel" => place.Tourism is "hotel" or "motel",
        "bed_and_breakfast" => place.Tourism == "bed_and_breakfast"
            || (place.Tourism == "guest_house" && place.GuestHouseType == "bed_and_breakfast"),
        "guest_house" => place.Tourism == "guest_house",
        "vacation_rental" => place.Tourism is "apartment" or "chalet",
        _ => true
    };

    private static bool TryGetTag(TagsCollectionBase? tags, string key, out string value)
    {
        value = "";
        return tags is not null && tags.TryGetValue(key, out value!)
            && !string.IsNullOrWhiteSpace(value);
    }

    private static Dictionary<string, string> CopyTags(TagsCollectionBase? tags)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (tags is not null)
            foreach (var tag in tags)
                result[tag.Key] = tag.Value;
        return result;
    }

    private static string? FirstTag(Dictionary<string, string> tags, params string[] keys)
    {
        foreach (var key in keys)
            if (tags.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                return value.Trim();
        return null;
    }

    private static string Address(Dictionary<string, string> tags)
    {
        var full = FirstTag(tags, "addr:full");
        if (full is not null)
            return full;
        var parts = new[]
        {
            FirstTag(tags, "addr:housenumber"), FirstTag(tags, "addr:street"),
            FirstTag(tags, "addr:suburb"), FirstTag(tags, "addr:city"),
            FirstTag(tags, "addr:state"), FirstTag(tags, "addr:postcode")
        };
        return string.Join(", ", parts.Where(x => x is not null));
    }

    private static (double Latitude, double Longitude)? Center(
        IEnumerable<long> nodeIds, Dictionary<long, (double Latitude, double Longitude)> coordinates)
    {
        double latitude = 0, longitude = 0;
        var count = 0;
        foreach (var id in nodeIds.Distinct())
        {
            if (!coordinates.TryGetValue(id, out var point))
                continue;
            latitude += point.Latitude;
            longitude += point.Longitude;
            count++;
        }
        return count == 0 ? null : (latitude / count, longitude / count);
    }

    private static double DistanceMeters(double latitude1, double longitude1,
        double latitude2, double longitude2)
    {
        const double earthRadius = 6_371_000;
        var lat = (latitude2 - latitude1) * Math.PI / 180;
        var lon = (longitude2 - longitude1) * Math.PI / 180;
        var a = Math.Pow(Math.Sin(lat / 2), 2)
            + Math.Cos(latitude1 * Math.PI / 180)
            * Math.Cos(latitude2 * Math.PI / 180)
            * Math.Pow(Math.Sin(lon / 2), 2);
        return 2 * earthRadius * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }

    private sealed record Candidate(string OsmType, long Id, Dictionary<string, string> Tags,
        long[] NodeIds, long[] WayIds, double? Latitude, double? Longitude);

    private sealed record CatalogPlace(FoundPlace Place, string Tourism, string? GuestHouseType)
    {
        public string SourceId => Place.SourceId;
        public double Latitude => Place.Latitude;
        public double Longitude => Place.Longitude;
    }
}

public sealed class GeofabrikSourceException(string message, Exception innerException)
    : Exception(message, innerException);
