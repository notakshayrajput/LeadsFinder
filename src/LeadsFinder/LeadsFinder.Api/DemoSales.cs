using System.Text;

namespace LeadsFinder.Api;

public sealed partial class DemoStore
{
    private static readonly string[] SalesStages = ["new", "in_progress", "possible_sale", "won", "lost"];
    private static readonly string[] ReviewFields = ["address", "phone", "email", "website"];

    private DemoUserRecord OwnedRecord(Guid userId, Guid recordId) =>
        _database.Records.FirstOrDefault(x => x.Id == recordId && x.UserId == userId)
        ?? throw new DemoRequestException("Record not found.", 404);

    private DemoSalesProfile? SalesProfile(Guid userId, string businessKey) =>
        _database.SalesProfiles.FirstOrDefault(x => x.UserId == userId && x.BusinessKey == businessKey);

    private DemoSalesResponse SalesResponse(Guid userId, string businessKey)
    {
        var sales = SalesProfile(userId, businessKey);
        return sales is null ? new("new", false, false, false, false, "", null)
            : new(sales.Stage, sales.ContactedOnEmail, sales.ContactedOnPhone, sales.ContactedOnWhatsapp,
                sales.DoNotContact, sales.Notes, sales.UpdatedAtUtc);
    }

    public DemoLeadResponse UpdateSales(Guid userId, Guid recordId, DemoUpdateSalesRequest request)
    {
        if (request.Stage is not null && !SalesStages.Contains(request.Stage))
            throw new DemoRequestException("Choose a supported sales stage.");
        if (request.Notes?.Length > 4000) throw new DemoRequestException("Notes must be 4,000 characters or fewer.");
        lock (_gate)
        {
            ActiveUser(userId);
            var row = OwnedRecord(userId, recordId);
            var sales = SalesProfile(userId, row.BusinessKey);
            if (sales is null)
            {
                sales = new DemoSalesProfile { UserId = userId, BusinessKey = row.BusinessKey };
                _database.SalesProfiles.Add(sales);
            }
            sales.Stage = request.Stage ?? sales.Stage;
            sales.ContactedOnEmail = request.ContactedOnEmail ?? sales.ContactedOnEmail;
            sales.ContactedOnPhone = request.ContactedOnPhone ?? sales.ContactedOnPhone;
            sales.ContactedOnWhatsapp = request.ContactedOnWhatsapp ?? sales.ContactedOnWhatsapp;
            sales.DoNotContact = request.DoNotContact ?? sales.DoNotContact;
            sales.Notes = request.Notes?.Trim() ?? sales.Notes;
            sales.UpdatedAtUtc = Now;
            Save();
            return LeadResponse(row);
        }
    }

    private static string? FieldValue(DemoBusiness business, string field) => field switch
    {
        "address" => business.Address, "phone" => business.Phone, "email" => business.Email,
        "website" => business.Website, _ => null
    };

    public DemoLeadResponse ReviewField(Guid userId, Guid recordId, DemoReviewFieldRequest request)
    {
        if (!ReviewFields.Contains(request.Field)) throw new DemoRequestException("Choose address, phone, email, or website.");
        if (request.Confidence is not (null or "" or "low" or "medium" or "high"))
            throw new DemoRequestException("Choose low, medium, high, or unrated confidence.");
        if (request.Reason?.Length > 500) throw new DemoRequestException("Report notes must be 500 characters or fewer.");
        lock (_gate)
        {
            ActiveUser(userId);
            var row = OwnedRecord(userId, recordId);
            var value = FieldValue(_database.Businesses[row.BusinessKey], request.Field!);
            if (string.IsNullOrWhiteSpace(value)) throw new DemoRequestException("An empty field cannot be rated.");
            if (!string.Equals(value, request.Value, StringComparison.Ordinal))
                throw new DemoRequestException("This field changed. Refresh the record before reviewing it.", 409);
            var review = _database.FieldReviews.FirstOrDefault(x => x.UserId == userId && x.BusinessKey == row.BusinessKey
                && x.Field == request.Field && x.Value == value);
            if (review is null)
            {
                review = new DemoFieldReview { UserId = userId, BusinessKey = row.BusinessKey, Field = request.Field!, Value = value };
                _database.FieldReviews.Add(review);
            }
            if (request.Confidence is not null) review.Confidence = request.Confidence.Length == 0 ? null : request.Confidence;
            if (request.Inaccurate.HasValue) review.Inaccurate = request.Inaccurate.Value;
            if (request.Reason is not null) review.Reason = request.Reason.Trim();
            review.UpdatedAtUtc = Now;
            Save();
            return LeadResponse(row);
        }
    }

    private Dictionary<string, DemoFieldQuality> QualityResponse(Guid userId, DemoBusiness business)
    {
        return ReviewFields.ToDictionary(field => field, field =>
        {
            var value = FieldValue(business, field);
            var reviews = _database.FieldReviews.Where(x => x.BusinessKey == business.Key && x.Field == field && x.Value == value).ToArray();
            var mine = reviews.FirstOrDefault(x => x.UserId == userId);
            var reports = reviews.Where(x => x.Inaccurate).Select(x => x.UserId).Distinct().Count();
            // This is a community warning, not a probability of factual correctness.
            return new DemoFieldQuality(mine?.Confidence, reports, mine?.Inaccurate == true,
                reports >= 3 ? "low" : reports > 0 ? "reported" : "unrated");
        });
    }

    private IEnumerable<DemoUserRecord> FilterRecords(Guid userId, string? status, Guid? jobId, string? query,
        string? contact, bool hasContact, string? salesStage, string? dnd)
    {
        if (status is not (null or "all" or "quarantine" or "clean")) throw new DemoRequestException("Choose quarantine, clean, or all records.");
        if (contact is not (null or "any" or "phone" or "email")) throw new DemoRequestException("Choose a supported contact filter.");
        if (salesStage is not (null or "" or "all" or "uncontacted" or "contacted") && !SalesStages.Contains(salesStage))
            throw new DemoRequestException("Choose a supported sales stage.");
        if (dnd is not (null or "all" or "exclude" or "only")) throw new DemoRequestException("Choose a supported DND filter.");
        if (jobId.HasValue) OwnedJob(userId, jobId.Value);
        var text = query?.Trim() ?? "";
        if (text.Length > 200) throw new DemoRequestException("Search text must be 200 characters or fewer.");
        return _database.Records.Where(row =>
        {
            if (row.UserId != userId || jobId.HasValue && row.JobId != jobId.Value || status is not (null or "all") && row.Status != status) return false;
            var b = _database.Businesses[row.BusinessKey];
            if (text.Length > 0 && !new[] { b.Name, b.Address, b.Phone, b.Email, b.Website, b.Locality }
                .Any(value => value?.Contains(text, StringComparison.OrdinalIgnoreCase) == true)) return false;
            if (hasContact && !HasContact(b) || contact == "phone" && string.IsNullOrWhiteSpace(b.Phone)
                || contact == "email" && string.IsNullOrWhiteSpace(b.Email)) return false;
            var sales = SalesResponse(userId, b.Key);
            if (dnd == "exclude" && sales.DoNotContact || dnd == "only" && !sales.DoNotContact) return false;
            var contacted = sales.ContactedOnEmail || sales.ContactedOnPhone || sales.ContactedOnWhatsapp;
            return salesStage switch
            {
                null or "" or "all" => true, "uncontacted" => !contacted, "contacted" => contacted, _ => sales.Stage == salesStage
            };
        }).OrderByDescending(x => x.FoundAtUtc).ThenBy(x => x.Id);
    }

    public string ExportSales(Guid userId, string? status, Guid? jobId, string? query, string? contact,
        bool hasContact, string? salesStage, string? dnd, bool includeSamples = false)
    {
        lock (_gate)
        {
            ActiveUser(userId);
            var rows = FilterRecords(userId, status, jobId, query, contact, hasContact, salesStage, dnd)
                .DistinctBy(x => x.BusinessKey).Where(x => includeSamples || !IsSample(_database.Businesses[x.BusinessKey])).ToArray();
            if (rows.Length == 0) throw new DemoRequestException("No records match this export. Enable sample records to export demo data.");
            if (rows.Length > 100_000) throw new DemoRequestException("Narrow the filters to export at most 100,000 businesses at a time.");
            var output = new StringBuilder();
            output.AppendLine("Topic,Company Name,Last Name,Business Phone,Email,Website,Address 1: Street 1,Address 1: City,Description,Do not allow Emails,Do not allow Phone Calls");
            foreach (var row in rows)
            {
                var b = _database.Businesses[row.BusinessKey];
                var sales = SalesResponse(userId, b.Key);
                // Dynamics requires Last Name. Organization prospects use the business
                // name as their record name; no individual contact identity is invented.
                var description = $"Organization prospect; no named contact supplied. Source: {(IsSample(b) ? "sample catalog" : "manual entry")}. Stage: {sales.Stage}. "
                    + $"Contacted: email={sales.ContactedOnEmail}, phone={sales.ContactedOnPhone}, WhatsApp={sales.ContactedOnWhatsapp}. {sales.Notes}";
                var cells = new[] { $"Sales enquiry — {b.Name}", b.Name, b.Name, new string((b.Phone ?? "").Where(char.IsDigit).ToArray()),
                    b.Email ?? "", b.Website ?? "", b.Address ?? "", b.Locality, description,
                    sales.DoNotContact ? "Yes" : "No", sales.DoNotContact ? "Yes" : "No" };
                output.AppendLine(string.Join(',', cells.Select(CsvCell)));
            }
            return output.ToString();
        }
    }

    private static string CsvCell(string value)
    {
        if (value.TrimStart().StartsWith('=') || value.TrimStart().StartsWith('+') || value.TrimStart().StartsWith('-')
            || value.TrimStart().StartsWith('@') || value.StartsWith('\t') || value.StartsWith('\r')) value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private static bool IsSample(DemoBusiness b) => b.IsSample || b.CatalogId.HasValue || b.Key.StartsWith("generated:", StringComparison.Ordinal)
        || b.Email?.EndsWith(".example", StringComparison.OrdinalIgnoreCase) == true
        || Uri.TryCreate(b.Website, UriKind.Absolute, out var uri) && uri.Host.EndsWith(".example", StringComparison.OrdinalIgnoreCase);
}
