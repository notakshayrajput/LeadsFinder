using System.Security.Claims;
using System.Threading.RateLimiting;
using LeadsFinder.Api;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddInMemoryCollection(LeadsFinder.Data.LocalEnvironment.Read());
// Explicit host/CLI settings take precedence over the repository's local .env.
builder.Configuration.AddEnvironmentVariables().AddCommandLine(args);
var dataPath = Path.GetFullPath(builder.Configuration["Demo:DataPath"] ?? "App_Data", builder.Environment.ContentRootPath);
Directory.CreateDirectory(dataPath);
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataPath, "keys")));
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
{
    options.Cookie.Name = "leadsfinder.session";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    options.ExpireTimeSpan = TimeSpan.FromDays(7);
    options.SlidingExpiration = true;
    options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = 401; return Task.CompletedTask; };
    options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; };
});
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("auth", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
        { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
builder.Services.AddSingleton(new DemoCatalog(builder.Configuration.GetValue("Demo:CatalogSize", 1_000_000)));
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddSingleton<DemoStore>(services => new DemoStore(dataPath, services.GetRequiredService<DemoCatalog>(),
    builder.Configuration, services.GetRequiredService<TimeProvider>()));
builder.Services.AddHostedService<DemoWorker>();

var app = builder.Build();
// The framework trusts forwarded headers only from loopback proxies by default.
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});
app.Use(async (context, next) =>
{
    // Reject browser writes coming from another origin while permitting same-origin
    // clients and non-browser deployment checks that do not send an Origin header.
    if (context.Request.Path.StartsWithSegments("/api") && !HttpMethods.IsGet(context.Request.Method)
        && !HttpMethods.IsHead(context.Request.Method) && !HttpMethods.IsOptions(context.Request.Method))
    {
        var origin = context.Request.Headers.Origin.ToString();
        var fetchSite = context.Request.Headers["Sec-Fetch-Site"].ToString();
        var expectedOrigin = $"{context.Request.Scheme}://{context.Request.Host}";
        if (fetchSite == "cross-site" || (origin.Length > 0 && !origin.Equals(expectedOrigin, StringComparison.OrdinalIgnoreCase)))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "This request must originate from this application." });
            return;
        }
        if ((HttpMethods.IsPost(context.Request.Method) || HttpMethods.IsPut(context.Request.Method))
            && context.Request.ContentLength > 0 && !context.Request.HasJsonContentType())
        {
            context.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
            await context.Response.WriteAsJsonAsync(new { error = "Send the request as application/json." });
            return;
        }
    }
    try { await next(context); }
    catch (DemoRequestException error)
    {
        context.Response.StatusCode = error.StatusCode;
        await context.Response.WriteAsJsonAsync(new { error = error.Message, code = error.Code });
    }
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.Use(async (context, next) =>
{
    var path = context.Request.Path;
    var publicAuthentication = path.Equals("/api/auth/register", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/api/auth/login", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/api/auth/logout", StringComparison.OrdinalIgnoreCase);
    if (path.StartsWithSegments("/api") && !publicAuthentication && context.User.Identity?.IsAuthenticated == true)
    {
        var store = context.RequestServices.GetRequiredService<DemoStore>();
        var userId = UserId(context.User);
        // Check the live account before model binding, including requests that carry
        // a cookie issued before an administrator blocked or changed the account.
        store.User(userId);
        if (path.StartsWithSegments("/api/admin")) store.RequireAdmin(userId);
    }
    await next(context);
});
app.UseAuthorization();
app.UseRateLimiter();

var auth = app.MapGroup("/api/auth");
auth.MapPost("/register", async (DemoRegisterRequest request, DemoStore store, HttpContext context) =>
{
    var user = store.Register(request);
    await SignIn(context, user);
    return Results.Ok(user);
}).RequireRateLimiting("auth");
auth.MapPost("/login", async (DemoLoginRequest request, DemoStore store, HttpContext context) =>
{
    var user = store.Login(request);
    await SignIn(context, user);
    return Results.Ok(user);
}).RequireRateLimiting("auth");
auth.MapPost("/logout", async (HttpContext context) =>
{
    await context.SignOutAsync();
    return Results.Ok(new { });
});
auth.MapGet("/me", (ClaimsPrincipal user, DemoStore store) => Results.Ok(store.User(UserId(user)))).RequireAuthorization();

var api = app.MapGroup("/api").RequireAuthorization();
api.MapGet("/config", (ClaimsPrincipal user, DemoStore store) => Results.Ok(new
{
    placeTypes = DemoCatalog.PlaceTypes, maxRowsPerJob = 200, catalogSize = store.Dashboard(UserId(user)).CatalogSize,
    mapCenter = new { latitude = 28.6139, longitude = 77.2090 }
}));
api.MapGet("/locations", (string? q) => Results.Ok(DemoCatalog.Locations
    .Where(x => string.IsNullOrWhiteSpace(q) || x.DisplayName.Contains(q.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray()));
api.MapGet("/dashboard", (ClaimsPrincipal user, DemoStore store) => Results.Ok(store.Dashboard(UserId(user))));
api.MapGet("/jobs", (ClaimsPrincipal user, DemoStore store, int? page, int? pageSize) =>
    Results.Ok(store.Jobs(UserId(user), page, pageSize)));
api.MapPost("/jobs", (DemoCreateJobRequest request, ClaimsPrincipal user, DemoStore store) =>
{
    var job = store.CreateJob(UserId(user), request);
    return Results.Created($"/api/jobs/{job.Id}", job);
});
api.MapGet("/jobs/{id:guid}", (Guid id, ClaimsPrincipal user, DemoStore store) => Results.Ok(store.Job(UserId(user), id)));
api.MapPost("/jobs/{id:guid}/enrich", (Guid id, ClaimsPrincipal user, DemoStore store) =>
    Results.Ok(store.Enrich(UserId(user), id)));
api.MapGet("/records", (ClaimsPrincipal user, DemoStore store, string? status, Guid? jobId, string? q,
    string? contact, bool? hasContact, int? page, int? pageSize, string? salesStage, string? dnd) =>
    Results.Ok(store.Records(UserId(user), status, jobId, q, contact, hasContact ?? false, page, pageSize, salesStage, dnd)));
api.MapPatch("/records/{id:guid}/sales", (Guid id, DemoUpdateSalesRequest request, ClaimsPrincipal user, DemoStore store) =>
    Results.Ok(store.UpdateSales(UserId(user), id, request)));
api.MapPut("/records/{id:guid}/quality", (Guid id, DemoReviewFieldRequest request, ClaimsPrincipal user, DemoStore store) =>
    Results.Ok(store.ReviewField(UserId(user), id, request)));
api.MapGet("/records/export", (ClaimsPrincipal user, DemoStore store, string? status, Guid? jobId, string? q,
    string? contact, bool? hasContact, string? salesStage, string? dnd, bool? includeSamples) =>
{
    var csv = store.ExportSales(UserId(user), status, jobId, q, contact, hasContact ?? false, salesStage, dnd, includeSamples ?? false);
    return Results.File(System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(csv)).ToArray(),
        "text/csv; charset=utf-8", $"dynamics-sales-leads-{DateTime.UtcNow:yyyy-MM-dd}.csv");
});
api.MapGet("/jobs/{id:guid}/records", (Guid id, ClaimsPrincipal user, DemoStore store, string? status, string? q,
    string? contact, bool? hasContact, int? page, int? pageSize) =>
    Results.Ok(store.Records(UserId(user), status, id, q, contact, hasContact ?? false, page, pageSize)));
api.MapPost("/records", (DemoCreateRecordRequest request, ClaimsPrincipal user, DemoStore store) =>
{
    var row = store.CreateRecord(UserId(user), request);
    return Results.Created($"/api/jobs/{row.JobId}/records", row);
});

var admin = api.MapGroup("/admin");
admin.MapGet("/overview", (ClaimsPrincipal user, DemoStore store) => Results.Ok(store.AdminOverview(UserId(user))));
admin.MapGet("/users", (ClaimsPrincipal user, DemoStore store, string? q, int? page, int? pageSize) =>
    Results.Ok(store.AdminUsers(UserId(user), q, page, pageSize)));
admin.MapPatch("/users/{id:guid}", (Guid id, DemoUpdateUserRequest request, ClaimsPrincipal user, DemoStore store) =>
    Results.Ok(store.UpdateUser(UserId(user), id, request)));
admin.MapGet("/catalog/batches", (ClaimsPrincipal user, DemoStore store) => Results.Ok(store.GenerationBatches(UserId(user))));
admin.MapPost("/catalog/generate", (DemoGenerateRequest request, ClaimsPrincipal user, DemoStore store) =>
{
    var batch = store.GenerateRows(UserId(user), request);
    return Results.Created("/api/admin/catalog/batches", batch);
});

// An unknown API path must not return the SPA's HTML shell.
app.MapMethods("/api/{**path}", ["GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS"], () => Results.NotFound());
if (Directory.Exists(Path.Combine(app.Environment.ContentRootPath, "wwwroot")))
    app.MapFallbackToFile("index.html");
app.Run();

static Guid UserId(ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
static Task SignIn(HttpContext context, DemoUserResponse user) => context.SignInAsync(
    CookieAuthenticationDefaults.AuthenticationScheme,
    new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
        new Claim(ClaimTypes.Name, user.Name)], CookieAuthenticationDefaults.AuthenticationScheme)),
    new AuthenticationProperties { IsPersistent = true });
