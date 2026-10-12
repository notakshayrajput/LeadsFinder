using Microsoft.EntityFrameworkCore;

namespace LeadsFinder.Data
{
    public sealed class LeadsDbContext(DbContextOptions<LeadsDbContext> options) : DbContext(options)
    {
        public DbSet<SourceRecord> SourceRecords => Set<SourceRecord>();

        public DbSet<SearchJobEntity> SearchJobs => Set<SearchJobEntity>();
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.HasPostgresExtension("postgis");

            modelBuilder.Entity<SourceRecord>(entity =>
            {
                entity.ToTable("source_records");
                entity.HasKey(x => x.Id);

                entity.Property(x => x.Location)
                    .HasColumnType("geography (point)");

                entity.Property(x => x.Strategy).HasMaxLength(50).IsRequired();
                entity.Property(x => x.SourceId).HasMaxLength(150).IsRequired();
                entity.Property(x => x.SourceLink).HasMaxLength(1000).IsRequired();
                entity.Property(x => x.Name).HasMaxLength(300).IsRequired();
                entity.Property(x => x.Address).HasMaxLength(1000).IsRequired();

                entity.HasIndex(x => new { x.Strategy, x.SourceId })
                    .IsUnique();
                entity.HasIndex(x => x.SearchJobId);

                entity.HasIndex(x => x.Location).HasMethod("gist");
            });

            modelBuilder.Entity<SearchJobEntity>(entity =>
            {
                entity.ToTable("search_jobs");
                entity.HasKey(x => x.Id);
                entity.Property(x => x.Status).HasMaxLength(20).IsRequired();
                entity.Property(x => x.PlaceType).HasMaxLength(40).IsRequired();
                entity.Property(x => x.Source).HasMaxLength(50).IsRequired();
                entity.Property(x => x.ShortagePolicy).HasMaxLength(30).IsRequired();
                entity.HasIndex(x => new { x.Status, x.ScheduledAtUtc });
                entity.Property(x => x.LastError).HasMaxLength(1000);
            });
        }
    }
}
