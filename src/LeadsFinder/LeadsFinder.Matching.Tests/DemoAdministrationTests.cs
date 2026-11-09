using System.Text.Json;
using LeadsFinder.Api;
using Microsoft.Extensions.Configuration;

namespace LeadsFinder.Matching.Tests;

public sealed class DemoAdministrationTests : IDisposable
{
    private const string Password = "Administration-test!2026";
    private readonly string _directory = Path.Combine(AppContext.BaseDirectory, "TestResults", "administration-" + Guid.NewGuid().ToString("N"));
    private readonly TestClock _clock = new(new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero));
    private readonly IConfiguration _configuration;
    private readonly DemoStore _store;
    private readonly Guid _admin;

    public DemoAdministrationTests()
    {
        _configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Demo:AdminEmail"] = "admin@example.test", ["Demo:AdminPassword"] = Password
        }).Build();
        _store = new DemoStore(_directory, new DemoCatalog(1_000_000), _configuration, _clock);
        _admin = _store.Login(new("admin@example.test", Password)).Id;
    }

    [Fact]
    public void ExactTrialBoundaryStopsAccessAndPausesThenRestoresJobs()
    {
        var user = Register("trial");
        var job = _store.CreateJob(user.Id, Search(50));
        _store.Work();
        Assert.InRange(_store.Job(user.Id, job.Id).FoundCount, 1, 49);
        _clock.Advance(TimeSpan.FromDays(3) - TimeSpan.FromTicks(1));
        Assert.Equal("trial", _store.User(user.Id).Status);
        _clock.Advance(TimeSpan.FromTicks(1));
        _store.Work();
        Assert.Equal("account_locked", Assert.Throws<DemoRequestException>(() => _store.User(user.Id)).Code);
        Assert.Equal("locked", _store.AdminUsers(_admin).Items.Single(x => x.Id == user.Id).Status);
        _store.UpdateUser(_admin, user.Id, new(true, false, 1000, 1000));
        Drain(job.Id, user.Id, "quarantined");
        Assert.Equal(50, _store.Job(user.Id, job.Id).FoundCount);
        _store.Enrich(user.Id, job.Id);
        _store.UpdateUser(_admin, user.Id, new(true, true, 1000, 1000));
        _store.Work();
        Assert.Equal(0, _store.AdminUsers(_admin).Items.Single(x => x.Id == user.Id).CleanCount);
        Assert.Equal("account_blocked", Assert.Throws<DemoRequestException>(() => _store.Dashboard(user.Id)).Code);
        _store.UpdateUser(_admin, user.Id, new(true, false, 1000, 1000));
        Drain(job.Id, user.Id, "completed");
        _clock.Advance(TimeSpan.FromDays(30));
        _store.Work();
        Assert.Equal("active", _store.User(user.Id).Status);
        Assert.Null(_store.User(user.Id).DeletionAtUtc);
        Assert.Equal("admin", _store.User(_admin).Role);
    }

    [Fact]
    public void TenDayRetentionDeletesAccountJobsAndLinksButKeepsSharedPlaces()
    {
        var expires = Register("expires");
        var kept = Register("kept");
        _store.UpdateUser(_admin, kept.Id, new(true, false, 1000, 1000));
        var place = new DemoCreateRecordRequest("Shared Hotel", null, 28.61, 77.21, null, null, null, null);
        _store.CreateRecord(expires.Id, place);
        var shared = _store.CreateRecord(kept.Id, place);
        _store.Enrich(kept.Id, shared.JobId);
        Drain(shared.JobId, kept.Id, "completed");
        _clock.Advance(TimeSpan.FromDays(10) - TimeSpan.FromTicks(1));
        _store.Work();
        Assert.Contains(_store.AdminUsers(_admin).Items, x => x.Id == expires.Id);
        _clock.Advance(TimeSpan.FromTicks(1));
        _store.Work();
        Assert.DoesNotContain(_store.AdminUsers(_admin).Items, x => x.Id == expires.Id);
        Assert.Equal(401, Assert.Throws<DemoRequestException>(() => _store.User(expires.Id)).StatusCode);
        Assert.Equal(1, _store.AdminOverview(_admin).GlobalRecords);
        Assert.Equal(1, Rows(kept.Id, "clean").Total);
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(_directory, "database.json")));
        Assert.DoesNotContain(json.RootElement.GetProperty("records").EnumerateArray(), x => x.GetProperty("userId").GetGuid() == expires.Id);
        Assert.DoesNotContain(json.RootElement.GetProperty("jobs").EnumerateArray(), x => x.GetProperty("userId").GetGuid() == expires.Id);
    }

    [Fact]
    public void QuarantineReservationsAndCleanReservationsPreventConcurrentQuotaBypass()
    {
        var user = Register("quota");
        _store.UpdateUser(_admin, user.Id, new(false, false, 40, 35));
        var first = _store.CreateJob(user.Id, Search(20));
        var second = _store.CreateJob(user.Id, Search(20));
        Assert.Equal("quarantine_limit", Assert.Throws<DemoRequestException>(() => _store.CreateJob(user.Id, Search(1))).Code);
        Drain(first.Id, user.Id, "quarantined");
        Drain(second.Id, user.Id, "quarantined");
        _store.Enrich(user.Id, first.Id);
        Assert.Equal("clean_limit", Assert.Throws<DemoRequestException>(() => _store.Enrich(user.Id, second.Id)).Code);
        Drain(first.Id, user.Id, "completed");
        _store.UpdateUser(_admin, user.Id, new(false, false, 40, 40));
        _store.Enrich(user.Id, second.Id);
        _store.UpdateUser(_admin, user.Id, new(false, false, 40, 20));
        _store.Work();
        Assert.True(_store.User(user.Id).CleanCount <= 20);
        Assert.Equal("quarantined", _store.Job(user.Id, second.Id).Status);
        Assert.True(Rows(user.Id, "quarantine").Total > 0);
        _store.UpdateUser(_admin, user.Id, new(false, false, 40, 40));
        _store.Enrich(user.Id, second.Id);
        Drain(second.Id, user.Id, "completed");
        Assert.Equal(0, Rows(user.Id, "quarantine").Total);
        Assert.Equal(40, _store.Job(user.Id, first.Id).FoundCount + _store.Job(user.Id, second.Id).FoundCount);
    }

    [Fact]
    public void AdministratorCannotBeBlockedOrLoseApprovalAndPublicSignupHasNoPrivileges()
    {
        var user = Register("ordinary");
        Assert.Equal("user", user.Role);
        Assert.Equal(403, Assert.Throws<DemoRequestException>(() => _store.AdminUsers(user.Id)).StatusCode);
        Assert.Equal(403, Assert.Throws<DemoRequestException>(() => _store.UpdateUser(user.Id, user.Id, new(true, false, 1000, 1000))).StatusCode);
        Assert.Equal(409, Assert.Throws<DemoRequestException>(() => _store.UpdateUser(_admin, _admin, new(false, true, 1000, 1000))).StatusCode);
        Assert.Equal("admin", _store.User(_admin).Role);
        Assert.Equal(400, Assert.Throws<DemoRequestException>(() => _store.UpdateUser(_admin, user.Id, new(false, false, -1, 1000))).StatusCode);
    }

    [Fact]
    public void MillionRowBatchStaysVirtualAndFetchesOnlyInsideSelectedCircle()
    {
        var user = Register("generated");
        var batch = _store.GenerateRows(_admin, new(89.95, 179.99, 20_000, 1_000_000));
        Assert.Equal(1_000_000, _store.AdminOverview(_admin).GeneratedRows);
        Assert.Equal(0, _store.AdminOverview(_admin).GlobalRecords);
        Assert.True(new FileInfo(Path.Combine(_directory, "database.json")).Length < 20_000);
        var job = _store.CreateJob(user.Id, new(batch.Latitude, batch.Longitude, batch.RadiusMeters, 100, "hotel"));
        Drain(job.Id, user.Id, "quarantined");
        var rows = Rows(user.Id, "quarantine");
        Assert.Equal(100, rows.Total);
        Assert.All(rows.Items, row =>
        {
            Assert.Equal(2, row.MissingFields.Length);
            Assert.InRange(Distance(batch.Latitude, batch.Longitude, row.Latitude, row.Longitude), 0, batch.RadiusMeters + .01);
        });
        _store.Enrich(user.Id, job.Id);
        Drain(job.Id, user.Id, "completed");
        Assert.All(Rows(user.Id, "clean").Items, row => Assert.Empty(row.MissingFields));
    }

    [Fact]
    public void FarAwayMillionRowBatchesAreSkippedWithoutStarvingSearch()
    {
        var user = Register("spatial");
        for (var index = 0; index < 10; index++) _store.GenerateRows(_admin, new(-40, -130, 50_000, 1_000_000));
        Assert.Equal(409, Assert.Throws<DemoRequestException>(() => _store.GenerateRows(_admin, new(28.61, 77.21, 1000, 1))).StatusCode);
        var job = _store.CreateJob(user.Id, Search(10));
        _store.Work();
        Assert.True(_store.Job(user.Id, job.Id).FoundCount > 0);
        Drain(job.Id, user.Id, "quarantined");
        Assert.Equal("quarantined", _store.Job(user.Id, job.Id).Status);
        Assert.Equal(10, _store.Job(user.Id, job.Id).FoundCount);
    }

    [Fact]
    public void ExistingAccountPromotionRequiresItsPasswordAndFailedBootstrapReleasesLock()
    {
        var user = Register("promote");
        _store.Dispose();
        IConfiguration Settings(string password) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Demo:AdminEmail"] = user.Email, ["Demo:AdminPassword"] = password
        }).Build();
        Assert.Throws<InvalidOperationException>(() => new DemoStore(_directory, new DemoCatalog(1000), Settings("Another-password!2026"), _clock));
        using var reopened = new DemoStore(_directory, new DemoCatalog(1000), Settings(Password), _clock);
        Assert.Equal("admin", reopened.Login(new(user.Email, Password)).Role);
    }

    [Fact]
    public void ExistingUsersWithoutLifecycleMetadataReceiveFreshTrialOnMigration()
    {
        var user = Register("legacy");
        _store.Dispose();
        var filename = Path.Combine(_directory, "database.json");
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(filename))!;
        var legacy = json["users"]!.AsArray().Single(item => item!["id"]!.GetValue<Guid>() == user.Id)!.AsObject();
        legacy.Remove("createdAtUtc");
        legacy.Remove("role");
        legacy.Remove("approved");
        _clock.Advance(TimeSpan.FromDays(30));
        File.WriteAllText(filename, json.ToJsonString());
        using var reopened = new DemoStore(_directory, new DemoCatalog(1000), _configuration, _clock);
        var migrated = reopened.User(user.Id);
        Assert.Equal(_clock.GetUtcNow().UtcDateTime, migrated.CreatedAtUtc);
        Assert.Equal("trial", migrated.Status);
        Assert.Equal("user", migrated.Role);
    }

    private DemoUserResponse Register(string name) => _store.Register(new(name, name + "@example.test", Password));
    private static DemoCreateJobRequest Search(int count) => new(28.6139, 77.209, 5000, count, "any_lodging");
    private DemoPage<DemoLeadResponse> Rows(Guid user, string status) => _store.Records(user, status, null, null, null, false, 1, 200);
    private void Drain(Guid job, Guid user, string target)
    {
        for (var index = 0; index < 100 && _store.Job(user, job).Status != target; index++) _store.Work();
        Assert.Equal(target, _store.Job(user, job).Status);
    }
    private static double Distance(double latitude1, double longitude1, double latitude2, double longitude2)
    {
        var lat1 = latitude1 * Math.PI / 180;
        var lat2 = latitude2 * Math.PI / 180;
        var dLon = (longitude2 - longitude1) * Math.PI / 180;
        var a = Math.Pow(Math.Sin((lat2 - lat1) / 2), 2) + Math.Cos(lat1) * Math.Cos(lat2) * Math.Pow(Math.Sin(dLon / 2), 2);
        return 12_742_000 * Math.Asin(Math.Sqrt(Math.Clamp(a, 0, 1)));
    }
    public void Dispose()
    {
        _store.Dispose();
        Directory.Delete(_directory, recursive: true);
    }
    private sealed class TestClock(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset _now = initial;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
