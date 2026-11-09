using System.Security.Cryptography;

namespace LeadsFinder.Api;

public sealed record DemoGenerateRequest(double Latitude, double Longitude, int RadiusMeters, int Count);
public sealed record DemoGenerationBatchResponse(Guid Id, double Latitude, double Longitude, int RadiusMeters,
    int Count, DateTime CreatedAtUtc, string CreatedByName, string Locality);

/// <summary>
/// A persisted recipe for a source region. Individual source rows are generated only
/// when fetched, so a million-row batch occupies one metadata record on disk.
/// </summary>
public sealed class DemoGenerationBatch
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public int RadiusMeters { get; set; }
    public int Count { get; set; }
    public int Seed { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public Guid CreatedById { get; set; }
    public string CreatedByName { get; set; } = "";
    public string Locality { get; set; } = "";
}

public sealed partial class DemoStore
{
    private const int GeneratedCapacity = 10_000_000;
    private const double GeneratedEarthRadius = 6_371_000d;
    private const double GoldenAngle = 2.39996322972865332;
    private static readonly int[] MissingPairs = [3, 5, 9, 6, 10, 12];
    private static readonly string[] GeneratedBrands = ["Amber", "Cedar", "Lotus", "Orchid", "Horizon", "Regal", "Olive", "Sapphire"];
    private static readonly string[] GeneratedStyles = ["Residency", "Suites", "Inn", "Retreat", "Court", "Lodge", "Haven", "Stay"];

    public DemoGenerationBatchResponse GenerateRows(Guid adminId, DemoGenerateRequest request)
    {
        lock (_gate)
        {
            var admin = RequireAdmin(adminId);
            ValidateCoordinates(request.Latitude, request.Longitude);
            if (request.RadiusMeters is < 100 or > 50_000)
                throw new DemoRequestException("Radius must be between 100 and 50,000 meters.");
            if (request.Count is < 1 or > 1_000_000)
                throw new DemoRequestException("Generate between 1 and 1,000,000 rows in one batch.");
            if (request.Count > GeneratedCapacity - GeneratedRowsCount())
                throw new DemoRequestException("The generated source has reached its 10,000,000-row capacity.", 409);

            var nearest = DemoCatalog.Locations.MinBy(location => GeneratedDistance(request.Latitude, request.Longitude,
                location.Latitude, location.Longitude))!;
            var locality = GeneratedDistance(request.Latitude, request.Longitude, nearest.Latitude, nearest.Longitude) <= 100_000
                ? nearest.DisplayName.Split(',')[0]
                : FormattableString.Invariant($"Area {request.Latitude:F3}, {request.Longitude:F3}");
            var batch = new DemoGenerationBatch
            {
                Latitude = request.Latitude, Longitude = request.Longitude, RadiusMeters = request.RadiusMeters,
                Count = request.Count, Seed = RandomNumberGenerator.GetInt32(1, int.MaxValue), CreatedAtUtc = Now,
                CreatedById = admin.Id, CreatedByName = admin.Name, Locality = locality
            };
            _database.GenerationBatches.Add(batch);
            Save();
            return GenerationBatchResponse(batch);
        }
    }

    public DemoGenerationBatchResponse[] GenerationBatches(Guid adminId)
    {
        lock (_gate)
        {
            RequireAdmin(adminId);
            return GenerationBatchResponses();
        }
    }

    private int GeneratedRowsCount() => _database.GenerationBatches.Sum(batch => batch.Count);

    private DemoGenerationBatchResponse[] GenerationBatchResponses() => _database.GenerationBatches
        .OrderByDescending(batch => batch.CreatedAtUtc).ThenByDescending(batch => batch.Id)
        .Select(GenerationBatchResponse).ToArray();

    private static DemoGenerationBatchResponse GenerationBatchResponse(DemoGenerationBatch batch) =>
        new(batch.Id, batch.Latitude, batch.Longitude, batch.RadiusMeters, batch.Count,
            batch.CreatedAtUtc, batch.CreatedByName, batch.Locality);

    /// <summary>
    /// Returns true when a generated source slot was scanned (candidate may be null).
    /// False means all generated batches are exhausted and the baseline source can run.
    /// Cursors address persistent batch insertion order, including spatially skipped rows.
    /// </summary>
    private bool TryNextGenerated(DemoJob job, out DemoCandidate? candidate, out bool exhausted)
    {
        candidate = null;
        exhausted = false;
        long offset = 0;
        foreach (var batch in _database.GenerationBatches)
        {
            var end = offset + batch.Count;
            if (job.GeneratedSearchCursor >= end) { offset = end; continue; }
            var distance = GeneratedDistance(job.Latitude, job.Longitude, batch.Latitude, batch.Longitude);
            if (job.PlaceType is not ("any_lodging" or "hotel") || distance > batch.RadiusMeters + (double)job.RadiusMeters + .001)
            {
                job.GeneratedSearchCursor = end;
                offset = end;
                continue;
            }

            // Row index increases with radius. Circle intersection and a radial interval
            // eliminate distant batches and whole rings before any business is allocated.
            var minRadius = Math.Max(0, distance - job.RadiusMeters - .001);
            var maxRadius = Math.Min(batch.RadiusMeters, distance + job.RadiusMeters + .001);
            var denominator = Math.Pow(Math.Sin(batch.RadiusMeters / (2 * GeneratedEarthRadius)), 2);
            var minFraction = Math.Pow(Math.Sin(minRadius / (2 * GeneratedEarthRadius)), 2) / denominator;
            var maxFraction = Math.Pow(Math.Sin(maxRadius / (2 * GeneratedEarthRadius)), 2) / denominator;
            var first = Math.Clamp((int)Math.Floor(batch.Count * minFraction - .5) - 1, 0, batch.Count);
            var last = Math.Clamp((int)Math.Ceiling(batch.Count * maxFraction - .5) + 2, 0, batch.Count);
            job.GeneratedSearchCursor = Math.Max(job.GeneratedSearchCursor, offset + first);
            if (job.GeneratedSearchCursor >= offset + last)
            {
                job.GeneratedSearchCursor = end;
                offset = end;
                continue;
            }

            var index = (int)(job.GeneratedSearchCursor++ - offset);
            var point = GeneratedPoint(batch, index);
            if (GeneratedDistance(job.Latitude, job.Longitude, point.Latitude, point.Longitude) <= job.RadiusMeters + .001)
            {
                var business = GeneratedBusiness(batch, index, point.Latitude, point.Longitude);
                candidate = new DemoCandidate(business, business.Key);
            }
            return true;
        }
        exhausted = true;
        return false;
    }

    private static (double Latitude, double Longitude) GeneratedPoint(DemoGenerationBatch batch, int index)
    {
        // Equal-area spherical cap + golden-angle bearings form a uniform disk. A 3D
        // tangent basis avoids the longitude singularity at the poles and wraps naturally
        // across the antimeridian without clipping rows to a latitude/longitude rectangle.
        var latitude = batch.Latitude * Math.PI / 180;
        var longitude = batch.Longitude * Math.PI / 180;
        var bearing = index * GoldenAngle + batch.Seed / (double)int.MaxValue * Math.Tau;
        var angle = 2 * Math.Asin(Math.Sqrt((index + .5) / batch.Count)
            * Math.Sin(batch.RadiusMeters / (2 * GeneratedEarthRadius)));
        var cosLat = Math.Cos(latitude);
        var sinLat = Math.Sin(latitude);
        var cosLon = Math.Cos(longitude);
        var sinLon = Math.Sin(longitude);
        var north = Math.Cos(bearing) * Math.Sin(angle);
        var east = Math.Sin(bearing) * Math.Sin(angle);
        var radial = Math.Cos(angle);
        var x = radial * cosLat * cosLon - north * sinLat * cosLon - east * sinLon;
        var y = radial * cosLat * sinLon - north * sinLat * sinLon + east * cosLon;
        var z = radial * sinLat + north * cosLat;
        return (Math.Atan2(z, Math.Sqrt(x * x + y * y)) * 180 / Math.PI, Math.Atan2(y, x) * 180 / Math.PI);
    }

    private static DemoBusiness GeneratedBusiness(DemoGenerationBatch batch, int index, double latitude, double longitude)
    {
        var mask = MissingPairs[(index % MissingPairs.Length + batch.Seed % MissingPairs.Length) % MissingPairs.Length];
        var token = $"{batch.Id:N}";
        var contact = $"stay{token[..12]}{index + 1}";
        return new DemoBusiness
        {
            Key = $"generated:{token}:{index:x8}",
            Name = $"{GeneratedBrands[index % GeneratedBrands.Length]} {GeneratedStyles[index / GeneratedBrands.Length % GeneratedStyles.Length]}",
            IsSample = true,
            Latitude = latitude, Longitude = longitude, PlaceType = "hotel", Locality = batch.Locality,
            Address = (mask & 1) == 0 ? $"{index % 500 + 1}, {batch.Locality}" : null,
            Phone = (mask & 2) == 0 ? $"+91 00000 {index % 100_000:00000}" : null,
            Email = (mask & 4) == 0 ? $"reservations@{contact}.example" : null,
            Website = (mask & 8) == 0 ? $"https://{contact}.example" : null
        };
    }

    private static double GeneratedDistance(double latitude1, double longitude1, double latitude2, double longitude2)
    {
        var lat1 = latitude1 * Math.PI / 180;
        var lat2 = latitude2 * Math.PI / 180;
        var dLon = (longitude2 - longitude1) * Math.PI / 180;
        var value = Math.Pow(Math.Sin((lat2 - lat1) / 2), 2)
            + Math.Cos(lat1) * Math.Cos(lat2) * Math.Pow(Math.Sin(dLon / 2), 2);
        return 2 * GeneratedEarthRadius * Math.Asin(Math.Sqrt(Math.Clamp(value, 0, 1)));
    }
}
