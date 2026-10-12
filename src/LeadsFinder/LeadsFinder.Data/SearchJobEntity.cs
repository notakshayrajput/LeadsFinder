namespace LeadsFinder.Data
{
    public sealed class SearchJobEntity
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
        public DateTime ScheduledAtUtc { get; set; }
        public DateTime? StartedAtUtc { get; set; }
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public int RadiusMeters { get; set; }
        public string PlaceType { get; set; } = "any_lodging";
        public string Source { get; set; } = "geofabrik-osm";
        public bool RequirePhone { get; set; }
        public bool RequireEmail { get; set; }
        public bool RequireAddress { get; set; } = true;
        public int TargetCount { get; set; } = 25;
        public string ShortagePolicy { get; set; } = "return_fewer";
        public string Status { get; set; } = "pending";
        public DateTime? FinishedAtUtc { get; set; }
        public int AttemptCount { get; set; }
        public DateTime? LeaseUntilUtc { get; set; }
        public string? LastError { get; set; }
    }

}
