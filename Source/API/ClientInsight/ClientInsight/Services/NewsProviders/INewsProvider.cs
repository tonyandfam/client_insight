namespace ClientInsightAPI.Services.NewsProviders;

public interface INewsProvider
{
    string Name { get; }

    Task<IReadOnlyList<ArticleCandidate>> SearchAsync(
        ClientForScan client,
        DateTimeOffset fromUtc,
        int limit,
        CancellationToken ct);
}

public sealed class ClientForScan
{
    public Guid ClientId { get; set; }
    public string Name { get; set; } = "";
    public string? Website { get; set; }
    public string? Address { get; set; }
}

public sealed class ArticleCandidate
{
    public string Url { get; set; } = "";
    public string? Title { get; set; }
    public string? Snippet { get; set; }
    public string? Source { get; set; }
    public DateTimeOffset? PublishedAtUtc { get; set; }
    public decimal? MatchScore { get; set; }
    public string? MatchedOn { get; set; }
    public string? RawJson { get; set; } // optional: store provider payload
}
