using Dapper;
using Npgsql;

namespace ClientInsightAPI.Services;

public sealed class CompanyFeedService
{
    private readonly NpgsqlDataSource _ds;

    public CompanyFeedService(NpgsqlDataSource ds) => _ds = ds;

    /// <summary>
    /// Returns a company feed grouped by client, ordered by company_clients.company_rank,
    /// with articles from the last N days ordered by llm_summaries.relevance_score (desc).
    ///
    /// NOTE: query.Limit is interpreted as "max articles per client" (clamped 1..500).
    /// </summary>
    public async Task<IReadOnlyList<CompanyClientFeedDto>> GetCompanyFeedAsync(
        Guid companyId,
        CompanyFeedQuery query,
        CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);

        var days = Math.Clamp(query.Days, 1, 365);
        var perClientLimit = Math.Clamp(query.Limit, 1, 500);

        var sql = @"
WITH company_clients_filtered AS (
  SELECT company_id, client_id, company_rank, is_active
  FROM app.company_clients
  WHERE company_id = @companyId
    AND (@activeOnly = false OR is_active = true)
),
rows AS (
  SELECT
    cc.client_id,
    c.name    AS client_name,
    c.website AS client_website,
    c.address AS client_address,
    cc.company_rank,

    a.article_id,
    a.source_language AS article_language,
    a.title,
    ls.summary AS english_summary,
    ls.relevance_score,
    ls.why_it_matters AS reason_for_score,
    ls.conversation_angle,
    a.url,
    a.source_country,
    a.published_at,
    a.social_image_url,

    ROW_NUMBER() OVER (
      PARTITION BY cc.client_id
      ORDER BY
        ls.relevance_score DESC NULLS LAST,
        COALESCE(a.published_at, a.retrieved_at) DESC NULLS LAST,
        a.article_id DESC
    ) AS rn
  FROM company_clients_filtered cc
  JOIN app.clients c
    ON c.client_id = cc.client_id
  JOIN app.llm_summaries ls
    ON ls.client_id = cc.client_id
  JOIN app.articles a
    ON a.article_id = ls.article_id
  WHERE
    COALESCE(a.published_at, a.retrieved_at) >= (now() - (@days || ' days')::interval)
    AND (@minRelevanceScore IS NULL OR ls.relevance_score >= @minRelevanceScore)
)
SELECT
  client_id           AS ClientId,
  client_name         AS ClientName,
  client_website      AS ClientWebsite,
  client_address      AS ClientAddress,
  company_rank        AS CompanyRank,

  article_id          AS ArticleId,
  article_language    AS ArticleLanguage,
  title               AS Title,
  english_summary     AS EnglishSummary,
  relevance_score     AS RelevanceScore,
  reason_for_score    AS ReasonForScore,
  conversation_angle  AS ConversationAngle,
  url                 AS Url,
  source_country      AS SourceCountry,
  published_at        AS PublishedAt,
  social_image_url    AS SocialImageUrl
FROM rows
WHERE rn <= @perClientLimit
ORDER BY
  company_rank ASC NULLS LAST,
  client_name ASC,
  relevance_score DESC NULLS LAST,
  published_at DESC NULLS LAST,
  article_id DESC;
";

        var flat = await conn.QueryAsync<CompanyFeedRow>(new CommandDefinition(
            sql,
            new
            {
                companyId,
                days,
                perClientLimit,
                activeOnly = query.ActiveOnly,
                minRelevanceScore = query.MinRelevanceScore
            },
            cancellationToken: ct));

        // Group into { client -> articles }
        var byClient = new Dictionary<Guid, CompanyClientFeedDto>();
        foreach (var r in flat)
        {
            if (!byClient.TryGetValue(r.ClientId, out var client))
            {
                client = new CompanyClientFeedDto
                {
                    ClientId = r.ClientId,
                    ClientName = r.ClientName ?? "",
                    ClientWebsite = r.ClientWebsite,
                    ClientAddress = r.ClientAddress,
                    CompanyRank = r.CompanyRank,
                    Articles = new List<CompanyClientFeedArticleDto>()
                };
                byClient.Add(r.ClientId, client);
            }

            // Safety: if something weird returns a row without an article id, skip it
            if (r.ArticleId is null) continue;

            client.Articles.Add(new CompanyClientFeedArticleDto
            {
                ArticleId = r.ArticleId.Value,
                ArticleLanguage = r.ArticleLanguage,
                Title = r.Title,
                EnglishSummary = r.EnglishSummary,
                RelevanceScore = r.RelevanceScore,
                ReasonForScore = r.ReasonForScore,
                ConversationAngle = r.ConversationAngle,
                Url = r.Url ?? "",
                SourceCountry = r.SourceCountry,
                PublishedAt = r.PublishedAt,
                SocialImageUrl = r.SocialImageUrl
            });
        }

        // Ensure client ordering (SQL should already do this, but keep it deterministic)
        var result = byClient.Values
            .OrderBy(x => x.CompanyRank.HasValue ? 0 : 1)
            .ThenBy(x => x.CompanyRank)
            .ThenBy(x => x.ClientName)
            .ToList();

        return result;
    }

    // Flat row used only for query mapping
    private sealed class CompanyFeedRow
    {
        public Guid ClientId { get; set; }
        public string? ClientName { get; set; }
        public string? ClientWebsite { get; set; }
        public string? ClientAddress { get; set; }
        public int? CompanyRank { get; set; }

        public long? ArticleId { get; set; }
        public string? ArticleLanguage { get; set; }
        public string? Title { get; set; }
        public string? EnglishSummary { get; set; }
        public decimal? RelevanceScore { get; set; }
        public string? ReasonForScore { get; set; }
        public string? ConversationAngle { get; set; }
        public string? Url { get; set; }
        public string? SourceCountry { get; set; }
        public DateTimeOffset? PublishedAt { get; set; }
        public string? SocialImageUrl { get; set; }
    }
}

public sealed class CompanyFeedQuery
{
    /// <summary>How far back to look (default 30 days).</summary>
    public int Days { get; set; } = 30;

    /// <summary>
    /// Max number of articles returned per client (default 50).
    /// (Kept name 'Limit' to avoid breaking callers.)
    /// </summary>
    public int Limit { get; set; } = 50;

    /// <summary>If true, only use active company_clients mappings (default true).</summary>
    public bool ActiveOnly { get; set; } = true;

    /// <summary>Optional: only include summaries with relevance_score >= MinRelevanceScore.</summary>
    public decimal? MinRelevanceScore { get; set; }
}

/// <summary>Top-level grouped result: one per client.</summary>
public sealed class CompanyClientFeedDto
{
    public Guid ClientId { get; set; }
    public string ClientName { get; set; } = "";
    public string? ClientWebsite { get; set; }
    public string? ClientAddress { get; set; }
    public int? CompanyRank { get; set; }

    public List<CompanyClientFeedArticleDto> Articles { get; set; } = new();
}

/// <summary>Article info per client (sorted by relevance_score desc).</summary>
public sealed class CompanyClientFeedArticleDto
{
    public long ArticleId { get; set; }
    public string? ArticleLanguage { get; set; }
    public string? Title { get; set; }
    public string? EnglishSummary { get; set; }
    public decimal? RelevanceScore { get; set; }
    public string? ReasonForScore { get; set; }
    public string? ConversationAngle { get; set; }
    public string Url { get; set; } = "";
    public string? SourceCountry { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public string? SocialImageUrl { get; set; }
}
