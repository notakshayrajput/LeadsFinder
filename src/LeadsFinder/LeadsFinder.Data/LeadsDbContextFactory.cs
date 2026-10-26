using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace LeadsFinder.Data
{
    public sealed class LeadsDbContextFactory : IDesignTimeDbContextFactory<LeadsDbContext>
    {
        public LeadsDbContext CreateDbContext(string[] args)
        {
            var connectionString =
          Environment.GetEnvironmentVariable("LEADSFINDER_DB")
          ?? LocalEnvironment.Read().GetValueOrDefault("LEADSFINDER_DB")
          ?? throw new InvalidOperationException("Set LEADSFINDER_DB in the environment or repository-root .env file.");

            var options = new DbContextOptionsBuilder<LeadsDbContext>()
                .UseNpgsql(connectionString, x => x.UseNetTopologySuite())
                .Options;

            return new LeadsDbContext(options);
        }
    }
}
