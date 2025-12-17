namespace ClientInsightAPI.Services.NewsProviders;

public sealed class NoopNewsProvider : INewsProvider
{
    public string Name => "noop";

    public Task<IReadOnlyList<ArticleCandidate>> SearchAsync(
        ClientForScan client,
        DateTimeOffset fromUtc,
        int limit,
        CancellationToken ct)
        => Task.FromResult<IReadOnlyList<ArticleCandidate>>(Array.Empty<ArticleCandidate>());
}
