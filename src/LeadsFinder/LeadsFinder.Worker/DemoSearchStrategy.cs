namespace LeadsFinder.Worker
{
    public sealed class DemoSearchStrategy : ISearchStrategy
    {
        public string Name => "demo";

        public Task<IReadOnlyList<FoundPlace>> SearchAsync(
            SearchJob job,
            IReadOnlySet<string> excludedSourceIds,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<FoundPlace> places =
            [
                new("demo-hotel-1","Demo Hotel", "1 Example Road",
                job.Latitude, job.Longitude, Name,
                "https://example.com/demo-hotel"),
            new("demo-bnb1","Demo B&B", "2 Example Road",
                job.Latitude, job.Longitude, Name,
                "https://example.com/demo-bnb")
            ];

            return Task.FromResult<IReadOnlyList<FoundPlace>>(
                places.Where(place => !excludedSourceIds.Contains(place.SourceId)).ToArray());
        }
    }
}
