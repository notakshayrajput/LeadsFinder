using System.Text.Json;
using System.Text.Json.Nodes;
using LeadsFinder.Api;

namespace LeadsFinder.Matching.Tests;

public sealed class DemoSalesTests : IDisposable
{
    private readonly string _directory = Path.Combine(AppContext.BaseDirectory, "TestResults", "sales-" + Guid.NewGuid().ToString("N"));
    private DemoStore _store;
    private readonly Guid _a;
    private readonly Guid _b;
    private const string Password = "Sales-workflow!2026";

    public DemoSalesTests()
    {
        _store = new DemoStore(_directory, new DemoCatalog(1000));
        _a = _store.Register(new("Alice", "alice@example.test", Password)).Id;
        _b = _store.Register(new("Bob", "bob@example.test", Password)).Id;
    }

    private DemoLeadResponse Add(Guid user, string name = "Sample business") => _store.CreateRecord(user,
        new(name, "12 Main Street", 28.61, 77.21, "+1 202 555 0144", "sales@example.test", "https://example.test", null));
    private DemoPage<DemoLeadResponse> Rows(Guid user, int page = 1, int size = 20, string? stage = null, string? dnd = null) =>
        _store.Records(user, "all", null, null, null, false, page, size, stage, dnd);
    private DemoLeadResponse Report(Guid user, DemoLeadResponse row, bool inaccurate = true) =>
        _store.ReviewField(user, row.Id, new("phone", row.Phone, null, inaccurate, "Wrong business"));

    [Fact]
    public void SalesActivityIsPrivateAndSurvivesRestart()
    {
        var a = Add(_a); var b = Add(_b);
        var updated = _store.UpdateSales(_a, a.Id, new("possible_sale", true, true, false, true, "Follow up after budget approval"));
        Assert.Equal("possible_sale", updated.Sales.Stage);
        Assert.True(updated.Sales.DoNotContact);
        Assert.Equal("new", Rows(_b).Items.Single().Sales.Stage);
        Assert.Equal(404, Assert.Throws<DemoRequestException>(() => _store.UpdateSales(_b, a.Id, new("won", null, null, null, null, null))).StatusCode);
        _store.Dispose(); _store = new DemoStore(_directory, new DemoCatalog(1000));
        var restored = Rows(_a).Items.Single();
        Assert.True(restored.Sales.ContactedOnEmail);
        Assert.True(restored.Sales.ContactedOnPhone);
        Assert.Equal(updated.Sales.Notes, restored.Sales.Notes);
        Assert.False(Rows(_b).Items.Single().Sales.DoNotContact);
    }

    [Fact]
    public void ReportsAreSharedButUniquePerUserAndCanBeWithdrawn()
    {
        var a = Add(_a); var b = Add(_b);
        Report(_a, a); Report(_a, a);
        var seenByB = Rows(_b).Items.Single().FieldQuality["phone"];
        Assert.Equal(1, seenByB.InaccurateReports);
        Assert.False(seenByB.ReportedByMe);
        Report(_b, b);
        Assert.Equal(2, Rows(_a).Items.Single().FieldQuality["phone"].InaccurateReports);
        Report(_a, a, false);
        Assert.Equal(1, Rows(_b).Items.Single().FieldQuality["phone"].InaccurateReports);
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(_directory, "database.json")));
        Assert.All(json.RootElement.GetProperty("fieldReviews").EnumerateArray(), x => Assert.Equal(a.Phone, x.GetProperty("value").GetString()));
    }

    [Fact]
    public void CommunityWarningAndPersonalConfidenceStayDistinct()
    {
        var a = Add(_a); var b = Add(_b);
        _store.ReviewField(_a, a.Id, new("phone", a.Phone, "high", false, null));
        Report(_b, b);
        var c = _store.Register(new("Chris", "chris@example.test", Password));
        var d = _store.Register(new("Dana", "dana@example.test", Password));
        Report(c.Id, Add(c.Id)); Report(d.Id, Add(d.Id));
        var quality = Rows(_a).Items.Single().FieldQuality["phone"];
        Assert.Equal("high", quality.Confidence);
        Assert.Equal(3, quality.InaccurateReports);
        Assert.Equal("low", quality.CommunitySignal);
        Assert.Null(Rows(_b).Items.Single().FieldQuality["phone"].Confidence);
        _store.Dispose(); _store = new DemoStore(_directory, new DemoCatalog(1000));
        Assert.Equal(3, Rows(_a).Items.Single().FieldQuality["phone"].InaccurateReports);
    }

    [Fact]
    public void StaleValuesAndInvalidReviewsAreRejected()
    {
        var a = Add(_a);
        Assert.Equal(409, Assert.Throws<DemoRequestException>(() => _store.ReviewField(_a, a.Id, new("phone", "old value", "low", true, null))).StatusCode);
        Assert.Throws<DemoRequestException>(() => _store.ReviewField(_a, a.Id, new("name", a.Name, "high", true, null)));
        Assert.Throws<DemoRequestException>(() => _store.ReviewField(_a, a.Id, new("phone", a.Phone, "certain", true, null)));
        Assert.Equal(404, Assert.Throws<DemoRequestException>(() => Report(_b, a)).StatusCode);
        Assert.Throws<DemoRequestException>(() => _store.UpdateSales(_a, a.Id, new("invented", null, null, null, null, null)));
        Assert.Throws<DemoRequestException>(() => _store.UpdateSales(_a, a.Id, new(null, null, null, null, null, new string('x', 4001))));
    }

    [Fact]
    public void CorrectedFieldDoesNotInheritReportsAgainstItsPreviousValue()
    {
        var a = Add(_a);
        Report(_a, a);
        _store.Dispose();
        var path = Path.Combine(_directory, "database.json");
        var database = JsonNode.Parse(File.ReadAllText(path))!;
        var business = database["businesses"]!.AsObject().First().Value!;
        business["phone"] = "+1 202 555 0199";
        File.WriteAllText(path, database.ToJsonString());
        _store = new DemoStore(_directory, new DemoCatalog(1000));
        var corrected = Rows(_a).Items.Single();
        Assert.Equal(0, corrected.FieldQuality["phone"].InaccurateReports);
        Assert.False(corrected.FieldQuality["phone"].ReportedByMe);
        using var saved = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(a.Phone, saved.RootElement.GetProperty("fieldReviews")[0].GetProperty("value").GetString());
    }

    [Fact]
    public void FiltersAndDashboardReflectSalesActivityAndDnd()
    {
        var a = Add(_a, "A"); var b = Add(_a, "B"); Add(_a, "C");
        _store.UpdateSales(_a, a.Id, new("possible_sale", true, null, null, false, null));
        _store.UpdateSales(_a, b.Id, new(null, null, null, null, true, null));
        Assert.Equal(1, Rows(_a, stage: "possible_sale", dnd: "exclude").Total);
        Assert.Equal(1, Rows(_a, stage: "uncontacted", dnd: "exclude").Total);
        Assert.Equal(1, Rows(_a, dnd: "only").Total);
        var dashboard = _store.Dashboard(_a);
        Assert.Equal(1, dashboard.PossibleSales);
        Assert.Equal(1, dashboard.ContactedLeads);
        Assert.Equal(1, dashboard.UncontactedLeads);
        Assert.Equal(1, dashboard.DoNotContactLeads);
        Assert.Throws<DemoRequestException>(() => Rows(_a, stage: "bad"));
    }

    [Fact]
    public void ExportIncludesAllPagesHonorsOwnershipDndAndEscapesCsv()
    {
        for (var i = 0; i < 25; i++) Add(_a, $"Business {i}");
        var special = Add(_a, "=SUM(1,2)\"quoted\"");
        var blocked = Add(_a, "Do not call"); Add(_b, "Other account");
        _store.UpdateSales(_a, blocked.Id, new(null, null, null, null, true, null));
        Assert.Equal(20, Rows(_a).Items.Count);
        var csv = _store.ExportSales(_a, "all", null, null, null, false, null, "exclude");
        Assert.Equal(27, csv.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Contains("Business 24", csv);
        Assert.Contains("\"'=SUM(1,2)\"\"quoted\"\"\"", csv);
        Assert.DoesNotContain("Other account", csv);
        Assert.DoesNotContain("Do not call", csv);
        var dnd = _store.ExportSales(_a, "all", null, null, null, false, null, "only");
        Assert.Contains("\"Yes\",\"Yes\"", dnd);
        Assert.Throws<DemoRequestException>(() => _store.ExportSales(_b, "all", special.JobId, null, null, false, null, null));
    }

    [Fact]
    public void ExportCollapsesDuplicateObservationsAndRequiresExplicitSampleOptIn()
    {
        // A large enough local catalog includes duplicate observations of shared places.
        _store.Dispose(); _store = new DemoStore(_directory, new DemoCatalog(1_000_000));
        var job = _store.CreateJob(_a, new(28.6139, 77.209, 5000, 200, "any_lodging"));
        for (var i = 0; i < 200 && _store.Job(_a, job.Id).Status == "fetching"; i++) _store.Work();
        var rows = Rows(_a, size: 200);
        Assert.Throws<DemoRequestException>(() => _store.ExportSales(_a, "all", null, null, null, false, null, null));
        var csv = _store.ExportSales(_a, "all", null, null, null, false, null, null, true);
        var unique = rows.Items.Select(x => (x.Latitude, x.Longitude)).Distinct().Count();
        Assert.Equal(unique + 1, csv.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public void PageInputClampsToExistingPages()
    {
        for (var i = 0; i < 25; i++) Add(_a, $"Business {i}");
        Assert.Equal(2, Rows(_a, 1000).Page);
        Assert.Equal(5, Rows(_a, 1000).Items.Count);
        Assert.Equal(1, Rows(_a, -1).Page);
    }

    public void Dispose() => _store.Dispose();
}
