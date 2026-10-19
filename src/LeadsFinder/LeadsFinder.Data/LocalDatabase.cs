using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace LeadsFinder.Data;

// Development fallback. An explicit LEADSFINDER_DB always takes precedence.
public static class LocalDatabase
{
    public const string DefaultConnectionString =
        "Host=127.0.0.1;Port=55432;Database=leadsfinder;Username=leadsfinder;Password=devpassword";

    public static async Task<string> ResolveAndInitializeAsync(string? configured, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return configured;

        if (!await CanConnectAsync(cancellationToken))
        {
            var compose = FindComposeFile();
            var docker = FindDockerExecutable();
            var startInfo = new ProcessStartInfo(docker)
            {
                ArgumentList = { "compose", "-f", compose, "up", "-d", "db" },
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            if (Path.IsPathRooted(docker))
                startInfo.Environment["PATH"] = Path.GetDirectoryName(docker) + Path.PathSeparator +
                    startInfo.Environment["PATH"];
            using var process = new Process
            {
                StartInfo = startInfo
            };
            try { process.Start(); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
            {
                throw new InvalidOperationException(
                    "The default LeadsFinder database is unavailable. Install/start Docker Desktop, or set LEADSFINDER_DB to a PostgreSQL/PostGIS database.", ex);
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            await stdout;
            if (process.ExitCode != 0)
            {
                var error = await stderr;
                throw new InvalidOperationException("Could not start the default database: " + error[..Math.Min(error.Length, 500)]);
            }
            await stderr;
            for (var attempt = 0; attempt < 30 && !await CanConnectAsync(cancellationToken); attempt++)
                await Task.Delay(1000, cancellationToken);
            if (!await CanConnectAsync(cancellationToken))
                throw new InvalidOperationException("Default database did not become ready on port 55432.");
        }

        var options = new DbContextOptionsBuilder<LeadsDbContext>()
            .UseNpgsql(DefaultConnectionString, x => x.UseNetTopologySuite()).Options;
        await using var db = new LeadsDbContext(options);
        await db.Database.MigrateAsync(cancellationToken);
        return DefaultConnectionString;
    }

    private static async Task<bool> CanConnectAsync(CancellationToken cancellationToken)
    {
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(DefaultConnectionString) { Timeout = 2 };
            await using var connection = new NpgsqlConnection(builder.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            return true;
        }
        catch (NpgsqlException) { return false; }
        catch (TimeoutException) { return false; }
    }

    private static string FindComposeFile()
    {
        foreach (var origin in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
            for (var directory = new DirectoryInfo(origin); directory is not null; directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, "compose.yaml");
                if (File.Exists(candidate)) return candidate;
            }
        throw new InvalidOperationException("Could not locate compose.yaml for the default database.");
    }

    private static string FindDockerExecutable()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new[]
        {
            Path.Combine(localAppData, "Programs", "DockerDesktop", "resources", "bin", "docker.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Docker", "Docker", "resources", "bin", "docker.exe")
        };
        return candidates.FirstOrDefault(File.Exists) ?? "docker";
    }
}
