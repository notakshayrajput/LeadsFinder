using NetTopologySuite.Geometries;
namespace LeadsFinder.Data
{
    public sealed class SourceRecord
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid SearchJobId { get; set; }

        public string Strategy { get; set; } = "";
        public string SourceId { get; set; } = "";
        public string SourceLink { get; set; } = "";
        public string Name { get; set; } = "";
        public string Address { get; set; } = "";
        public Point Location { get; set; } = null!;

        public string? Phone { get; set; }
        public string? Email { get; set; }
        public string? Website { get; set; }
        public string RawJson { get; set; } = "{}";
        public DateTime FetchedAtUtc { get; set; } = DateTime.UtcNow;
    }
}
