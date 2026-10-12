namespace LeadsFinder.Worker
{
    public sealed class DemoDirectoryStrategy : ISearchStrategy
    {
        public string Name => "demo-directory";

        public Task<IReadOnlyList<FoundPlace>> SearchAsync(
            SearchJob job,
            IReadOnlySet<string> excludedSourceIds,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<FoundPlace> places =
            [
                new("directory-guest-house-1","Sample Guest House", "3 Example Road",
                job.Latitude, job.Longitude, Name,
                "https://example.com/directory/guest-house")
            ];

            return Task.FromResult<IReadOnlyList<FoundPlace>>(
                places.Where(place => !excludedSourceIds.Contains(place.SourceId)).ToArray());
        }
    }
}
