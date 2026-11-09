using System.Security.Cryptography;

namespace LeadsFinder.Api;

public sealed partial class DemoStore
{
    public DemoUser RequireAdmin(Guid userId)
    {
        lock (_gate)
        {
            var user = ActiveUser(userId);
            if (user.Role != "admin") throw new DemoRequestException("Administrator access is required.", 403);
            return user;
        }
    }

    public DemoAdminOverviewResponse AdminOverview(Guid adminId)
    {
        lock (_gate)
        {
            RequireAdmin(adminId);
            return new DemoAdminOverviewResponse(_database.Users.Count, _database.Users.Count(x => x.Approved),
                _database.Users.Count(x => x.Blocked), _database.Users.Count(x => AccountStatus(x) == "locked"),
                _database.Jobs.Count, _database.Businesses.Count, checked(_catalog.Size + GeneratedRowsCount()),
                GeneratedRowsCount(), GenerationBatchResponses());
        }
    }

    public DemoPage<DemoAdminUserResponse> AdminUsers(Guid adminId, string? q = null, int? page = null, int? pageSize = null)
    {
        lock (_gate)
        {
            RequireAdmin(adminId);
            var query = q?.Trim() ?? "";
            if (query.Length > 200) throw new DemoRequestException("Search text must be 200 characters or fewer.");
            return Page(_database.Users.Where(x => query.Length == 0 || x.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || x.Email.Contains(query, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.CreatedAtUtc).ThenBy(x => x.Id).Select(AdminUserResponse), page, pageSize);
        }
    }

    public DemoAdminUserResponse UpdateUser(Guid adminId, Guid userId, DemoUpdateUserRequest request)
    {
        if (!request.Approved.HasValue || !request.Blocked.HasValue || !request.QuarantineLimit.HasValue || !request.CleanLimit.HasValue)
            throw new DemoRequestException("Supply approval, blocked status, quarantine limit, and clean limit.");
        if (request.QuarantineLimit is < 0 or > 10_000_000 || request.CleanLimit is < 0 or > 10_000_000)
            throw new DemoRequestException("Record limits must be between 0 and 10,000,000.");
        lock (_gate)
        {
            RequireAdmin(adminId);
            var user = _database.Users.FirstOrDefault(x => x.Id == userId) ?? throw new DemoRequestException("User not found.", 404);
            if (user.Role == "admin" && (request.Blocked.Value || !request.Approved.Value))
                throw new DemoRequestException("Administrator accounts must remain approved and unblocked.", 409);
            user.Approved = request.Approved.Value;
            user.Blocked = request.Blocked.Value;
            user.QuarantineLimit = request.QuarantineLimit.Value;
            user.CleanLimit = request.CleanLimit.Value;
            // Removing approval from an old account can make it immediately eligible
            // for retention. Return its final metadata while removing all personal links.
            var response = AdminUserResponse(user);
            ApplyLifecycle();
            Save();
            return response;
        }
    }

    private DemoUser ActiveUser(Guid userId)
    {
        SweepLifecycle();
        var user = _database.Users.FirstOrDefault(x => x.Id == userId)
            ?? throw new DemoRequestException("Sign in to continue.", 401);
        var status = AccountStatus(user);
        if (status == "blocked") throw new DemoRequestException("Your account has been blocked. Contact an administrator.", 403, "account_blocked");
        if (status == "locked") throw new DemoRequestException("Your trial has ended. An administrator must approve your account to continue.", 403, "account_locked");
        return user;
    }

    private string AccountStatus(DemoUser user)
    {
        if (user.Blocked) return "blocked";
        if (user.Role == "admin" || user.Approved) return "active";
        return Now >= user.CreatedAtUtc.AddDays(3) ? "locked" : "trial";
    }

    private DemoUserResponse UserResponse(DemoUser user) => new(user.Id, user.Name, user.Email, user.Role,
        user.Approved, user.Blocked, user.CreatedAtUtc, user.LastLoginAtUtc,
        user.Role == "admin" || user.Approved ? null : user.CreatedAtUtc.AddDays(3),
        user.Role == "admin" || user.Approved ? null : user.CreatedAtUtc.AddDays(10),
        user.QuarantineLimit, user.CleanLimit, QuarantineCount(user.Id), CleanKeys(user.Id).Count, AccountStatus(user));
    private DemoAdminUserResponse AdminUserResponse(DemoUser user) => new(UserResponse(user), _database.Jobs.Count(x => x.UserId == user.Id));

    private bool MigrateUsers()
    {
        var changed = false;
        foreach (var user in _database.Users)
        {
            if (user.CreatedAtUtc == default) { user.CreatedAtUtc = Now; changed = true; }
            if (user.Role is not ("admin" or "user")) { user.Role = "user"; changed = true; }
        }
        // Upgrade generated display names without changing identities or user-entered businesses.
        foreach (var business in _database.Businesses.Values.Where(x => x.CatalogId.HasValue))
        {
            var name = _catalog.Lookup(business.CatalogId!.Value, complete: false).Name;
            if (business.Name != name) { business.Name = name; changed = true; }
        }
        return changed;
    }

    private bool BootstrapAdmin(IConfiguration? configuration)
    {
        var email = NormalizeEmail(configuration?["Demo:AdminEmail"]);
        var password = configuration?["Demo:AdminPassword"];
        if (email.Length == 0 && string.IsNullOrEmpty(password)) return false;
        if (!ValidEmail(email) || password is null || password.Length is < 8 or > 128)
            throw new InvalidOperationException("Configure Demo:AdminEmail and a Demo:AdminPassword between 8 and 128 characters.");
        var existing = _database.Users.FirstOrDefault(x => x.Email == email);
        if (existing is not null)
        {
            if (existing.Role != "admin" && !CryptographicOperations.FixedTimeEquals(
                HashPassword(password, Convert.FromBase64String(existing.PasswordSalt)), Convert.FromBase64String(existing.PasswordHash)))
                throw new InvalidOperationException("The configured administrator email belongs to an existing account with a different password. Supply that account's current password to authorize promotion.");
            var changed = existing.Role != "admin" || !existing.Approved || existing.Blocked;
            existing.Role = "admin"; existing.Approved = true; existing.Blocked = false;
            // Configuration promotes this explicit account; it never resets a stored password.
            return changed;
        }
        var name = configuration?["Demo:AdminName"]?.Trim();
        if (string.IsNullOrEmpty(name)) name = "Administrator";
        if (name.Length > 100) throw new InvalidOperationException("Demo:AdminName must be 100 characters or fewer.");
        var salt = RandomNumberGenerator.GetBytes(16);
        _database.Users.Add(new DemoUser { Name = name, Email = email, Role = "admin", Approved = true,
            CreatedAtUtc = Now, PasswordSalt = Convert.ToBase64String(salt), PasswordHash = Convert.ToBase64String(HashPassword(password, salt)) });
        return true;
    }

    private void SweepLifecycle()
    {
        if (ApplyLifecycle()) Save();
    }

    private bool ApplyLifecycle()
    {
        var changed = false;
        var expired = _database.Users.Where(x => x.Role != "admin" && !x.Approved && Now >= x.CreatedAtUtc.AddDays(10))
            .Select(x => x.Id).ToHashSet();
        if (expired.Count > 0)
        {
            _database.Users.RemoveAll(x => expired.Contains(x.Id));
            _database.Jobs.RemoveAll(x => expired.Contains(x.UserId));
            _database.Records.RemoveAll(x => expired.Contains(x.UserId));
            _database.SalesProfiles.RemoveAll(x => expired.Contains(x.UserId));
            _database.FieldReviews.RemoveAll(x => expired.Contains(x.UserId));
            changed = true;
        }
        foreach (var job in _database.Jobs.Where(x => x.Status is "fetching" or "enriching" || x.PausedStatus is not null))
        {
            var user = _database.Users.FirstOrDefault(x => x.Id == job.UserId);
            var restricted = user is null || AccountStatus(user) is "locked" or "blocked";
            if (restricted && job.PausedStatus is null)
            {
                job.PausedStatus = job.Status;
                job.Status = "quarantined";
                job.LastError = "This job is paused until an administrator restores account access.";
                changed = true;
            }
            else if (!restricted && job.PausedStatus is not null)
            {
                job.Status = job.PausedStatus;
                job.PausedStatus = null;
                job.LastError = null;
                changed = true;
            }
        }
        return changed;
    }

    private int QuarantineCount(Guid userId) => _database.Records.Count(x => x.UserId == userId && x.Status == "quarantine");
    private HashSet<string> CleanKeys(Guid userId) => _database.Records.Where(x => x.UserId == userId && x.Status == "clean")
        .Select(x => x.BusinessKey).ToHashSet();
    private void RequireQuarantineCapacity(DemoUser user, int additional)
    {
        var reserved = _database.Jobs.Where(x => x.UserId == user.Id && (x.Status == "fetching" || x.PausedStatus == "fetching"))
            .Sum(x => (long)Math.Max(0, x.TargetCount - x.FoundCount));
        if ((long)QuarantineCount(user.Id) + reserved + additional > user.QuarantineLimit)
            throw new DemoRequestException("Your quarantine record limit would be exceeded. Enrich existing rows or ask an administrator to increase your limit.", 409, "quarantine_limit");
    }
    private void RequireCleanCapacity(DemoUser user, DemoJob job)
    {
        var clean = CleanKeys(user.Id);
        var destinations = _database.Records.Where(x => x.JobId == job.Id && x.Status == "quarantine").Select(x => x.BusinessKey).ToHashSet();
        if (!destinations.Except(clean).Any()) return;
        var activeJobs = _database.Jobs.Where(x => x.UserId == user.Id && (x.Status == "enriching" || x.PausedStatus == "enriching"))
            .Select(x => x.Id).ToHashSet();
        clean.UnionWith(_database.Records.Where(x => x.UserId == user.Id && activeJobs.Contains(x.JobId)).Select(x => x.BusinessKey));
        clean.UnionWith(destinations);
        if (clean.Count > user.CleanLimit)
            throw new DemoRequestException("Your clean record limit would be exceeded. Ask an administrator to increase your limit before enriching this job.", 409, "clean_limit");
    }
}
