using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LeadsFinder.Api;

/// <summary>
/// Single-instance durable local database. One canonical business is shared globally;
/// user observation links own quarantine/clean state and retain discovery history.
/// A process lock prevents two servers from writing the same snapshot concurrently.
/// </summary>
public sealed partial class DemoStore : IDisposable
{
    private readonly object _gate = new();
    private readonly DemoCatalog _catalog;
    private readonly string _path;
    private readonly FileStream _processLock;
    private readonly DemoDatabase _database;
    private readonly TimeProvider _clock;
    private DateTime Now => _clock.GetUtcNow().UtcDateTime;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const int PasswordIterations = 210_000;

    public DemoStore(string dataPath, DemoCatalog catalog, IConfiguration? configuration = null, TimeProvider? clock = null)
    {
        _catalog = catalog;
        _clock = clock ?? TimeProvider.System;
        Directory.CreateDirectory(dataPath);
        _path = Path.Combine(dataPath, "database.json");
        _processLock = new FileStream(Path.Combine(dataPath, "database.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            _database = File.Exists(_path)
                ? JsonSerializer.Deserialize<DemoDatabase>(File.ReadAllText(_path), Json) ?? throw new InvalidDataException("The stored database is empty.")
                : new DemoDatabase();
            if (_database.Version != 1) throw new InvalidDataException("Unsupported database version.");
            var changed = MigrateUsers();
            changed |= BootstrapAdmin(configuration);
            changed |= ApplyLifecycle();
            if (changed) Save();
        }
        catch
        {
            _processLock.Dispose();
            throw;
        }
    }

    public DemoUserResponse Register(DemoRegisterRequest request)
    {
        var name = request.Name?.Trim() ?? "";
        var email = NormalizeEmail(request.Email);
        if (name.Length is < 1 or > 100) throw new DemoRequestException("Enter a name between 1 and 100 characters.");
        if (!ValidEmail(email)) throw new DemoRequestException("Enter a valid email address.");
        if (request.Password is null || request.Password.Length is < 8 or > 128)
            throw new DemoRequestException("Use a password between 8 and 128 characters.");
        var salt = RandomNumberGenerator.GetBytes(16);
        var passwordHash = HashPassword(request.Password, salt);
        lock (_gate)
        {
            SweepLifecycle();
            if (_database.Users.Any(x => x.Email == email)) throw new DemoRequestException("An account already uses this email address.", 409);
            var user = new DemoUser { Name = name, Email = email, CreatedAtUtc = Now, LastLoginAtUtc = Now,
                PasswordSalt = Convert.ToBase64String(salt), PasswordHash = Convert.ToBase64String(passwordHash) };
            _database.Users.Add(user);
            Save();
            return UserResponse(user);
        }
    }

    public DemoUserResponse Login(DemoLoginRequest request)
    {
        var email = NormalizeEmail(request.Email);
        DemoUser? user;
        lock (_gate)
        {
            SweepLifecycle();
            user = _database.Users.FirstOrDefault(x => x.Email == email);
        }
        var salt = user is null ? new byte[16] : Convert.FromBase64String(user.PasswordSalt);
        var candidate = HashPassword((request.Password ?? "")[..Math.Min(request.Password?.Length ?? 0, 128)], salt);
        if (user is null || request.Password is null || request.Password.Length > 128
            || !CryptographicOperations.FixedTimeEquals(candidate, Convert.FromBase64String(user.PasswordHash)))
            throw new DemoRequestException("Email or password is incorrect.", 401);
        lock (_gate)
        {
            user = ActiveUser(user.Id);
            user.LastLoginAtUtc = Now;
            Save();
            return UserResponse(user);
        }
    }

    public DemoUserResponse User(Guid userId)
    {
        lock (_gate) return UserResponse(ActiveUser(userId));
    }

    public DemoDashboardResponse Dashboard(Guid userId)
    {
        lock (_gate)
        {
            ActiveUser(userId);
            var groups = _database.Records.Where(x => x.UserId == userId).GroupBy(x => x.BusinessKey).ToArray();
            var enriched = groups.Count(x => x.Any(r => r.Status == "clean"));
            var jobs = _database.Jobs.Where(x => x.UserId == userId).ToArray();
            return new DemoDashboardResponse(groups.Length, enriched, groups.Length - enriched,
                groups.Count(x => x.Any(r => r.Status == "clean") && HasContact(_database.Businesses[x.Key])),
                jobs.Length, jobs.Count(x => x.Status is "fetching" or "enriching"), checked(_catalog.Size + GeneratedRowsCount()),
                jobs.OrderByDescending(x => x.CreatedAtUtc).Take(6).Select(JobResponse).ToArray())
            {
                UncontactedLeads = groups.Count(x => HasContact(_database.Businesses[x.Key]) && !SalesResponse(userId, x.Key).DoNotContact
                    && !SalesResponse(userId, x.Key).ContactedOnEmail && !SalesResponse(userId, x.Key).ContactedOnPhone && !SalesResponse(userId, x.Key).ContactedOnWhatsapp),
                PossibleSales = groups.Count(x => SalesResponse(userId, x.Key).Stage == "possible_sale" && !SalesResponse(userId, x.Key).DoNotContact),
                ContactedLeads = groups.Count(x => SalesResponse(userId, x.Key) is var s && (s.ContactedOnEmail || s.ContactedOnPhone || s.ContactedOnWhatsapp)),
                DoNotContactLeads = groups.Count(x => SalesResponse(userId, x.Key).DoNotContact)
            };
        }
    }

    public DemoPage<DemoJobResponse> Jobs(Guid userId, int? page, int? pageSize)
    {
        lock (_gate)
        {
            ActiveUser(userId);
            return Page(_database.Jobs.Where(x => x.UserId == userId)
                .OrderByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.Id).Select(JobResponse), page, pageSize);
        }
    }

    public DemoJobResponse Job(Guid userId, Guid jobId)
    {
        lock (_gate)
        {
            ActiveUser(userId);
            return JobResponse(OwnedJob(userId, jobId));
        }
    }

    public DemoJobResponse CreateJob(Guid userId, DemoCreateJobRequest request)
    {
        ValidateCoordinates(request.Latitude, request.Longitude);
        if (request.RadiusMeters is < 100 or > 50_000) throw new DemoRequestException("Radius must be between 100 and 50,000 meters.");
        if (request.TargetCount is < 1 or > 200) throw new DemoRequestException("A job can fetch between 1 and 200 rows.");
        if (!DemoCatalog.PlaceTypes.Contains(request.PlaceType)) throw new DemoRequestException("Choose a supported lodging type.");
        lock (_gate)
        {
            var user = ActiveUser(userId);
            RequireQuarantineCapacity(user, request.TargetCount);
            if (_database.Jobs.Count(x => x.UserId == userId && x.Status is "fetching" or "enriching") >= 5)
                throw new DemoRequestException("Wait for an active job to finish before starting another.", 409);
            var number = _database.Jobs.Count(x => x.UserId == userId) + 1;
            var job = new DemoJob
            {
                UserId = userId, Name = $"Search {number:000}", Latitude = request.Latitude, Longitude = request.Longitude,
                RadiusMeters = request.RadiusMeters, TargetCount = request.TargetCount, PlaceType = request.PlaceType!, CreatedAtUtc = Now
            };
            _database.Jobs.Add(job);
            Save();
            return JobResponse(job);
        }
    }

    public DemoJobResponse Enrich(Guid userId, Guid jobId)
    {
        lock (_gate)
        {
            var user = ActiveUser(userId);
            var job = OwnedJob(userId, jobId);
            if (job.Status is "fetching") throw new DemoRequestException("Wait for fetching to finish before enriching this job.", 409);
            if (job.Status is "completed" or "enriching") return JobResponse(job);
            if (job.Status is "failed") throw new DemoRequestException("This job failed. Create a new search to continue.", 409);
            RequireCleanCapacity(user, job);
            job.Status = "enriching";
            job.ProcessedCount = job.FoundCount - _database.Records.Count(x => x.JobId == job.Id && x.Status == "quarantine");
            job.LastError = null;
            job.FinishedAtUtc = null;
            Save();
            return JobResponse(job);
        }
    }

    public DemoPage<DemoLeadResponse> Records(Guid userId, string? status, Guid? jobId, string? query,
        string? contact, bool hasContact, int? page, int? pageSize, string? salesStage = null, string? dnd = null)
    {
        lock (_gate)
        {
            ActiveUser(userId);
            var rows = Page(FilterRecords(userId, status, jobId, query, contact, hasContact, salesStage, dnd), page, pageSize);
            return new DemoPage<DemoLeadResponse>(rows.Items.Select(LeadResponse).ToArray(), rows.Total, rows.Page, rows.PageSize);
        }
    }

    public DemoLeadResponse CreateRecord(Guid userId, DemoCreateRecordRequest request)
    {
        ValidateCoordinates(request.Latitude, request.Longitude);
        var name = Clean(request.Name, 200);
        if (string.IsNullOrWhiteSpace(name)) throw new DemoRequestException("Enter a place name.");
        var address = Clean(request.Address, 500);
        var phone = Clean(request.Phone, 60);
        var email = Clean(request.Email, 254);
        var website = Clean(request.Website, 500);
        if (email is not null && !ValidEmail(email)) throw new DemoRequestException("Enter a valid contact email address.");
        if (website is not null && (!Uri.TryCreate(website, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")))
            throw new DemoRequestException("Use an http or https website address.");
        // Rounded position + normalized name identifies a manually created canonical place.
        var identity = FormattableString.Invariant($"{name.ToUpperInvariant()}|{request.Latitude:F4}|{request.Longitude:F4}");
        var key = "manual:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        lock (_gate)
        {
            var user = ActiveUser(userId);
            RequireQuarantineCapacity(user, 1);
            DemoJob? job = request.JobId.HasValue ? OwnedJob(userId, request.JobId.Value) : null;
            var matching = _database.Businesses.Values.FirstOrDefault(x => SamePlace(x, name, request.Latitude, request.Longitude));
            if (matching is not null) key = matching.Key;
            var existing = _database.Records.FirstOrDefault(x => x.UserId == userId && x.BusinessKey == key);
            if (existing is not null) throw new DemoRequestException("You already have this place in your records.", 409);
            if (job is not null && (job.Status != "quarantined" || job.FoundCount >= 200))
                throw new DemoRequestException("Add rows to a quarantined job with fewer than 200 rows.", 409);
            if (!_database.Businesses.TryGetValue(key, out var business))
            {
                business = new DemoBusiness { Key = key, Name = name, Address = address, Latitude = request.Latitude,
                    Longitude = request.Longitude, Phone = phone, Email = email, Website = website };
                _database.Businesses.Add(key, business);
            }
            else
            {
                business.Address ??= address; business.Phone ??= phone; business.Email ??= email; business.Website ??= website;
            }
            if (job is null)
            {
                job = new DemoJob { UserId = userId, Name = $"Entry {_database.Jobs.Count(x => x.UserId == userId) + 1:000}",
                    Status = "quarantined", Latitude = request.Latitude, Longitude = request.Longitude,
                    TargetCount = 1, PlaceType = "hotel", RadiusMeters = 100, CreatedAtUtc = Now };
                _database.Jobs.Add(job);
            }
            job.FoundCount++;
            job.TargetCount = Math.Max(job.TargetCount, job.FoundCount);
            var row = new DemoUserRecord { UserId = userId, JobId = job.Id, BusinessKey = key, ObservationId = key, FoundAtUtc = Now };
            _database.Records.Add(row);
            Save();
            return LeadResponse(row);
        }
    }

    public bool Work()
    {
        lock (_gate)
        {
            var changed = ApplyLifecycle();
            // Each job advances every tick so long scans cannot starve other users.
            var active = _database.Jobs.Where(x => x.Status is "fetching" or "enriching").ToArray();
            if (active.Length == 0)
            {
                if (changed) Save();
                return changed;
            }
            foreach (var job in active)
            {
                try
                {
                    if (job.Status == "fetching") Fetch(job);
                    else Fill(job);
                }
                catch (Exception error)
                {
                    job.Status = "failed";
                    job.LastError = "The job could not be completed. Start a new search to continue.";
                    job.FinishedAtUtc = Now;
                    System.Diagnostics.Trace.TraceError("Job {0}: {1}", job.Id, error);
                }
            }
            Save();
            return true;
        }
    }

    private void Fetch(DemoJob job)
    {
        var user = _database.Users.First(x => x.Id == job.UserId);
        var otherJobs = _database.Records.Where(x => x.UserId == job.UserId && x.JobId != job.Id)
            .Select(x => x.BusinessKey).ToHashSet();
        var observations = _database.Records.Where(x => x.JobId == job.Id).Select(x => x.ObservationId).ToHashSet();
        var added = 0;
        for (var scanned = 0; scanned < 10_000 && added < 5 && job.FoundCount < job.TargetCount; scanned++)
        {
            if (QuarantineCount(user.Id) >= user.QuarantineLimit)
            {
                job.Status = "quarantined";
                job.LastError = "Fetching stopped at your quarantine record limit. Enrich existing rows or ask an administrator to increase your limit.";
                break;
            }
            DemoCandidate? candidate;
            var exhausted = false;
            if (!TryNextGenerated(job, out candidate, out _)) candidate = _catalog.Next(job, out exhausted);
            if (exhausted)
            {
                job.Status = job.FoundCount == 0 ? "completed" : "quarantined";
                if (job.FoundCount == 0) job.FinishedAtUtc = Now;
                break;
            }
            if (candidate is null) continue;
            if (MergeManualPlace(candidate.Business))
                otherJobs = _database.Records.Where(x => x.UserId == job.UserId && x.JobId != job.Id).Select(x => x.BusinessKey).ToHashSet();
            if (otherJobs.Contains(candidate.Business.Key) || !observations.Add(candidate.ObservationId)) continue;
            _database.Businesses.TryAdd(candidate.Business.Key, candidate.Business);
            _database.Records.Add(new DemoUserRecord { UserId = job.UserId, JobId = job.Id,
                BusinessKey = candidate.Business.Key, ObservationId = candidate.ObservationId, FoundAtUtc = Now });
            job.FoundCount++;
            added++;
        }
        job.ProcessedCount = job.FoundCount;
        if (job.FoundCount == job.TargetCount) job.Status = "quarantined";
    }

    private void Fill(DemoJob job)
    {
        var user = _database.Users.First(x => x.Id == job.UserId);
        var cleanKeys = CleanKeys(user.Id);
        var rows = _database.Records.Where(x => x.JobId == job.Id && x.Status == "quarantine").Take(4).ToArray();
        foreach (var row in rows)
        {
            // Match canonical identity, rather than a fragile name string comparison.
            var clean = cleanKeys.Contains(row.BusinessKey);
            if (clean)
            {
                _database.Records.Remove(row);
                job.DuplicatesRemoved++;
            }
            else
            {
                if (cleanKeys.Count >= user.CleanLimit)
                {
                    job.Status = "quarantined";
                    job.LastError = "Enrichment paused at your clean record limit. Ask an administrator to increase your limit, then run enrichment again.";
                    return;
                }
                var business = _database.Businesses[row.BusinessKey];
                if (business.CatalogId.HasValue)
                {
                    var filled = _catalog.Lookup(business.CatalogId.Value, complete: true);
                    business.Address ??= filled.Address; business.Phone ??= filled.Phone;
                    business.Email ??= filled.Email; business.Website ??= filled.Website;
                }
                else DemoCatalog.FillManual(business);
                row.Status = "clean";
                row.EnrichedAtUtc = Now;
                cleanKeys.Add(row.BusinessKey);
            }
            job.ProcessedCount++;
        }
        if (!_database.Records.Any(x => x.JobId == job.Id && x.Status == "quarantine"))
        {
            job.Status = "completed";
            job.FinishedAtUtc = Now;
        }
    }

    private DemoJob OwnedJob(Guid userId, Guid jobId) => _database.Jobs.FirstOrDefault(x => x.Id == jobId && x.UserId == userId)
        ?? throw new DemoRequestException("Job not found.", 404);
    private bool MergeManualPlace(DemoBusiness catalogBusiness)
    {
        var matches = _database.Businesses.Values.Where(x => x.Key.StartsWith("manual:", StringComparison.Ordinal)
            && SamePlace(x, catalogBusiness.Name, catalogBusiness.Latitude, catalogBusiness.Longitude)).ToArray();
        if (matches.Length == 0) return false;
        if (!_database.Businesses.TryGetValue(catalogBusiness.Key, out var canonical))
        {
            canonical = catalogBusiness;
            _database.Businesses.Add(canonical.Key, canonical);
        }
        foreach (var manual in matches)
        {
            canonical.Address ??= manual.Address; canonical.Phone ??= manual.Phone;
            canonical.Email ??= manual.Email; canonical.Website ??= manual.Website;
            foreach (var link in _database.Records.Where(x => x.BusinessKey == manual.Key)) link.BusinessKey = canonical.Key;
            foreach (var profile in _database.SalesProfiles.Where(x => x.BusinessKey == manual.Key)) profile.BusinessKey = canonical.Key;
            foreach (var review in _database.FieldReviews.Where(x => x.BusinessKey == manual.Key)) review.BusinessKey = canonical.Key;
            _database.Businesses.Remove(manual.Key);
        }
        return true;
    }
    private static bool SamePlace(DemoBusiness existing, string name, double latitude, double longitude)
    {
        if (!existing.Name.Trim().Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
        var north = (existing.Latitude - latitude) * 111_320;
        var east = (existing.Longitude - longitude) * 97_700;
        return north * north + east * east <= 20 * 20;
    }
    private DemoJobResponse JobResponse(DemoJob job) => new(job.Id, job.Name, job.Status, job.PlaceType,
        job.Latitude, job.Longitude, job.RadiusMeters, job.TargetCount, job.FoundCount,
        _database.Records.Count(x => x.JobId == job.Id && x.Status == "quarantine"),
        _database.Records.Count(x => x.JobId == job.Id && x.Status == "clean"), job.DuplicatesRemoved,
        job.ProcessedCount, job.CreatedAtUtc, job.FinishedAtUtc, job.LastError);
    private DemoLeadResponse LeadResponse(DemoUserRecord row)
    {
        var b = _database.Businesses[row.BusinessKey];
        var missing = new[] { ("address", b.Address), ("phone", b.Phone), ("email", b.Email), ("website", b.Website) }
            .Where(x => string.IsNullOrWhiteSpace(x.Item2)).Select(x => x.Item1).ToArray();
        return new DemoLeadResponse(row.Id, b.Name, b.Address, b.Latitude, b.Longitude, b.Phone, b.Email,
            b.Website, b.Locality, b.PlaceType, row.Status, row.JobId, _database.Jobs.First(x => x.Id == row.JobId).Name,
            row.FoundAtUtc, row.EnrichedAtUtc, missing)
        {
            Sales = SalesResponse(row.UserId, b.Key), FieldQuality = QualityResponse(row.UserId, b), IsSample = IsSample(b)
        };
    }
    private static DemoPage<T> Page<T>(IEnumerable<T> query, int? page, int? pageSize)
    {
        var current = Math.Clamp(page ?? 1, 1, 1_000_000);
        var size = Math.Clamp(pageSize ?? 20, 1, 200);
        var all = query.ToArray();
        current = Math.Min(current, Math.Max(1, (int)Math.Ceiling(all.Length / (double)size)));
        return new DemoPage<T>(all.Skip((current - 1) * size).Take(size).ToArray(), all.Length, current, size);
    }
    private void Save()
    {
        var temporary = _path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, _database, Json);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, _path, overwrite: true);
    }
    private static bool HasContact(DemoBusiness business) => !string.IsNullOrWhiteSpace(business.Phone) || !string.IsNullOrWhiteSpace(business.Email);
    private static byte[] HashPassword(string password, byte[] salt) => Rfc2898DeriveBytes.Pbkdf2(password, salt, PasswordIterations, HashAlgorithmName.SHA256, 32);
    private static string NormalizeEmail(string? value) => value?.Trim().ToLowerInvariant() ?? "";
    private static bool ValidEmail(string email) => email.Length <= 254 && MailAddress.TryCreate(email, out var address) && address.Address == email;
    private static string? Clean(string? value, int maximum)
    {
        var result = value?.Trim();
        if (result?.Length > maximum) throw new DemoRequestException($"A value exceeds the maximum length of {maximum} characters.");
        return string.IsNullOrEmpty(result) ? null : result;
    }
    private static void ValidateCoordinates(double latitude, double longitude)
    {
        if (!double.IsFinite(latitude) || latitude is < -90 or > 90 || !double.IsFinite(longitude) || longitude is < -180 or > 180)
            throw new DemoRequestException("Choose valid coordinates on the map.");
    }
    public void Dispose() => _processLock.Dispose();
}
