namespace LeadsFinder.Worker;

public interface ISearchStrategy
{
    string Name { get; }

    Task<IReadOnlyList<FoundPlace>> SearchAsync(
        SearchJob job,
        IReadOnlySet<string> excludedSourceIds,
        CancellationToken cancellationToken);
}
