using LeadsFinder.Data;
using LeadsFinder.Worker;
using Microsoft.EntityFrameworkCore;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Services.AddSingleton<ISearchStrategy, GeofabrikSearchStrategy>();
builder.Services.AddSingleton<ISearchStrategy, GoogleMapsScraperSearchStrategy>();
builder.Services.AddHostedService<Worker>();

var connectionString = await LocalDatabase.ResolveAndInitializeAsync(builder.Configuration["LEADSFINDER_DB"]);

builder.Services.AddDbContextFactory<LeadsDbContext>(options =>
    options.UseNpgsql(connectionString, npgsql => npgsql.UseNetTopologySuite()));



var host = builder.Build();
host.Run();
