using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace LeadsFinder.Matching;

public static class PlaceMatcher
{
    public const string RuleVersion = "lodging-v1";
    private static readonly HashSet<string> GenericNameTokens =
        ["hotel", "hotels", "the", "and", "by", "resort", "resorts", "guest", "house", "homestay", "inn", "lodge"];

    public static PairAssessment Compare(PlaceSnapshot left, PlaceSnapshot right)
    {
        var distance = DistanceMeters(left.Latitude, left.Longitude, right.Latitude, right.Longitude);
        var leftName = NameKey(left.Name);
        var rightName = NameKey(right.Name);
        var name = Similarity(leftName, rightName);
        double? address = string.IsNullOrWhiteSpace(left.Address) || string.IsNullOrWhiteSpace(right.Address)
            ? null : Similarity(AddressKey(left.Address), AddressKey(right.Address));
        var samePhone = PhoneKey(left.Phone) is { Length: >= 7 } phone && phone == PhoneKey(right.Phone);
        var sameWebsite = WebsiteKey(left.Website) is { Length: > 0 } host && host == WebsiteKey(right.Website);
        var conflict = DifferentPostalCodes(left.Address, right.Address)
            || DifferentLeadingNumbers(left.Address, right.Address);
        var reasons = new List<string>();
        var decision = "likely_distinct";

        if (left.Strategy == right.Strategy && left.SourceId == right.SourceId)
        {
            decision = "likely_duplicate";
            reasons.Add("Same provider and source identity.");
        }
        else if (name >= .92 && distance < 40)
        {
            decision = conflict ? "possible_duplicate" : "likely_duplicate";
            reasons.Add("Very similar distinctive names and less than 40 metres apart.");
        }
        else if ((name >= .65 && distance <= 100)
            || (name >= .45 && (samePhone || sameWebsite))
            || (name >= .85 && address >= .85))
        {
            decision = "possible_duplicate";
            reasons.Add("Overlapping identity signals need further comparison.");
        }
        else
            reasons.Add("Available identity signals do not support a duplicate match.");

        if (conflict) reasons.Add("Different postal codes or leading property numbers; inspect the addresses.");
        if (samePhone) reasons.Add("Same normalized phone; shared booking numbers are possible.");
        if (sameWebsite) reasons.Add("Same website host; several properties may share a domain.");
        if (address is null) reasons.Add("One or both addresses are missing.");
        if (leftName.Length == 0 || rightName.Length == 0)
            reasons.Add("A generic or placeholder name provides no distinctive name evidence.");

        return new(left, right, distance, name, address, samePhone, sameWebsite, conflict,
            decision, reasons.ToArray());
    }

    public static string Normalize(string? value)
    {
        var text = (value ?? "").ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var output = new StringBuilder();
        var latinBase = false;
        foreach (var c in text)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)
            {
                // Latin accents can be folded, but Indic vowel marks carry identity information.
                if (!latinBase) output.Append(c);
                continue;
            }
            latinBase = char.IsLetter(c) && c <= '\u024f';
            output.Append(char.IsLetterOrDigit(c) ? c : ' ');
        }
        return Regex.Replace(output.ToString(), @"\s+", " ").Trim();
    }

    public static string PhoneKey(string? value)
    {
        // Remove punctuation only. Country codes are retained to avoid conflating countries.
        return Regex.Replace(value ?? "", @"[^0-9]", "");
    }

    public static string WebsiteKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        if (!Uri.TryCreate(value.Contains("://") ? value : "https://" + value, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")) return "";
        return uri.IdnHost.ToLowerInvariant().TrimStartPrefix("www.");
    }

    private static string TrimStartPrefix(this string value, string prefix) =>
        value.StartsWith(prefix, StringComparison.Ordinal) ? value[prefix.Length..] : value;

    private static string NameKey(string name)
    {
        if (Regex.IsMatch(name, @"^(unnamed|unknown)\b", RegexOptions.IgnoreCase)) return "";
        return string.Join(' ', Normalize(name).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(x => !GenericNameTokens.Contains(x)).Order(StringComparer.Ordinal));
    }

    private static string AddressKey(string address) => Regex.Replace(Normalize(address),
        @"\b(rd|st|ave)\b", match => match.Value switch { "rd" => "road", "st" => "street", _ => "avenue" });

    private static bool DifferentPostalCodes(string left, string right)
    {
        // Initial lodging dataset is Indian; this only treats six-digit values as postal codes.
        var a = Regex.Match(left, @"\b[1-9][0-9]{5}\b").Value;
        var b = Regex.Match(right, @"\b[1-9][0-9]{5}\b").Value;
        return a.Length > 0 && b.Length > 0 && a != b;
    }

    private static bool DifferentLeadingNumbers(string left, string right)
    {
        var a = Regex.Match(left.Trim(), @"^\d+[a-z]?(?:/\d+[a-z]?)?\b", RegexOptions.IgnoreCase).Value;
        var b = Regex.Match(right.Trim(), @"^\d+[a-z]?(?:/\d+[a-z]?)?\b", RegexOptions.IgnoreCase).Value;
        return a.Length > 0 && b.Length > 0 && !a.Equals(b, StringComparison.OrdinalIgnoreCase);
    }

    private static double Similarity(string left, string right)
    {
        if (left.Length == 0 || right.Length == 0) return 0;
        if (left == right) return 1;
        var a = left.Split(' ').ToHashSet();
        var b = right.Split(' ').ToHashSet();
        var dice = 2.0 * a.Intersect(b).Count() / (a.Count + b.Count);
        var previous = Enumerable.Range(0, right.Length + 1).ToArray();
        var current = new int[right.Length + 1];
        for (var i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= right.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + (left[i - 1] == right[j - 1] ? 0 : 1));
            (previous, current) = (current, previous);
        }
        var edit = 1.0 - (double)previous[right.Length] / Math.Max(left.Length, right.Length);
        return .55 * edit + .45 * dice;
    }

    public static double DistanceMeters(double lat1, double lon1, double lat2, double lon2)
    {
        const double radians = Math.PI / 180;
        var lat = (lat2 - lat1) * radians;
        var lon = (lon2 - lon1) * radians;
        var a = Math.Pow(Math.Sin(lat / 2), 2) + Math.Cos(lat1 * radians) * Math.Cos(lat2 * radians)
            * Math.Pow(Math.Sin(lon / 2), 2);
        return 6_371_008.8 * 2 * Math.Asin(Math.Sqrt(Math.Clamp(a, 0, 1)));
    }
}
