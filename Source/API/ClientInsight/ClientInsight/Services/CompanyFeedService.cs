using Dapper;
using Npgsql;

namespace ClientInsightAPI.Services;

public sealed class CompanyFeedService
{
    private readonly NpgsqlDataSource _ds;

    public CompanyFeedService(NpgsqlDataSource ds) => _ds = ds;

    public async Task<IReadOnlyList<CompanyFeedItemDto>> GetCompanyFeedAsync(
        Guid companyId,
        CompanyFeedQuery query,
        CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);

        var days = Math.Clamp(query.Days, 1, 365);
        var limit = Math.Clamp(query.Limit, 1, 500);

        // We want a single "best" client match per article for this company to avoid duplicates.
        // DISTINCT ON (article_id) picks the best match according to the ORDER BY inside the CTE.
        //
        // Sort preference for choosing the best match:
        // 1) highest match_score
        // 2) lowest company_rank (if present)
        // 3) most recent published/retrieved
        var sql = @"
WITH company_clients_filtered AS (
  SELECT company_id, client_id, company_rank, is_active
  FROM app.company_clients
  WHERE company_id = @companyId
    AND (@activeOnly = false OR is_active = true)
),
joined AS (
  SELECT
    a.article_id,
    a.url,
    a.canonical_url,
    a.title,

    a.source,
    a.published_at,
    a.retrieved_at,

    cc.client_id,
    c.name AS client_name,
    cc.company_rank,
    cc.is_active,

    ca.match_score,
    ca.matched_on,

    (s.client_id IS NOT NULL) AS has_summary
  FROM company_clients_filtered cc
  JOIN app.client_articles ca
    ON ca.client_id = cc.client_id
  JOIN app.articles a
    ON a.article_id = ca.article_id
  JOIN app.clients c
    ON c.client_id = cc.client_id
  LEFT JOIN app.llm_summaries s
    ON s.client_id = ca.client_id
   AND s.article_id = a.article_id
  WHERE
    COALESCE(a.published_at, a.retrieved_at) >= (now() - (@days || ' days')::interval)
    AND (@minScore IS NULL OR ca.match_score >= @minScore)
),
best_per_article AS (
  SELECT DISTINCT ON (article_id)
    *
  FROM joined
  ORDER BY
    article_id,
    match_score DESC NULLS LAST,
    company_rank ASC NULLS LAST,
    COALESCE(published_at, retrieved_at) DESC NULLS LAST
)
SELECT
  article_id        AS ArticleId,
  url               AS Url,
  canonical_url     AS CanonicalUrl,
  title             AS Title,
  source            AS Source,
  published_at      AS PublishedAt,
  retrieved_at      AS RetrievedAt,

  client_id         AS ClientId,
  client_name       AS ClientName,
  company_rank      AS CompanyRank,
  is_active         AS IsActive,

  match_score       AS MatchScore,
  matched_on        AS MatchedOn,

  has_summary       AS HasSummary
FROM best_per_article
ORDER BY
  COALESCE(published_at, retrieved_at) DESC NULLS LAST,
  match_score DESC NULLS LAST
LIMIT @limit;
";

        var rows = await conn.QueryAsync<CompanyFeedItemDto>(new CommandDefinition(
            sql,
            new
            {
                companyId,
                days,
                limit,
                activeOnly = query.ActiveOnly,
                minScore = query.MinScore
            },
            cancellationToken: ct));

        return rows.AsList();
    }
}

public sealed class CompanyFeedQuery
{
    /// <summary>How far back to look (default 7 days).</summary>
    public int Days { get; set; } = 7;

    /// <summary>Max number of feed items returned (default 100).</summary>
    public int Limit { get; set; } = 100;

    /// <summary>If true, only use active company_clients mappings (default true).</summary>
    public bool ActiveOnly { get; set; } = true;

    /// <summary>Optional: only include matches with match_score >= MinScore.</summary>
    public decimal? MinScore { get; set; }
}

public sealed class CompanyFeedItemDto
{
    public long ArticleId { get; set; }
    public string Url { get; set; } = "";
    public string? CanonicalUrl { get; set; }
    public string? Title { get; set; }
    public string? Source { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public DateTimeOffset RetrievedAt { get; set; }

    public Guid ClientId { get; set; }
    public string ClientName { get; set; } = "";
    public int? CompanyRank { get; set; }
    public bool IsActive { get; set; }

    public decimal? MatchScore { get; set; }
    public string? MatchedOn { get; set; }

    public bool HasSummary { get; set; }
}
