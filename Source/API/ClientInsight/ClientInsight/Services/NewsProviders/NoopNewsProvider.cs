namespace ClientInsightAPI.Services.NewsProviders;

public sealed class NoopNewsProvider : INewsProvider
{
    public string Name => "noop";

    public Task<IReadOnlyList<ArticleCandidate>> SearchAsync(
        ClientForScan client,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        int maxRecords,
        CancellationToken ct)
        => Task.FromResult<IReadOnlyList<ArticleCandidate>>(Array.Empty<ArticleCandidate>());
}
