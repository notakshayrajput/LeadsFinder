using LeadsFinder.Data;
using LeadsFinder.Worker;
using Microsoft.EntityFrameworkCore;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton<ISearchStrategy, GeofabrikSearchStrategy>();
builder.Services.AddSingleton<ISearchStrategy, GoogleMapsScraperSearchStrategy>();
builder.Services.AddHostedService<Worker>();

var connectionString = builder.Configuration["LEADSFINDER_DB"]
    ?? throw new InvalidOperationException("Set LEADSFINDER_DB.");

builder.Services.AddDbContextFactory<LeadsDbContext>(options =>
    options.UseNpgsql(connectionString, npgsql => npgsql.UseNetTopologySuite()));



var host = builder.Build();
host.Run();
