namespace LeadsFinder.Api;

/// <summary>
/// A deterministic local source and lookup database. Rows are generated on demand,
/// so increasing the catalog to ten million does not allocate ten million objects.
/// Nine canonical places occupy each ten-observation block; the tenth repeats the first.
/// Fixed grid layers keep existing business identifiers and coordinates stable as it grows.
/// </summary>
public sealed class DemoCatalog
{
    public static readonly string[] PlaceTypes = ["any_lodging", "hotel", "guest_house", "hostel", "apartment"];
    public static readonly LocationSearchResult[] Locations =
    [
        new("delhi", "New Delhi, Delhi NCR", 28.6139, 77.2090),
        new("connaught-place", "Connaught Place, New Delhi", 28.6315, 77.2167),
        new("aerocity", "Aerocity, New Delhi", 28.5510, 77.1233),
        new("gurugram", "Gurugram, Haryana, Delhi NCR", 28.4595, 77.0266),
        new("noida", "Noida, Uttar Pradesh, Delhi NCR", 28.5355, 77.3910),
        new("faridabad", "Faridabad, Haryana, Delhi NCR", 28.4089, 77.3178),
        new("ghaziabad", "Ghaziabad, Uttar Pradesh, Delhi NCR", 28.6692, 77.4538),
        new("greater-noida", "Greater Noida, Uttar Pradesh, Delhi NCR", 28.4744, 77.5040),
        new("dwarka", "Dwarka, New Delhi", 28.5921, 77.0460),
        new("rohini", "Rohini, New Delhi", 28.7495, 77.0565)
    ];
    private const int Columns = 1000, Rows = 900, BusinessesPerLayer = Columns * Rows;
    private const double South = 28.25, North = 29.05, West = 76.75, East = 77.75;
    private readonly int _businessCount;
    private readonly int _layers;
    public int Size { get; }

    public DemoCatalog(int size)
    {
        if (size is < 1_000 or > 10_000_000)
            throw new InvalidOperationException("Demo:CatalogSize must be between 1,000 and 10,000,000.");
        Size = size;
        _businessCount = (size / 10) * 9 + Math.Min(size % 10, 9);
        _layers = (int)Math.Ceiling(_businessCount / (double)BusinessesPerLayer);
    }

    public DemoCandidate? Next(DemoJob job, out bool exhausted)
    {
        var latSpan = job.RadiusMeters / 110_540d;
        var lonSpan = job.RadiusMeters / (111_320d * Math.Max(0.01, Math.Cos(job.Latitude * Math.PI / 180)));
        var minRow = Math.Clamp((int)Math.Floor((job.Latitude - latSpan - South) / (North - South) * Rows) - 1, 0, Rows - 1);
        var maxRow = Math.Clamp((int)Math.Ceiling((job.Latitude + latSpan - South) / (North - South) * Rows) + 1, 0, Rows - 1);
        var minCol = Math.Clamp((int)Math.Floor((job.Longitude - lonSpan - West) / (East - West) * Columns) - 1, 0, Columns - 1);
        var maxCol = Math.Clamp((int)Math.Ceiling((job.Longitude + lonSpan - West) / (East - West) * Columns) + 1, 0, Columns - 1);
        var width = maxCol - minCol + 1;
        var count = (long)width * (maxRow - minRow + 1) * _layers * 2;
        exhausted = job.SearchCursor >= count;
        if (exhausted) return null;
        var cursor = job.SearchCursor++;
        var duplicate = cursor % 2 == 1;
        var layer = (int)(cursor / 2 % _layers);
        var cell = cursor / 2 / _layers;
        var row = minRow + (int)(cell / width);
        var column = minCol + (int)(cell % width);
        var id = layer * BusinessesPerLayer + row * Columns + column;
        if (id >= _businessCount || (duplicate && id % 9 != 0)) return null;
        var observation = (id / 9) * 10 + (duplicate ? 9 : id % 9);
        if (observation >= Size) return null;
        var business = Lookup(id, complete: false);
        if (job.PlaceType != "any_lodging" && job.PlaceType != business.PlaceType) return null;
        if (Distance(job.Latitude, job.Longitude, business.Latitude, business.Longitude) > job.RadiusMeters) return null;
        return new DemoCandidate(business, $"catalog:{observation}");
    }

    public DemoBusiness Lookup(int id, bool complete)
    {
        var cell = id % BusinessesPerLayer;
        var row = cell / Columns;
        var column = cell % Columns;
        var jitterX = ((id * 2654435761u) % 1000) / 1000d;
        var jitterY = ((id * 2246822519u) % 1000) / 1000d;
        var latitude = South + (row + 0.2 + 0.6 * jitterY) / Rows * (North - South);
        var longitude = West + (column + 0.2 + 0.6 * jitterX) / Columns * (East - West);
        var city = Locations.MinBy(x => Math.Pow(x.Latitude - latitude, 2) + Math.Pow(x.Longitude - longitude, 2))!;
        var locality = city.DisplayName.Split(',')[0];
        string[] brands = ["Amber", "Cedar", "Lotus", "Imperial", "Orchid", "Horizon", "Regal", "Park", "Sapphire", "Olive", "Grand", "Silver"];
        string[] styles = ["Residency", "Suites", "Inn", "Retreat", "Heights", "Palace", "Court", "Lodge", "Haven", "Stay"];
        var kind = (id % 10) switch { < 6 => "hotel", < 8 => "guest_house", 8 => "hostel", _ => "apartment" };
        return new DemoBusiness
        {
            Key = $"catalog:{id}", CatalogId = id,
            Name = $"{brands[(id + id / BusinessesPerLayer) % brands.Length]} {styles[id / brands.Length % styles.Length]}",
            IsSample = true,
            Latitude = latitude, Longitude = longitude, Locality = locality, PlaceType = kind,
            Address = complete || id % 4 != 0 ? $"{id % 500 + 1}, Sector {id % 96 + 1}, {locality}, Delhi NCR" : null,
            Phone = complete || id % 5 == 0 ? Phone(id) : null,
            Email = complete || id % 7 == 0 ? $"reservations@stay{id + 1}.example" : null,
            Website = complete || id % 3 == 0 ? $"https://stay{id + 1}.example" : null
        };
    }

    public static void FillManual(DemoBusiness business)
    {
        var seed = (int)(uint.Parse(business.Key[^8..], System.Globalization.NumberStyles.HexNumber) % 1_000_000);
        business.Address ??= $"{seed % 500 + 1}, Sector {seed % 96 + 1}, {business.Locality}";
        business.Phone ??= Phone(seed);
        business.Email ??= $"reservations@place{seed}.example";
        business.Website ??= $"https://place{seed}.example";
    }

    // Deliberately non-dialable numbers and reserved domains prevent accidental outreach.
    private static string Phone(int id) => $"+91 00000 {id % 100_000:00000}";
    private static double Distance(double lat1, double lon1, double lat2, double lon2)
    {
        var dLat = (lat2 - lat1) * Math.PI / 180;
        var dLon = (lon2 - lon1) * Math.PI / 180;
        var a = Math.Pow(Math.Sin(dLat / 2), 2) + Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) * Math.Pow(Math.Sin(dLon / 2), 2);
        return 6_371_000 * 2 * Math.Asin(Math.Sqrt(Math.Clamp(a, 0, 1)));
    }
}
public sealed record DemoCandidate(DemoBusiness Business, string ObservationId);
